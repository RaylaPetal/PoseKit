namespace PoseKit.Pairing;

using System.Collections.Generic;
using System.Globalization;
using PoseKit.Presets;

/// <summary>Builds pairing tells; sending is PairingSender's job.
///
/// Free text always goes last, '|'-joined, so it can contain spaces without escaping. The parser only
/// splits the fixed leading fields.
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
    private const string CoupleAnswerKeyword = "posekitcoupleanswer";
    private const string BoneAlignKeyword = "posekitalign";

    public static string ComposeInvite(PartnerIdentity target, string inviteId) =>
        $"/tell {target.TellAddress} {InviteKeyword} {inviteId}";

    public static string ComposeAccept(PartnerIdentity target, string inviteId) =>
        $"/tell {target.TellAddress} {AcceptKeyword} {inviteId}";

    public static string ComposeUnpair(PartnerIdentity target) =>
        $"/tell {target.TellAddress} {UnpairKeyword}";

    public static string ComposeQueueSignal(PartnerIdentity target, string name) =>
        $"/tell {target.TellAddress} {QueueKeyword} {name}";

    /// Asks the partner to capture their own pose state. Only the anchor kind (and furniture item)
    /// is sent, so the partner anchors its own position. A partner anchor is sent as kind 0, since
    /// the partner is the root and is captured unanchored.
    public static string ComposeCoupleCaptureRequest(PartnerIdentity target, string requestId, PresetAnchor? anchorHint)
    {
        var anchorKind = anchorHint?.Spot != null ? 1 : anchorHint?.Furniture != null ? 2 : 0;
        var entryId = anchorHint?.Furniture?.EntryId ?? 0u;
        var ic = CultureInfo.InvariantCulture;
        return $"/tell {target.TellAddress} {CoupleCaptureKeyword} {requestId} {anchorKind} " +
               $"{entryId.ToString(ic)} {anchorHint?.Furniture?.FurnitureName ?? ""}";
    }

    /// The reply to a capture request, tagged with its request id.
    public static string ComposeCoupleCaptureReply(PartnerIdentity target, string requestId,
        PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, PenumbraLink? penumbra) =>
        $"/tell {target.TellAddress} {CoupleCaptureReplyKeyword} {requestId} " +
        $"{ComposeCapturedStateTail(pose, offset, anchor, penumbra, presetName: null)}";

    /// Relays a couple preset's partner half. The sender waits for the answer before playing.
    public static string ComposeCoupleRelay(PartnerIdentity target, string presetName,
        PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, PenumbraLink? penumbra) =>
        $"/tell {target.TellAddress} {CoupleRelayKeyword} " +
        $"{ComposeCapturedStateTail(pose, offset, anchor, penumbra, presetName)}";

    /// Signals that this side started a Bone Align, so simultaneous aligns can tie-break.
    public static string ComposeBoneAlign(PartnerIdentity target) =>
        $"/tell {target.TellAddress} {BoneAlignKeyword}";

    /// The one answer to a couple relay.
    public static string ComposeCoupleAnswer(PartnerIdentity target, string presetName, bool accepted) =>
        $"/tell {target.TellAddress} {CoupleAnswerKeyword} {(accepted ? 1 : 0)} {presetName}";

    /// Wire format for a captured pose state: fixed fields first, then furniture name, mod name and
    /// optional preset name joined by '|'. Mod, group and option travel as hashes; the mod name is
    /// only for the not-found notice.
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
        var modDirectoryHash = penumbra?.ModDirectory is { Length: > 0 } dir ? ModDirectoryHash.Compute(dir) : "0";

        // "0" means not applicable; real hashes are always 8 hex characters.
        var (groupNameHash, optionNameHash) = penumbra?.GroupName is { Length: > 0 } group
            ? (ModDirectoryHash.Compute(group), ModDirectoryHash.Compute(penumbra!.OptionName))
            : ("0", "0");

        var tokens = string.Join(' ', new[]
        {
            pose.EmoteModeId.ToString(ic), pose.CPoseState.ToString(ic),
            offset.Position.X.ToString(ic), offset.Position.Y.ToString(ic), offset.Position.Z.ToString(ic),
            offset.Rotation.ToString(ic),
            anchorKind.ToString(ic), anchorNum.ToString(ic),
            ax.ToString(ic), ay.ToString(ic), az.ToString(ic), arot.ToString(ic),
            (penumbra != null ? 1 : 0).ToString(ic),
            modDirectoryHash, groupNameHash, optionNameHash,
        });

        var compoundParts = new List<string> { furnitureName, penumbra?.ModName ?? "" };
        if (presetName != null) compoundParts.Add(presetName);

        return $"{tokens} {string.Join('|', compoundParts)}";
    }

    public static string ComposeOverrideToggle(PartnerIdentity target, bool enabled) =>
        $"/tell {target.TellAddress} {OverrideToggleKeyword} {(enabled ? "on" : "off")}";

    /// Names what the partner should play under mutual override. For a mod pose it also carries the
    /// mod, group, option and trigger hashes ("0" when not applicable), since one option can have
    /// several triggers.
    public static string ComposeForceSelection(PartnerIdentity target, string name,
        PenumbraLink? penumbra = null, string? triggerText = null)
    {
        var modDirectoryHash = penumbra?.ModDirectory is { Length: > 0 } dir ? ModDirectoryHash.Compute(dir) : "0";
        var (groupNameHash, optionNameHash) = penumbra?.GroupName is { Length: > 0 } group
            ? (ModDirectoryHash.Compute(group), ModDirectoryHash.Compute(penumbra!.OptionName))
            : ("0", "0");
        var triggerHash = penumbra != null && triggerText is { Length: > 0 } t ? ModDirectoryHash.Compute(t) : "0";
        return $"/tell {target.TellAddress} {ForceSelectKeyword} {modDirectoryHash} {groupNameHash} {optionNameHash} {triggerHash} {name}";
    }
}
