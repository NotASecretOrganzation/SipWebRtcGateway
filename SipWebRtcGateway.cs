using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
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

    // 在 SipWebRtcGateway 類別中添加新的欄位來追蹤橋接通話狀態
    private Dictionary<string, BridgeCallState> _bridgeCallStates = new();

    // 添加橋接通話狀態類別
    public class BridgeCallState
    {
        public string AliceSessionId { get; set; }
        public string BobSessionId { get; set; }
        public bool AliceAccepted { get; set; }
        public bool BobAccepted { get; set; }
        public string BridgeId { get; set; }
    }

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
            var bridgeCreated = await bridge.EstablishBridge(aliceSessionId, bobSessionId, aliceTransport, bobTransport);
            
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

                //await bridge.InitiateCall();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error creating Alice to Bob call bridge");
        }
    }
    private async Task HandleRegularSipCall(string sessionId, string sipUri)
    {
        try
        {
            // Get the SIP transport for this session
            if (!_sipTransports.TryGetValue(sessionId, out SIPTransport? sipTransport))
            {
                _logger.LogError($"No SIP transport found for session {sessionId}");
                await NotifyBrowserClient(sessionId, "call-failed", "No SIP transport available");
                return;
            }

            var sipUserAgent = new SIPUserAgent(sipTransport, null);
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
            _mediaSessions[sessionId] = mediaSession;

            // Create call session
            var callSession = new CallSession
            {
                SipTransport = sipTransport,
                SipUserAgent = sipUserAgent,
                WebRtcPeer = peerConnection,
                MediaSession = mediaSession
            };
            _callSessions[sessionId] = callSession;

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
                // 客戶端發送的訊息
                case "make-call":
                    await InitiateSipCall(sessionId, (string)msg.Data);
                    break;
                case "accept-call":
                    await HandleWebRtcOffer(sessionId, msg.Data);
                    break;
                case "reject-call":
                    await HandleRejectCall(sessionId);
                    break;
                case "accept-bridge-call":
                    await HandleAcceptBridgeCall(sessionId, msg.Data);
                    break;
                case "reject-bridge-call":
                    await HandleRejectBridgeCall(sessionId);
                    break;
                case "hang-up":
                    await HandleHangUp(sessionId);
                    break;
                case "offer":
                    await HandleWebRtcOffer(sessionId, msg.Data);
                    break;
                case "answer":
                    await HandleWebRtcAnswer(sessionId, msg.Data);
                    break;
                case "ice-candidate":
                    await HandleIceCandidate(sessionId, msg.Data);
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
                default:
                    _logger.LogWarning($"Unknown message type: {msg.Type}");
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
            // Find the bridge call state for this session
            var bridgeCallState = _bridgeCallStates.Values.FirstOrDefault(s => 
                s.AliceSessionId == sessionId || s.BobSessionId == sessionId);

            if (bridgeCallState == null)
            {
                _logger.LogWarning($"No bridge call state found for session {sessionId}");
                return;
            }

            // Update acceptance status
            if (sessionId == bridgeCallState.AliceSessionId)
            {
                bridgeCallState.AliceAccepted = true;
                _logger.LogInformation($"Alice ({sessionId}) accepted bridge call {bridgeCallState.BridgeId}");
            }
            else if (sessionId == bridgeCallState.BobSessionId)
            {
                bridgeCallState.BobAccepted = true;
                _logger.LogInformation($"Bob ({sessionId}) accepted bridge call {bridgeCallState.BridgeId}");
            }

            var otherSessionId = sessionId == bridgeCallState.AliceSessionId 
                ? bridgeCallState.BobSessionId 
                : bridgeCallState.AliceSessionId;

            // Notify the other party that call was accepted
            await NotifyBrowserClient(otherSessionId, "bridge-accepted", new
            {
                acceptedBy = sessionId
            });

            // If both parties have accepted, establish the bridge
            if (bridgeCallState.AliceAccepted && bridgeCallState.BobAccepted)
            {
                _logger.LogInformation($"Both parties accepted, establishing bridge {bridgeCallState.BridgeId}");
                await EstablishBridge(bridgeCallState);
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
                    await HandleBridgeCall(sessionId, targetSessionId, sipUri);
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

    // Add EstablishBridge method
    private async Task EstablishBridge(BridgeCallState bridgeCallState)
    {
        try
        {
            _logger.LogInformation($"Establishing bridge for Alice ({bridgeCallState.AliceSessionId}) and Bob ({bridgeCallState.BobSessionId})");

            // Notify clients that bridge is being established
            await NotifyBrowserClient(bridgeCallState.AliceSessionId, "bridge-establishing", new
            {
                bridgeId = bridgeCallState.BridgeId,
                targetSessionId = bridgeCallState.BobSessionId
            });

            await NotifyBrowserClient(bridgeCallState.BobSessionId, "bridge-establishing", new
            {
                bridgeId = bridgeCallState.BridgeId,
                targetSessionId = bridgeCallState.AliceSessionId
            });

            // Get SIP transports
            if (!_sipTransports.TryGetValue(bridgeCallState.AliceSessionId, out SIPTransport? aliceTransport) ||
                !_sipTransports.TryGetValue(bridgeCallState.BobSessionId, out SIPTransport? bobTransport))
            {
                _logger.LogError($"Missing SIP transport for bridge establishment");
                return;
            }

            // Create CallBridge
            var bridge = new CallBridge(_loggerFactory.CreateLogger<CallBridge>());
            var bridgeCreated = await bridge.EstablishBridge(
                bridgeCallState.AliceSessionId, 
                bridgeCallState.BobSessionId, 
                aliceTransport, 
                bobTransport);

            if (bridgeCreated)
            {
                // Store bridge
                _callBridges[bridge.BridgeId] = bridge;
                _sessionToBridge[bridgeCallState.AliceSessionId] = bridge.BridgeId;
                _sessionToBridge[bridgeCallState.BobSessionId] = bridge.BridgeId;

                // Notify clients that bridge is established (but WebRTC not ready yet)
                await NotifyBrowserClient(bridgeCallState.AliceSessionId, "bridge-established", new
                {
                    bridgeId = bridge.BridgeId,
                    targetSessionId = bridgeCallState.BobSessionId
                });

                await NotifyBrowserClient(bridgeCallState.BobSessionId, "bridge-established", new
                {
                    bridgeId = bridge.BridgeId,
                    targetSessionId = bridgeCallState.AliceSessionId
                });

                _logger.LogInformation($"Bridge {bridge.BridgeId} established successfully");


                await bridge.EstablishSipCall();

                // If SIP call is established, notify clients they can start WebRTC
                if (bridge.SipCallEstablished)
                {
                    await NotifyBrowserClient(bridge.AliceSessionId, "sip-call-established", new
                    {
                        bridgeId = bridge.BridgeId
                    });
                    
                    await NotifyBrowserClient(bridge.BobSessionId, "sip-call-established", new
                    {
                        bridgeId = bridge.BridgeId
                    });
                }
            }
            else
            {
                _logger.LogError($"Failed to create bridge");
                await NotifyBrowserClient(bridgeCallState.AliceSessionId, "bridge-failed", "Failed to establish bridge");
                await NotifyBrowserClient(bridgeCallState.BobSessionId, "bridge-failed", "Failed to establish bridge");
            }

            // Clean up bridge call state
            _bridgeCallStates.Remove(bridgeCallState.BridgeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error establishing bridge");
        }
    }

    // Modify HandleBridgeCall method (called from InitiateSipCall)
    private async Task HandleBridgeCall(string aliceSessionId, string bobSessionId, string sipUri)
    {
        try
        {
            _logger.LogInformation($"Creating bridge call: {aliceSessionId} -> {bobSessionId}");

            // Create bridge call state
            var bridgeId = Guid.NewGuid().ToString();
            var bridgeCallState = new BridgeCallState
            {
                AliceSessionId = aliceSessionId,
                BobSessionId = bobSessionId,
                AliceAccepted = false,
                BobAccepted = false,
                BridgeId = bridgeId
            };

            _bridgeCallStates[bridgeId] = bridgeCallState;

            // Notify Bob about incoming call
            await NotifyBrowserClient(bobSessionId, "bridge-call", new
            {
                bridgeId = bridgeId,
                targetSessionId = aliceSessionId,
                isInitiator = false,
                from = sipUri
            });

            // Notify Alice about the call initiation
            await NotifyBrowserClient(aliceSessionId, "bridge-call", new
            {
                bridgeId = bridgeId,
                targetSessionId = bobSessionId,
                isInitiator = true
            });

            _logger.LogInformation($"Bridge call {bridgeId} initiated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error creating bridge call");
            await NotifyBrowserClient(aliceSessionId, "call-failed", ex.Message);
        }
    }
}
