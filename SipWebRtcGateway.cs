using DnsClient.Internal;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Net;
using System.Text.Json;
using WebSocketSharp.Server;

namespace ConsoleApp1;

public partial class SipWebRtcGateway
{
    private WebSocketServer _webSocketServer;
    private Dictionary<string, SIPTransport> _sipTransports = new();
    private Dictionary<string, RTCPeerConnection> _webRtcConnections = new();
    private Dictionary<string, SIPUserAgent> _sipCalls = new();
    private Dictionary<string, WebSocketBehavior1> _webSocketClients = new();
    private Dictionary<string, VoIPMediaSession> _mediaSessions = new();
    private ILogger<SipWebRtcGateway> _logger;

    public SipWebRtcGateway(ILogger<SipWebRtcGateway> logger)
    {
        _logger = logger;
    }

    public async Task Start()
    {

        // Start WebSocket server for browser clients
        _webSocketServer = new WebSocketServer("ws://localhost:8080");

        _webSocketServer.WebSocketServices.AddService<WebSocketBehavior1>("/sip", webSocketBehavior =>
        {
            webSocketBehavior.HandleWebSocketMessage = HandleWebSocketMessage;
            webSocketBehavior.OnClientConnected = OnClientConnected;
            webSocketBehavior.OnClientDisconnected = OnClientDisconnected;
        });

        _webSocketServer.Start();
        _logger.LogInformation("SIP-WebRTC Gateway started on ws://localhost:8080/sip");
    }

    private void OnClientConnected(string sessionId, WebSocketBehavior1 client)
    {
        _webSocketClients[sessionId] = client;
    }

    private void OnClientDisconnected(string sessionId)
    {
        _webSocketClients.Remove(sessionId);

        // Clean up associated connections
        if (_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? value))
        {
            value.close();
            _webRtcConnections.Remove(sessionId);
        }

        if (_sipCalls.TryGetValue(sessionId, out SIPUserAgent? value2))
        {
            value2.Hangup();
            _sipCalls.Remove(sessionId);
        }
    }

    private async Task OnSipRequest(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        if (sipRequest.Method == SIPMethodsEnum.INVITE)
        {
            _logger.LogInformation($"Incoming SIP call from {sipRequest.Header.From.FromURI}");

            // Create WebRTC peer connection for this SIP call
            var sessionId = Guid.NewGuid().ToString();
            var peerConnection = await CreateWebRtcPeerConnection(sessionId);

            var sipTransport = new SIPTransport();
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            sipTransport.AddSIPChannel(new SIPUDPChannel(endpoint));

            _logger.LogInformation($"Port: {endpoint.Port}");

            // Create SIP user agent
            var sipUserAgent = new SIPUserAgent(sipTransport, null);
            _sipCalls[sessionId] = sipUserAgent;

            // Create media session for the call
            var mediaSession = new VoIPMediaSession();

            // Accept the SIP call
            var uas = sipUserAgent.AcceptCall(sipRequest);
            await sipUserAgent.Answer(uas, mediaSession);

            // Set up RTP bridging from SIP to WebRTC
            mediaSession.OnRtpPacketReceived += (rep, media, rtpPkt) =>
            {
                if (_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? value))
                {
                    value.SendRtpRaw(media, rtpPkt.Payload,
                        rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
                }
            };

            // Notify any connected browser clients about incoming call
            await NotifyBrowserClient(sessionId, "incoming-call", new
            {
                from = sipRequest.Header.From.FromURI.ToString(),
                sessionId = sessionId
            });
        }
    }

    private async Task<RTCPeerConnection> CreateWebRtcPeerConnection(string sessionId)
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "turn:172.27.200.242:3478", username = "username1", credential ="password1"  }
            }
        };

        var peerConnection = new RTCPeerConnection(config);
        _webRtcConnections[sessionId] = peerConnection;

        // Add audio track
        var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, new List<SDPAudioVideoMediaFormat>
        {
            new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU)
        });
        var videoTack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, new List<SDPAudioVideoMediaFormat>
        {
            new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.H263)
        });
        peerConnection.addTrack(audioTrack);

        // Bridge RTP from WebRTC to SIP
        peerConnection.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (_sipCalls.TryGetValue(sessionId, out SIPUserAgent? value) && value.MediaSession != null)
            {
                if (_sipCalls[sessionId].MediaSession is VoIPMediaSession mediaSession)
                {
                    mediaSession.SendRtpRaw(media, rtpPkt.Payload,
                        rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
                }
            }
        };

        // Handle connection state changes
        peerConnection.onconnectionstatechange += (state) =>
        {
            _logger.LogInformation($"WebRTC connection state changed to {state} for session {sessionId}");
        };

        return peerConnection;
    }

    private async void HandleWebSocketMessage(string sessionId, string message)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<WebSocketMessage>(message);
            _logger.LogInformation($"Received WebSocket message: {msg.Type} from session {sessionId}");

            switch (msg.Type)
            {
                case "offer":
                    await HandleWebRtcOffer(sessionId, msg.Data);
                    break;
                case "answer":
                    await HandleWebRtcAnswer(sessionId, msg.Data);
                    break;
                case "ice-candidate":
                    await HandleIceCandidate(sessionId, msg.Data);
                    break;
                case "make-call":
                    await InitiateSipCall(sessionId, (string)msg.Data);
                    break;
                case "hang-up":
                    await HandleHangUp(sessionId);
                    break;
                case "accept-call":
                    await HandleWebRtcOffer(sessionId, msg.Data);
                    break;

                case "reject-call":
                    await HandleRejectCall(sessionId);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Error handling WebSocket message: {ex.Message}");
        }
    }

    private async Task HandleWebRtcOffer(string sessionId, object data)
    {
        try
        {
            var offerJson = JsonSerializer.Serialize(data);
            var offer = JsonSerializer.Deserialize<RTCSessionDescriptionInit>(offerJson);

            if (_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? peerConnection))
            {

                // Set remote description (offer from browser)
                peerConnection.setRemoteDescription(offer);

                // Create answer
                var answer = peerConnection.createAnswer();
                await peerConnection.setLocalDescription(answer);

                // Send answer back to browser
                await NotifyBrowserClient(sessionId, "answer", answer);

                _logger.LogInformation($"Processed WebRTC offer and sent answer for session {sessionId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Error handling WebRTC offer: {ex.Message}");
        }
    }

    private async Task HandleWebRtcAnswer(string sessionId, object data)
    {
        try
        {
            var answerJson = JsonSerializer.Serialize(data);
            var answer = JsonSerializer.Deserialize<RTCSessionDescriptionInit>(answerJson);

            if (_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? peerConnection))
            {
                peerConnection.setRemoteDescription(answer);

                _logger.LogInformation($"Processed WebRTC answer for session {sessionId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Error handling WebRTC answer: {ex.Message}");
        }
    }

    private async Task HandleIceCandidate(string sessionId, object data)
    {
        try
        {
            var candidateJson = JsonSerializer.Serialize(data);
            var candidate = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson);

            if (_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? peerConnection))
            {
                peerConnection.addIceCandidate(candidate);

                _logger.LogInformation($"Added ICE candidate for session {sessionId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Error handling ICE candidate: {ex.Message}");
        }
    }

    private async Task HandleHangUp(string sessionId)
    {
        try
        {
            // Close WebRTC connection
            if (_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? value))
            {
                value.close();
                _webRtcConnections.Remove(sessionId);
            }

            // Hang up SIP call
            if (_sipCalls.TryGetValue(sessionId, out SIPUserAgent? value2))
            {
                value2.Hangup();
                _sipCalls.Remove(sessionId);
            }

            _logger.LogInformation($"Hung up call for session {sessionId}");
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Error hanging up call: {ex.Message}");
        }
    }

    private async Task InitiateSipCall(string sessionId, string sipUri)
    {
        try
        {
            _logger.LogInformation($"Initiating SIP call to {sipUri} for session {sessionId}");

            var sipUserAgent = new SIPUserAgent(_sipTransport, null);
            _sipCalls[sessionId] = sipUserAgent;

            // Create or get existing WebRTC peer connection
            RTCPeerConnection peerConnection;
            if (!_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? value))
            {
                peerConnection = await CreateWebRtcPeerConnection(sessionId);
            }
            else
            {
                peerConnection = value;
            }

            // Create WebRTC offer
            var offer = peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer);

            // Send offer to browser
            await NotifyBrowserClient(sessionId, "offer", offer);

            // Create media session
            var mediaSession = new VoIPMediaSession();

            // Set up RTP bridging from SIP to WebRTC
            mediaSession.OnRtpPacketReceived += (rep, media, rtpPkt) =>
            {
                if (_webRtcConnections.TryGetValue(sessionId, out RTCPeerConnection? value))
                {
                    value.SendRtpRaw(media, rtpPkt.Payload,
                        rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
                }
            };

            // Initiate SIP call
            var callResult = await sipUserAgent.Call(sipUri, null, null, mediaSession);

            if (callResult)
            {
                _logger.LogInformation($"SIP call successfully initiated to {sipUri}");
            }
            else
            {
                _logger.LogInformation($"Failed to initiate SIP call to {sipUri}");
                await NotifyBrowserClient(sessionId, "call-failed", $"Failed to call {sipUri}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Error initiating SIP call: {ex.Message}");
            await NotifyBrowserClient(sessionId, "call-failed", ex.Message);
        }
    }

    private async Task HandleRejectCall(string sessionId)
    {
        if (_sipCalls.TryGetValue(sessionId, out var ua))
        {
            ua.Hangup();
            _sipCalls.Remove(sessionId);
        }

        if (_webRtcConnections.TryGetValue(sessionId, out var pc))
        {
            pc.close();
            _webRtcConnections.Remove(sessionId);
        }

        _logger.LogInformation($"Call from SIP rejected by browser, session {sessionId}");
    }

    private async Task NotifyBrowserClient(string sessionId, string messageType, object data)
    {
        try
        {
            if (_webSocketClients.TryGetValue(sessionId, out WebSocketBehavior1? value))
            {
                var message = new WebSocketMessage
                {
                    Type = messageType,
                    Data = data
                };
                value.SendMessage(message);
            }
            else
            {
                // Broadcast to all connected clients if no specific session
                var message = new WebSocketMessage
                {
                    Type = messageType,
                    Data = data,
                    SessionId = sessionId
                };

                foreach (var client in _webSocketClients.Values)
                {
                    client.SendMessage(message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation($"Error notifying browser client: {ex.Message}");
        }
    }

    public void Stop()
    {
        _webSocketServer?.Stop();

        // Clean up all connections
        foreach (var connection in _webRtcConnections.Values)
        {
            connection.close();
        }
        _webRtcConnections.Clear();

        foreach (var call in _sipCalls.Values)
        {
            call.Hangup();
        }
        _sipCalls.Clear();

        _logger.LogInformation("SIP-WebRTC Gateway stopped");
    }
}
