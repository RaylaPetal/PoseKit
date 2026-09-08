namespace PoseKit.Pairing;

using PoseKit;

/// <summary>Only ever builds text and returns it — no dependency on any chat-send API, so there is no
/// code path here that could ever transmit anything. Sending is always a separate, deliberate step
/// PairingSender takes with the string this class hands back.
///
/// Every keyword that carries a free-text name puts it last, taking the rest of the message — names
/// can contain spaces, and every other field is small and fixed-shape, so there's no need for any
/// escaping/encoding as long as parsing only ever splits the fixed leading tokens off.</summary>
public static class PairingComposer
{
    private const string InviteKeyword = "posekitpair invite";
    private const string AcceptKeyword = "posekitpair accept";
    private const string UnpairKeyword = "posekitpair unpair";
    private const string QueueKeyword = "posekitqueue";
    private const string PresetSyncKeyword = "posekitpresetsync";

    public static string ComposeInvite(PartnerIdentity target, string inviteId) =>
        $"/tell {target.TellAddress} {InviteKeyword} {inviteId}";

    public static string ComposeAccept(PartnerIdentity target, string inviteId) =>
        $"/tell {target.TellAddress} {AcceptKeyword} {inviteId}";

    /// Best-effort notice so the peer isn't left showing a stale "Paired with X" after this side
    /// unpairs — mirrors the "Panic notifies the peer" pattern: ending a trust relationship doesn't
    /// need the invite/accept id handshake establishing one did.
    public static string ComposeUnpair(PartnerIdentity target) =>
        $"/tell {target.TellAddress} {UnpairKeyword}";

    /// Carries which selection was queued (its display name) so the partner's UI can show what you
    /// picked before they've picked their own, and so the couple-pairing section can display it too.
    public static string ComposeQueueSignal(PartnerIdentity target, string name) =>
        $"/tell {target.TellAddress} {QueueKeyword} {name}";

    /// Sent when saving a preset while paired, so the partner's own client auto-saves a matching
    /// entry — same name and pose, but never the offset (that's captured relative to this side's own
    /// body and means nothing applied to theirs; they set their own via the live-offset editor and
    /// "Update preset" once it's created). No Penumbra link or anchor either — both are meaningful
    /// only relative to the side that captured them.
    public static string ComposePresetSync(PartnerIdentity target, PoseIdentifier pose, string name) =>
        $"/tell {target.TellAddress} {PresetSyncKeyword} {pose.EmoteModeId} {pose.CPoseState} {name}";
}
