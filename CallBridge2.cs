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
    protected RTCPeerConnection _aliceWebCallRtc;
    protected RTCPeerConnection _aliceWebAnswerCallRtc;
    protected SIPUserAgent _aliceCallAgent;
    protected SIPUserAgent _aliceAnswerCallAgent;
    protected VoIPMediaSession _aliceCallMediaSession;
    protected VoIPMediaSession _aliceAnswerCallMediaSession;
    protected string _aliceSessionId;
    protected bool _aliceAccepted;
    protected bool _sipCallEstablished;

    public CallBridge2(ILogger<CallBridge> logger, string aliceSessionId, SIPTransport aliceTransport)
    {
        _logger = logger;
        _bridgeId = Guid.NewGuid().ToString();
        _aliceSessionId = aliceSessionId;
        _aliceTransport = aliceTransport;
        _aliceAnswerCallAgent = new SIPUserAgent(aliceTransport, null);
        _aliceAnswerCallAgent.OnIncomingCall += async (ua, req) =>
        {
            _aliceAnswerCallMediaSession = new VoIPMediaSession();
            _aliceWebAnswerCallRtc = await CreateWebRtcPeerConnection(aliceSessionId);
            _aliceAnswerCallMediaSession.OnRtpPacketReceived += (rep, media, rtpPkt) =>
            {
                _aliceWebAnswerCallRtc.SendRtpRaw(media, rtpPkt.Payload,
                       rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            };
            _aliceWebAnswerCallRtc.OnRtpPacketReceived += (rep, media, rtpPkt) =>
            {
                _aliceAnswerCallMediaSession.SendRtpRaw(media, rtpPkt.Payload,
                       rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            };
            _logger.LogInformation($"Incoming call for Alice ({aliceSessionId}) in bridge {_bridgeId}");
            SIPServerUserAgent? useragent = ua.AcceptCall(req);
            await _aliceCallAgent.Answer(useragent, _aliceAnswerCallMediaSession);
            await _aliceAnswerCallMediaSession.Start();
        };
    }

    // New: Method to establish SIP call
    public async Task EstablishSipCall(string sipUrl)
    {
        try
        {
            _logger.LogInformation($"Creating call bridge {_bridgeId} for Alice ({_aliceSessionId})");

            // Create SIP user agents
            _aliceCallAgent = new SIPUserAgent(_aliceTransport, null);

            // Create media sessions
            _aliceCallMediaSession = new VoIPMediaSession();
            _aliceWebCallRtc = await CreateWebRtcPeerConnection(_aliceSessionId);
            _aliceCallMediaSession.OnRtpPacketReceived += (rep, media, rtpPkt) =>
            {
                _aliceWebAnswerCallRtc.SendRtpRaw(media, rtpPkt.Payload,
                       rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            };
            _aliceWebCallRtc.OnRtpPacketReceived += (rep, media, rtpPkt) =>
            {
                _aliceCallMediaSession.SendRtpRaw(media, rtpPkt.Payload,
                       rtpPkt.Header.Timestamp, rtpPkt.Header.MarkerBit, rtpPkt.Header.PayloadType);
            };
            var callResult = await _aliceCallAgent.Call(sipUrl, null, null, _aliceCallMediaSession);

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

    public async Task HandleWebRtcOffer(RTCPeerConnection peerConnection, string sessionId, RTCSessionDescriptionInit offer)
    {
        try
        {
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

    public async Task HandleWebRtcAnswer(RTCPeerConnection peerConnection, string sessionId, RTCSessionDescriptionInit answer)
    {
        try
        {
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

    public async Task HandleIceCandidate(RTCPeerConnection peerConnection, string sessionId, RTCIceCandidateInit candidate)
    {
        try
        {
            if (peerConnection != null)
            {
                peerConnection.addIceCandidate(candidate);
                _logger.LogInformation($"Added ICE candidate in bridge {_bridgeId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling ICE candidate in bridge {_bridgeId}");
        }
    }

    public void HangupCall()
    {
        _aliceCallAgent?.Hangup();

        _aliceWebCallRtc?.close();
    }

    public void HangupAnserCall()
    {

        _aliceAnswerCallAgent?.Hangup();

        _aliceWebAnswerCallRtc?.close();

        _logger.LogInformation($"Call bridge {_bridgeId} hung up");
    }

    public void Dispose()
    {
        HangupAnserCall();
    }
}