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
    private WebSocketServer? _webSocketServer;
    private Dictionary<string, SIPTransport> _sipTransports = new();
    private Dictionary<string, RTCPeerConnection> _webRtcConnections = new();
    private Dictionary<string, SIPUserAgent> _sipCalls = new();
    private Dictionary<string, CustomWebSocketBehavior> _webSocketClients = new();
    private Dictionary<string, VoIPMediaSession> _mediaSessions = new();
    private Dictionary<string, CallSession> _callSessions = new();
    private Dictionary<string, CallBridge> _callBridges = new();
    private Dictionary<string, string> _sessionToBridge = new(); // Maps session ID to bridge ID
    protected ILogger<SipWebRtcGateway> _logger;
    protected ILoggerFactory _loggerFactory;

    public SipWebRtcGateway(ILogger<SipWebRtcGateway> logger, ILoggerFactory factory)
    {
        _logger = logger;
        _loggerFactory = factory;
    }

    public async Task Start()
    {
        // Start WebSocket server for browser clients
        _webSocketServer = new WebSocketServer("ws://localhost:8080");

        _webSocketServer.WebSocketServices.AddService<CustomWebSocketBehavior>("/sip", webSocketBehavior =>
        {
            webSocketBehavior.HandleWebSocketMessage = HandleWebSocketMessage;
            webSocketBehavior.OnClientConnected = OnClientConnected;
            webSocketBehavior.OnClientDisconnected = OnClientDisconnected;
        });

        _webSocketServer.Start();
        _logger.LogInformation("SIP-WebRTC Gateway started on ws://localhost:8080/sip");
    }

    private void OnClientConnected(string sessionId, CustomWebSocketBehavior client)
    {
        _webSocketClients[sessionId] = client;
        
        // Create SIP transport for this session
        var sipTransport = CreateSipTransport(sessionId);
        _sipTransports[sessionId] = sipTransport;
        
        _logger.LogInformation($"Client connected with session ID: {sessionId}, SIP transport created");
    }

    private SIPTransport CreateSipTransport(string sessionId)
    {
        var sipTransport = new SIPTransport();
        IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
        sipTransport.AddSIPChannel(new SIPUDPChannel(endpoint));
        
        // Register SIP request handler for this transport
        //sipTransport.SIPTransportRequestReceived += async (localSIPEndPoint, remoteEndPoint, sipRequest) =>
        //{
        //    await OnSipRequest(sessionId, localSIPEndPoint, remoteEndPoint, sipRequest);
        //};
        
        _logger.LogInformation($"Created SIP transport for session {sessionId} on port {endpoint.Port}");
        return sipTransport;
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

        if (_sipTransports.TryGetValue(sessionId, out SIPTransport? transport))
        {
            transport.Shutdown();
            _sipTransports.Remove(sessionId);
        }

        if (_callSessions.TryGetValue(sessionId, out CallSession? callSession))
        {
            callSession.SipUserAgent?.Hangup();
            callSession.WebRtcPeer?.close();
            _callSessions.Remove(sessionId);
        }

        // Clean up call bridge if this session is part of one
        if (_sessionToBridge.TryGetValue(sessionId, out string? bridgeId))
        {
            if (_callBridges.TryGetValue(bridgeId, out CallBridge? bridge))
            {
                bridge.Hangup();
                _callBridges.Remove(bridgeId);
            }
            _sessionToBridge.Remove(sessionId);
        }

        _logger.LogInformation($"Client disconnected: {sessionId}, cleaned up all resources");
    }

    private async Task OnSipRequest(string sessionId, SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        if (sipRequest.Method == SIPMethodsEnum.INVITE)
        {
            _logger.LogInformation($"Incoming SIP call from {sipRequest.Header.From.FromURI} to session {sessionId}");
        }
    }

    private async Task HandleAliceToBobCall(string aliceSessionId, string bobSessionId)
    {
        try
        {
            _logger.LogInformation($"Creating Alice to Bob call bridge: {aliceSessionId} -> {bobSessionId}");

            // Get SIP transports for both sessions
            if (!_sipTransports.TryGetValue(aliceSessionId, out SIPTransport? aliceTransport) ||
                !_sipTransports.TryGetValue(bobSessionId, out SIPTransport? bobTransport))
            {
                _logger.LogError($"Missing SIP transport for Alice ({aliceSessionId}) or Bob ({bobSessionId})");
                return;
            }

            // Create call bridge
            var bridge = new CallBridge(_loggerFactory.CreateLogger<CallBridge>());
            var bridgeCreated = await bridge.CreateBridge(aliceSessionId, bobSessionId, aliceTransport, bobTransport);
            
            if (bridgeCreated)
            {
                _callBridges[bridge.BridgeId] = bridge;
                _sessionToBridge[aliceSessionId] = bridge.BridgeId;
                _sessionToBridge[bobSessionId] = bridge.BridgeId;

                // Notify both clients about the incoming call
                await NotifyBrowserClient(aliceSessionId, "bridge-call", new
                {
                    bridgeId = bridge.BridgeId,
                    targetSessionId = bobSessionId,
                    isInitiator = true
                });

                await NotifyBrowserClient(bobSessionId, "bridge-call", new
                {
                    bridgeId = bridge.BridgeId,
                    targetSessionId = aliceSessionId,
                    isInitiator = false,
                    from = SessionIdToFromUri(bobSessionId)
                });

                _logger.LogInformation($"Alice to Bob call bridge {bridge.BridgeId} created successfully");

                bridge.InitiateCall();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error creating Alice to Bob call bridge");
        }
    }

    private string? ExtractSessionIdFromUri(string uri)
    {
        // Extract session ID from URI like "sip:sessionId@domain.com"
        try
        {
            var uriParts = uri.Split('@');
            if (uriParts.Length > 0)
            {
                var userPart = uriParts[0];
                if (userPart.StartsWith("sip:"))
                {
                    return userPart[4..];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Error extracting session ID from URI {uri}: {ex.Message}");
        }
        return null;
    }

    private string? SessionIdToFromUri(string sessionId)
    {
        // Convert session ID to SIP URI format
        if (_sipTransports.TryGetValue(sessionId, out SIPTransport? transport))
        {
            var domain = $"{transport.GetSIPChannels().First().ListeningEndPoint.Address}:{transport.GetSIPChannels().First().ListeningEndPoint.Port}";
            return $"sip:{sessionId}@{domain}";
        }
        else
        {
            _logger.LogWarning($"No SIP transport found for session ID {sessionId}");
            return null;
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
            if (_mediaSessions.TryGetValue(sessionId, out VoIPMediaSession? mediaSession))
            {
                mediaSession.SendRtpRaw(media, rtpPkt.Payload,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
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
                case "bridge-offer":
                    await HandleBridgeOffer(sessionId, msg.Data);
                    break;
                case "bridge-answer":
                    await HandleBridgeAnswer(sessionId, msg.Data);
                    break;
                case "bridge-ice-candidate":
                    await HandleBridgeIceCandidate(sessionId, msg.Data);
                    break;
                case "accept-bridge-call":
                    await HandleAcceptBridgeCall(sessionId, msg.Data);
                    break;
                case "reject-bridge-call":
                    await HandleRejectBridgeCall(sessionId);
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

    private async Task HandleBridgeOffer(string sessionId, object data)
    {
        try
        {
            if (_sessionToBridge.TryGetValue(sessionId, out string? bridgeId) &&
                _callBridges.TryGetValue(bridgeId, out CallBridge? bridge))
            {
                var offerJson = JsonSerializer.Serialize(data);
                var offer = JsonSerializer.Deserialize<RTCSessionDescriptionInit>(offerJson);
                
                // Determine if this is Alice or Bob based on the session ID
                bool isAlice = sessionId == bridge.AliceSessionId;
                await bridge.HandleWebRtcOffer(sessionId, offer, isAlice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling bridge offer for session {sessionId}");
        }
    }

    private async Task HandleBridgeAnswer(string sessionId, object data)
    {
        try
        {
            if (_sessionToBridge.TryGetValue(sessionId, out string? bridgeId) &&
                _callBridges.TryGetValue(bridgeId, out CallBridge? bridge))
            {
                var answerJson = JsonSerializer.Serialize(data);
                var answer = JsonSerializer.Deserialize<RTCSessionDescriptionInit>(answerJson);
                
                bool isAlice = sessionId == bridge.AliceSessionId;
                await bridge.HandleWebRtcAnswer(sessionId, answer, isAlice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling bridge answer for session {sessionId}");
        }
    }

    private async Task HandleBridgeIceCandidate(string sessionId, object data)
    {
        try
        {
            if (_sessionToBridge.TryGetValue(sessionId, out string? bridgeId) &&
                _callBridges.TryGetValue(bridgeId, out CallBridge? bridge))
            {
                var candidateJson = JsonSerializer.Serialize(data);
                var candidate = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson);
                
                bool isAlice = sessionId == bridge.AliceSessionId;
                await bridge.HandleIceCandidate(sessionId, candidate, isAlice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling bridge ICE candidate for session {sessionId}");
        }
    }

    private async Task HandleAcceptBridgeCall(string sessionId, object data)
    {
        try
        {
            if (_sessionToBridge.TryGetValue(sessionId, out string? bridgeId) &&
                _callBridges.TryGetValue(bridgeId, out CallBridge? bridge))
            {
                // The call bridge is already created, just notify the other party
                var bridgeInfo = JsonSerializer.Deserialize<BridgeCallInfo>(JsonSerializer.Serialize(data));
                
                // Find the other session in this bridge
                var otherSessionId = _sessionToBridge.FirstOrDefault(x => x.Value == bridgeId && x.Key != sessionId).Key;
                if (!string.IsNullOrEmpty(otherSessionId))
                {
                    await NotifyBrowserClient(otherSessionId, "bridge-accepted", new
                    {
                        bridgeId = bridgeId,
                        acceptedBy = sessionId
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling bridge call acceptance for session {sessionId}");
        }
    }

    private async Task HandleRejectBridgeCall(string sessionId)
    {
        try
        {
            if (_sessionToBridge.TryGetValue(sessionId, out string? bridgeId) &&
                _callBridges.TryGetValue(bridgeId, out CallBridge? bridge))
            {
                // Find the other session in this bridge
                var otherSessionId = _sessionToBridge.FirstOrDefault(x => x.Value == bridgeId && x.Key != sessionId).Key;
                if (!string.IsNullOrEmpty(otherSessionId))
                {
                    await NotifyBrowserClient(otherSessionId, "bridge-rejected", new
                    {
                        bridgeId = bridgeId,
                        rejectedBy = sessionId
                    });
                }

                // Clean up the bridge
                bridge.Hangup();
                _callBridges.Remove(bridgeId);
                _sessionToBridge.Remove(sessionId);
                if (!string.IsNullOrEmpty(otherSessionId))
                {
                    _sessionToBridge.Remove(otherSessionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling bridge call rejection for session {sessionId}");
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

            // Clean up call session
            if (_callSessions.TryGetValue(sessionId, out CallSession? callSession))
            {
                callSession.SipUserAgent?.Hangup();
                callSession.WebRtcPeer?.close();
                _callSessions.Remove(sessionId);
            }

            // Clean up call bridge if this session is part of one
            if (_sessionToBridge.TryGetValue(sessionId, out string? bridgeId))
            {
                if (_callBridges.TryGetValue(bridgeId, out CallBridge? bridge))
                {
                    bridge.Hangup();
                    _callBridges.Remove(bridgeId);
                }
                _sessionToBridge.Remove(sessionId);
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

            // Check if this is a bridge call (to another session)
            var targetSessionId = ExtractSessionIdFromUri(sipUri);

            if (!string.IsNullOrEmpty(targetSessionId) && _webSocketClients.ContainsKey(targetSessionId))
            {

                if (!string.IsNullOrEmpty(targetSessionId) && _webSocketClients.ContainsKey(targetSessionId))
                {
                    // This is Alice calling Bob - create a call bridge
                    await HandleAliceToBobCall(sessionId, targetSessionId, sipRequest);
                }
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

        if (_callSessions.TryGetValue(sessionId, out CallSession? callSession))
        {
            callSession.SipUserAgent?.Hangup();
            callSession.WebRtcPeer?.close();
            _callSessions.Remove(sessionId);
        }

        _logger.LogInformation($"Call from SIP rejected by browser, session {sessionId}");
    }

    private async Task NotifyBrowserClient(string sessionId, string messageType, object data)
    {
        try
        {
            if (_webSocketClients.TryGetValue(sessionId, out CustomWebSocketBehavior? value))
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

        foreach (var transport in _sipTransports.Values)
        {
            transport.Shutdown();
        }
        _sipTransports.Clear();

        foreach (var callSession in _callSessions.Values)
        {
            callSession.SipUserAgent?.Hangup();
            callSession.WebRtcPeer?.close();
        }
        _callSessions.Clear();

        foreach (var bridge in _callBridges.Values)
        {
            bridge.Hangup();
        }
        _callBridges.Clear();
        _sessionToBridge.Clear();

        _logger.LogInformation("SIP-WebRTC Gateway stopped");
    }
}
