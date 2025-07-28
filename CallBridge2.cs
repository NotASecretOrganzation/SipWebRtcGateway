using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace ConsoleApp1;

public class CallBridge2
{
    protected readonly ILogger<CallBridge> _logger;
    protected readonly string _bridgeId;
    protected SIPTransport _aliceTransport;
    protected RTCPeerConnection _aliceWebRtc;
    protected SIPUserAgent _aliceSip;
    protected VoIPMediaSession _aliceMediaSession;
    protected bool _isActive;
    protected string _aliceSessionId;
    protected string _bobSessionId;
    protected bool _aliceAccepted;
    protected bool _bobAccepted;
    protected bool _sipCallEstablished;

    public CallBridge2(ILogger<CallBridge> logger)
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
            _aliceSessionId = aliceSessionId;
            _bobSessionId = bobSessionId;

            _logger.LogInformation($"Creating call bridge {_bridgeId} between Alice ({aliceSessionId}) and Bob ({bobSessionId})");

            // Create WebRTC connections for both parties
            _aliceWebRtc = await CreateWebRtcPeerConnection(aliceSessionId);

            // Create SIP user agents
            _aliceSip = new SIPUserAgent(aliceTransport, null);
            _aliceSip.OnIncomingCall += (ua, req) =>
            {
                _logger.LogInformation($"Incoming call for Alice ({aliceSessionId}) in bridge {_bridgeId}");
                // Handle incoming call logic here
            };

            // Create media sessions
            _aliceMediaSession = new VoIPMediaSession();

            // Note: Don't set up RTP bridging yet, wait until SIP call is established
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

    // New: Method to accept calls
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


            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error accepting call for session {sessionId}");
            return false;
        }
    }

    // New: Method to establish SIP call
    public async Task EstablishSipCall(string sipUrl)
    {
        try
        {
            _logger.LogInformation($"Both parties accepted, establishing SIP call in bridge {_bridgeId}");

            SetupRtpBridging();
            var callResult = await _aliceSip.Call(sipUrl, null, null, _aliceMediaSession);

            if (callResult)
            {
                _sipCallEstablished = true;
                _logger.LogInformation($"SIP call successfully established in bridge {_bridgeId}");

                // Set up RTP bridging after SIP call is established
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
            if (_isActive && _aliceMediaSession != null)
            {
                _aliceMediaSession.SendRtpRaw(media, rtpPkt.Payload,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            }
        };

        
        // Bridge RTP from Alice's SIP to Bob's WebRTC
        _aliceMediaSession.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (_isActive && _aliceWebRtc != null)
            {
                _aliceWebRtc.SendRtpRaw(media, rtpPkt.Payload,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            }
        };
        
    }

    protected string TransportToSipUrl(string sessionId, SIPTransport transport)
    {
        // Convert transport to SIP URL format
        return $"sip:{sessionId}@{transport.GetSIPChannels().FirstOrDefault()?.ListeningSIPEndPoint.Address}:{transport.GetSIPChannels().FirstOrDefault()?.ListeningSIPEndPoint.Port}";
    }
    public async Task HandleWebRtcOffer(string sessionId, RTCSessionDescriptionInit offer)
    {
        try
        {
            var peerConnection = _aliceWebRtc;

            if (peerConnection != null)
            {
                peerConnection.setRemoteDescription(offer);
                var answer = peerConnection.createAnswer();
                await peerConnection.setLocalDescription(answer);

                _logger.LogInformation($"Processed WebRTC offer in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling WebRTC offer in bridge {_bridgeId}");
        }
    }

    public async Task HandleWebRtcAnswer(string sessionId, RTCSessionDescriptionInit answer)
    {
        try
        {
            var peerConnection = _aliceWebRtc;

            if (peerConnection != null)
            {
                peerConnection.setRemoteDescription(answer);
                _logger.LogInformation($"Processed WebRTC offer in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling WebRTC answer in bridge {_bridgeId}");
        }
    }

    public async Task HandleIceCandidate(string sessionId, RTCIceCandidateInit candidate)
    {
        try
        {
            if (_aliceWebRtc != null)
            {
                _aliceWebRtc.addIceCandidate(candidate);
                _logger.LogInformation($"Added ICE candidate in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling ICE candidate in bridge {_bridgeId}");
        }
    }

    public void Hangup()
    {
        _isActive = false;

        _aliceSip?.Hangup();

        _aliceWebRtc?.close();

        _logger.LogInformation($"Call bridge {_bridgeId} hung up");
    }

    public void Dispose()
    {
        Hangup();
    }

    public string BridgeId => _bridgeId;
    public bool IsActive => _isActive;
    public RTCPeerConnection AliceWebRtc => _aliceWebRtc;
    public string AliceSessionId => _aliceSessionId;
    public string BobSessionId => _bobSessionId;
    public bool AliceAccepted => _aliceAccepted;
    public bool BobAccepted => _bobAccepted;
    public bool SipCallEstablished => _sipCallEstablished;
}