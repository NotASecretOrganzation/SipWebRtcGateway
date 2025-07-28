using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

public class CallSession
{
    public SIPTransport SipTransport { get; set; }
    public SIPUserAgent SipUserAgent { get; set; }
    public RTCPeerConnection WebRtcPeer { get; set; }
    public VoIPMediaSession MediaSession { get; set; }
}