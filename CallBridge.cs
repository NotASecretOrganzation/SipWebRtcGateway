using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace ConsoleApp1;

public class CallBridge
{
    private readonly ILogger<CallBridge> _logger;
    private readonly string _bridgeId;
    private RTCPeerConnection _aliceWebRtc;
    private RTCPeerConnection _bobWebRtc;
    private SIPUserAgent _aliceSip;
    private SIPUserAgent _bobSip;
    private VoIPMediaSession _aliceMediaSession;
    private VoIPMediaSession _bobMediaSession;
    private bool _isActive;

    public CallBridge(ILogger<CallBridge> logger)
    {
        _logger = logger;
        _bridgeId = Guid.NewGuid().ToString();
    }

    public async Task<bool> CreateBridge(string aliceSessionId, string bobSessionId, SIPTransport aliceTransport, SIPTransport bobTransport)
    {
        try
        {
            _logger.LogInformation($"Creating call bridge {_bridgeId} between Alice ({aliceSessionId}) and Bob ({bobSessionId})");

            // Create WebRTC connections for both parties
            _aliceWebRtc = await CreateWebRtcPeerConnection(aliceSessionId);
            _bobWebRtc = await CreateWebRtcPeerConnection(bobSessionId);

            // Create SIP user agents
            _aliceSip = new SIPUserAgent(aliceTransport, null);
            _bobSip = new SIPUserAgent(bobTransport, null);

            // Create media sessions
            _aliceMediaSession = new VoIPMediaSession();
            _bobMediaSession = new VoIPMediaSession();

            // Set up RTP bridging between Alice and Bob
            SetupRtpBridging();

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

    public async Task<bool> InitiateCall(string aliceUri, string bobUri)
    {
        try
        {
            _logger.LogInformation($"Initiating call from {aliceUri} to {bobUri} via bridge {_bridgeId}");

            // Create WebRTC offers for both parties
            var aliceOffer = _aliceWebRtc.createOffer();
            await _aliceWebRtc.setLocalDescription(aliceOffer);

            var bobOffer = _bobWebRtc.createOffer();
            await _bobWebRtc.setLocalDescription(bobOffer);

            // Initiate SIP calls
            var aliceCallResult = await _aliceSip.Call(aliceUri, null, null, _aliceMediaSession);
            var bobCallResult = await _bobSip.Call(bobUri, null, null, _bobMediaSession);

            if (aliceCallResult && bobCallResult)
            {
                _logger.LogInformation($"Successfully initiated calls via bridge {_bridgeId}");
                return true;
            }
            else
            {
                _logger.LogWarning($"Failed to initiate calls via bridge {_bridgeId}. Alice: {aliceCallResult}, Bob: {bobCallResult}");
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
} 