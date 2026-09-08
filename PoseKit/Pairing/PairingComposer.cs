namespace PoseKit.Pairing;

/// <summary>Only ever builds text and returns it — no dependency on any chat-send API, so there is no
/// code path here that could ever transmit anything. Sending is always a separate, deliberate step
/// PairingSender takes with the string this class hands back.</summary>
public static class PairingComposer
{
    private const string InviteKeyword = "posekitpair invite";
    private const string AcceptKeyword = "posekitpair accept";
    private const string UnpairKeyword = "posekitpair unpair";
    private const string QueueKeyword = "posekitqueue";

    public static string ComposeInvite(PartnerIdentity target, string inviteId) =>
        $"/tell {target.TellAddress} {InviteKeyword} {inviteId}";

    public static string ComposeAccept(PartnerIdentity target, string inviteId) =>
        $"/tell {target.TellAddress} {AcceptKeyword} {inviteId}";

    /// Best-effort notice so the peer isn't left showing a stale "Paired with X" after this side
    /// unpairs — mirrors the "Panic notifies the peer" pattern: ending a trust relationship doesn't
    /// need the invite/accept id handshake establishing one did.
    public static string ComposeUnpair(PartnerIdentity target) =>
        $"/tell {target.TellAddress} {UnpairKeyword}";

    /// A readiness signal only, no payload — the receiver already knows its own queued selection, so
    /// this tell only needs to say "I've queued something," not what.
    public static string ComposeQueueSignal(PartnerIdentity target) =>
        $"/tell {target.TellAddress} {QueueKeyword}";
}
