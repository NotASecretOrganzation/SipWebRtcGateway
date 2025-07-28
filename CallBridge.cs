using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace ConsoleApp1;

public class CallBridge
{
    protected readonly ILogger<CallBridge> _logger;
    protected readonly string _bridgeId;
    protected SIPTransport _aliceTransport;
    protected SIPTransport _bobTransport;
    protected RTCPeerConnection _aliceWebRtc;
    protected RTCPeerConnection _bobWebRtc;
    protected SIPUserAgent _aliceSip;
    protected SIPUserAgent _bobSip;
    protected VoIPMediaSession _aliceMediaSession;
    protected VoIPMediaSession _bobMediaSession;
    protected bool _isActive;
    protected string _aliceSessionId;
    protected string _bobSessionId;
    protected bool _aliceAccepted;
    protected bool _bobAccepted;
    protected bool _sipCallEstablished;

    public CallBridge(ILogger<CallBridge> logger)
    {
        _logger = logger;
        _bridgeId = Guid.NewGuid().ToString();
        _aliceAccepted = false;
        _bobAccepted = false;
        _sipCallEstablished = false;
    }

    public async Task<bool> EstablishBridge(string aliceSessionId, string bobSessionId, SIPTransport aliceTransport, SIPTransport bobTransport)
    {
        try
        {
            _aliceTransport = aliceTransport;
            _bobTransport = bobTransport;
            _aliceSessionId = aliceSessionId;
            _bobSessionId = bobSessionId;
            
            _logger.LogInformation($"Creating call bridge {_bridgeId} between Alice ({aliceSessionId}) and Bob ({bobSessionId})");

            // Create WebRTC connections for both parties
            _aliceWebRtc = await CreateWebRtcPeerConnection(aliceSessionId);
            _bobWebRtc = await CreateWebRtcPeerConnection(bobSessionId);

            // Create SIP user agents
            _aliceSip = new SIPUserAgent(aliceTransport, null);
            _aliceSip.OnIncomingCall += (ua, req) =>
            {
                _logger.LogInformation($"Incoming call for Alice ({aliceSessionId}) in bridge {_bridgeId}");
                // Handle incoming call logic here
            };

            _bobSip = new SIPUserAgent(bobTransport, null);
            _bobSip.OnIncomingCall += (ua, req) =>
            {
                _logger.LogInformation($"Incoming call for Bob ({bobSessionId}) in bridge {_bridgeId}");
                // Handle incoming call logic here
            };

            // Create media sessions
            _aliceMediaSession = new VoIPMediaSession();
            _bobMediaSession = new VoIPMediaSession();

            // 注意：此時不設置 RTP 轉發，等待 SIP 呼叫建立後再設置
            _isActive = true;
            _logger.LogInformation($"Call bridge {_bridgeId} created successfully");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to create call bridge {_bridgeId}");
            return false;
        }
    }

    // 新增：接受呼叫的方法
    public async Task<bool> AcceptCall(string sessionId)
    {
        try
        {
            bool isAlice = sessionId == _aliceSessionId;
            if (isAlice)
            {
                _aliceAccepted = true;
            }
            else
            {
                _bobAccepted = true;
            }

            _logger.LogInformation($"{(isAlice ? "Alice" : "Bob")} ({sessionId}) accepted the call in bridge {_bridgeId}");

            // 如果兩方都接受了，建立 SIP 呼叫
            if (_aliceAccepted && _bobAccepted && !_sipCallEstablished)
            {
                await EstablishSipCall();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error accepting call for session {sessionId}");
            return false;
        }
    }

    // 新增：建立 SIP 呼叫的方法
    private async Task EstablishSipCall()
    {
        try
        {
            _logger.LogInformation($"Both parties accepted, establishing SIP call in bridge {_bridgeId}");
            
            // 建立 Alice 到 Bob 的 SIP 呼叫
            string bobSipUrl = $"sip:{_bobSessionId}@{_bobTransport.GetSIPChannels().FirstOrDefault()?.ListeningSIPEndPoint.Address}:{_bobTransport.GetSIPChannels().FirstOrDefault()?.ListeningSIPEndPoint.Port}";
            
            var callResult = await _aliceSip.Call(bobSipUrl, null, null, _aliceMediaSession);
            
            if (callResult)
            {
                _sipCallEstablished = true;
                _logger.LogInformation($"SIP call successfully established in bridge {_bridgeId}");
                
                // SIP 呼叫建立後，設置 RTP 轉發
                SetupRtpBridging();
            }
            else
            {
                _logger.LogWarning($"Failed to establish SIP call in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error establishing SIP call in bridge {_bridgeId}");
        }
    }

    private async Task<RTCPeerConnection> CreateWebRtcPeerConnection(string sessionId)
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "turn:172.27.200.242:3478", username = "username1", credential = "password1" }
            }
        };

        var peerConnection = new RTCPeerConnection(config);

        // Add audio track
        var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, new List<SDPAudioVideoMediaFormat>
        {
            new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU)
        });

        peerConnection.addTrack(audioTrack);

        var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, new List<SDPAudioVideoMediaFormat>
        {
            new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.H263)
        });



        peerConnection.addTrack(videoTrack);

        // Handle connection state changes
        peerConnection.onconnectionstatechange += (state) =>
        {
            _logger.LogInformation($"WebRTC connection state changed to {state} for session {sessionId} in bridge {_bridgeId}");
        };

        return peerConnection;
    }

    private void SetupRtpBridging()
    {
        // Bridge RTP from Alice's WebRTC to Bob's SIP
        _aliceWebRtc.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (_isActive && _bobMediaSession != null)
            {
                _bobMediaSession.SendRtpRaw(media, rtpPkt.Payload,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            }
        };

        // Bridge RTP from Bob's WebRTC to Alice's SIP
        _bobWebRtc.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (_isActive && _aliceMediaSession != null)
            {
                _aliceMediaSession.SendRtpRaw(media, rtpPkt.Payload,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            }
        };

        // Bridge RTP from Alice's SIP to Bob's WebRTC
        _aliceMediaSession.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (_isActive && _bobWebRtc != null)
            {
                _bobWebRtc.SendRtpRaw(media, rtpPkt.Payload,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            }
        };

        // Bridge RTP from Bob's SIP to Alice's WebRTC
        _bobMediaSession.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (_isActive && _aliceWebRtc != null)
            {
                _aliceWebRtc.SendRtpRaw(media, rtpPkt.Payload,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            }
        };

        _logger.LogInformation($"RTP bridging set up for bridge {_bridgeId}");
    }

    protected string TransportToSipUrl(string sessionId, SIPTransport transport)
    {
        // Convert transport to SIP URL format
        return $"sip:{sessionId}@{transport.GetSIPChannels().FirstOrDefault()?.ListeningSIPEndPoint.Address}:{transport.GetSIPChannels().FirstOrDefault()?.ListeningSIPEndPoint.Port}";
    }

    public async Task<bool> InitiateCall()
    {
        try
        {
            string sipUrl = TransportToSipUrl(BobSessionId,_bobTransport);
            // Initiate SIP calls
            var callResult = await _aliceSip.Call(sipUrl, null, null, _aliceMediaSession);

            if (callResult)
            {
                _logger.LogInformation($"Successfully initiated calls via bridge {_bridgeId}");
                return true;
            }
            else
            {
                _logger.LogWarning($"Failed to initiate calls via bridge {_bridgeId}.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error initiating calls via bridge {_bridgeId}");
            return false;
        }
    }

    public async Task HandleWebRtcOffer(string sessionId, RTCSessionDescriptionInit offer, bool isAlice)
    {
        try
        {
            var peerConnection = isAlice ? _aliceWebRtc : _bobWebRtc;
            
            if (peerConnection != null)
            {
                peerConnection.setRemoteDescription(offer);
                var answer = peerConnection.createAnswer();
                await peerConnection.setLocalDescription(answer);

                _logger.LogInformation($"Processed WebRTC offer for {(isAlice ? "Alice" : "Bob")} in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling WebRTC offer for {(isAlice ? "Alice" : "Bob")} in bridge {_bridgeId}");
        }
    }

    public async Task HandleWebRtcAnswer(string sessionId, RTCSessionDescriptionInit answer, bool isAlice)
    {
        try
        {
            var peerConnection = isAlice ? _aliceWebRtc : _bobWebRtc;
            
            if (peerConnection != null)
            {
                peerConnection.setRemoteDescription(answer);
                _logger.LogInformation($"Processed WebRTC answer for {(isAlice ? "Alice" : "Bob")} in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling WebRTC answer for {(isAlice ? "Alice" : "Bob")} in bridge {_bridgeId}");
        }
    }

    public async Task HandleIceCandidate(string sessionId, RTCIceCandidateInit candidate, bool isAlice)
    {
        try
        {
            var peerConnection = isAlice ? _aliceWebRtc : _bobWebRtc;
            
            if (peerConnection != null)
            {
                peerConnection.addIceCandidate(candidate);
                _logger.LogInformation($"Added ICE candidate for {(isAlice ? "Alice" : "Bob")} in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling ICE candidate for {(isAlice ? "Alice" : "Bob")} in bridge {_bridgeId}");
        }
    }

    public void Hangup()
    {
        _isActive = false;

        _aliceSip?.Hangup();
        _bobSip?.Hangup();

        _aliceWebRtc?.close();
        _bobWebRtc?.close();

        _logger.LogInformation($"Call bridge {_bridgeId} hung up");
    }

    public void Dispose()
    {
        Hangup();
    }

    public string BridgeId => _bridgeId;
    public bool IsActive => _isActive;
    public RTCPeerConnection AliceWebRtc => _aliceWebRtc;
    public RTCPeerConnection BobWebRtc => _bobWebRtc;
    public string AliceSessionId => _aliceSessionId;
    public string BobSessionId => _bobSessionId;
    public bool AliceAccepted => _aliceAccepted;
    public bool BobAccepted => _bobAccepted;
    public bool SipCallEstablished => _sipCallEstablished;
} 