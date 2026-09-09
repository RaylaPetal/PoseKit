namespace PoseKit.Pairing;

using System.Collections.Generic;
using System.Globalization;
using PoseKit.Presets;

/// <summary>Only ever builds text and returns it — no dependency on any chat-send API, so there is no
/// code path here that could ever transmit anything. Sending is always a separate, deliberate step
/// PairingSender takes with the string this class hands back.
///
/// Every keyword that carries free-text puts it last, as a single '|'-joined compound token taking
/// the rest of the message — names/mod/group/option text can contain spaces (and even '|', since the
/// final piece of the compound absorbs everything left after splitting only the first few '|'s), and
/// every other field is small and fixed-shape, so there's no need for further escaping as long as
/// parsing only ever splits the fixed leading tokens and the fixed number of leading '|' separators.
/// </summary>
public static class PairingComposer
{
    private const string InviteKeyword = "posekitpair invite";
    private const string AcceptKeyword = "posekitpair accept";
    private const string UnpairKeyword = "posekitpair unpair";
    private const string QueueKeyword = "posekitqueue";
    private const string OverrideToggleKeyword = "posekitoverride";
    private const string ForceSelectKeyword = "posekitforcequeue";
    private const string CoupleCaptureKeyword = "posekitcouplecapture";
    private const string CoupleCaptureReplyKeyword = "posekitcouplecapturereply";
    private const string CoupleRelayKeyword = "posekitcoupleplay";

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

    /// Sent when saving a preset while paired with "include partner" enabled: asks the partner to
    /// capture and reply with its own current pose/offset/anchor/Penumbra state. Only the anchor
    /// *kind* (none/spot/furniture) and, for furniture, which item — not any position/rotation —
    /// travel here, so the partner captures its OWN spot/furniture-relative position rather than
    /// this side's; the request just hints which kind of anchor to capture and, for furniture, which
    /// physical item (so both sides anchor to the same piece of furniture). See
    /// couple-preset-relay's spec — the reply (ComposeCoupleCaptureReply) carries the actual captured
    /// state back.
    public static string ComposeCoupleCaptureRequest(PartnerIdentity target, string requestId, PresetAnchor? anchorHint)
    {
        var anchorKind = anchorHint?.Spot != null ? 1 : anchorHint?.Furniture != null ? 2 : 0;
        var entryId = anchorHint?.Furniture?.EntryId ?? 0u;
        var ic = CultureInfo.InvariantCulture;
        return $"/tell {target.TellAddress} {CoupleCaptureKeyword} {requestId} {anchorKind} " +
               $"{entryId.ToString(ic)} {anchorHint?.Furniture?.FurnitureName ?? ""}";
    }

    /// Sent once in reply to a capture request, carrying this side's own captured pose/offset/anchor/
    /// Penumbra state tagged with the request id so the requester can match it to the right in-flight
    /// save (and discard anything stale/unmatched). Never sent unprompted.
    public static string ComposeCoupleCaptureReply(PartnerIdentity target, string requestId,
        PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, PenumbraLink? penumbra) =>
        $"/tell {target.TellAddress} {CoupleCaptureReplyKeyword} {requestId} " +
        $"{ComposeCapturedStateTail(pose, offset, anchor, penumbra, presetName: null)}";

    /// Sent when playing a preset that carries a captured partner half: relays that half (its
    /// pose/offset/anchor/Penumbra state, exactly as captured at save time) plus the preset's name,
    /// for the partner's accept/deny prompt (or immediate auto-play under mutual override). No
    /// acknowledgement is expected back — the sender already plays its own half immediately.
    public static string ComposeCoupleRelay(PartnerIdentity target, string presetName,
        PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, PenumbraLink? penumbra) =>
        $"/tell {target.TellAddress} {CoupleRelayKeyword} " +
        $"{ComposeCapturedStateTail(pose, offset, anchor, penumbra, presetName)}";

    /// Shared wire shape for "a captured pose/offset/anchor/Penumbra state", used by both the capture
    /// reply and the play relay (which also appends the preset's name as one more compound field).
    /// Fixed-shape fields first (emote mode/cpose, offset, anchor kind/numeric fields), then every
    /// free-text field joined by '|' last, ordered least-to-most likely to itself contain a literal
    /// '|' so only the true last field needs to safely absorb one — furniture/group/option names
    /// before the more free-form mod name/directory, with an optional preset name (the most
    /// user-free-typed of all of them) absolute last.
    private static string ComposeCapturedStateTail(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor,
        PenumbraLink? penumbra, string? presetName)
    {
        var ic = CultureInfo.InvariantCulture;
        var anchorKind = anchor?.Spot != null ? 1 : anchor?.Furniture != null ? 2 : 0;
        var (anchorNum, ax, ay, az, arot, furnitureName) = anchor?.Spot is { } spot
            ? (spot.TerritoryType, spot.Position.X, spot.Position.Y, spot.Position.Z, spot.Rotation, "")
            : anchor?.Furniture is { } furniture
                ? (furniture.EntryId, furniture.RelativePosition.X, furniture.RelativePosition.Y,
                    furniture.RelativePosition.Z, furniture.RelativeRotation, furniture.FurnitureName)
                : (0u, 0f, 0f, 0f, 0f, "");

        var tokens = string.Join(' ', new[]
        {
            pose.EmoteModeId.ToString(ic), pose.CPoseState.ToString(ic),
            offset.Position.X.ToString(ic), offset.Position.Y.ToString(ic), offset.Position.Z.ToString(ic),
            offset.Rotation.ToString(ic),
            anchorKind.ToString(ic), anchorNum.ToString(ic),
            ax.ToString(ic), ay.ToString(ic), az.ToString(ic), arot.ToString(ic),
            (penumbra != null ? 1 : 0).ToString(ic),
        });

        var compoundParts = new List<string> { furnitureName, penumbra?.GroupName ?? "", penumbra?.OptionName ?? "", penumbra?.ModName ?? "", penumbra?.ModDirectory ?? "" };
        if (presetName != null) compoundParts.Add(presetName);

        return $"{tokens} {string.Join('|', compoundParts)}";
    }

    /// Announces this side's own "override queue" checkbox state — sent whenever it's toggled, and
    /// once more right after pairing activates, so the partner's MutualOverrideActive reading is
    /// never stale.
    public static string ComposeOverrideToggle(PartnerIdentity target, bool enabled) =>
        $"/tell {target.TellAddress} {OverrideToggleKeyword} {(enabled ? "on" : "off")}";

    /// Sent instead of the normal queue signal when the acting side already has its own queued pick
    /// and mutual override is active — names the item the *partner* should play, not the sender's
    /// own. No acknowledgement is expected or sent back; the sender plays its own already-queued pick
    /// immediately rather than waiting for one.
    public static string ComposeForceSelection(PartnerIdentity target, string name) =>
        $"/tell {target.TellAddress} {ForceSelectKeyword} {name}";
}
