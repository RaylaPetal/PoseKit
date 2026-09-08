namespace PoseKit.Pairing;

using System.Globalization;
using System.Numerics;
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
    /// entry under the same name. Only the anchor (the shared physical context — which furniture, or
    /// which world spot) and the name travel across; pose, offset, and Penumbra link are deliberately
    /// NOT sent — the receiving side captures those from its *own* currently-playing pose instead
    /// (see PairingListener's PresetSyncReceived wiring in Plugin.cs), since a couple pose typically
    /// has each side already playing their own different half (their own mod/option, their own
    /// offset) by the time one of them saves it as a preset — sending this side's own pose/offset
    /// across would silently overwrite the correct thing already sitting on the receiver's client.
    ///
    /// Wire shape: "&lt;anchorKind 0/1/2&gt; &lt;num1&gt; &lt;num2&gt; &lt;num3&gt; &lt;num4&gt;
    /// &lt;num5&gt; &lt;furnitureName&gt;|&lt;name&gt;" — num1..num5 are TerritoryType/EntryId,
    /// Position.X/Y/Z (or RelativePosition), Rotation (or RelativeRotation), all 0 when anchorKind is
    /// 0 (none).
    public static string ComposePresetSync(PartnerIdentity target, PresetAnchor? anchor, string name)
    {
        var anchorKind = anchor?.Spot != null ? 1 : anchor?.Furniture != null ? 2 : 0;
        var num1 = anchor?.Spot?.TerritoryType ?? anchor?.Furniture?.EntryId ?? 0u;
        var position = anchor?.Spot?.Position ?? anchor?.Furniture?.RelativePosition ?? Vector3.Zero;
        var rotation = anchor?.Spot?.Rotation ?? anchor?.Furniture?.RelativeRotation ?? 0f;

        var compound = string.Join('|', anchor?.Furniture?.FurnitureName ?? "", name);

        return $"/tell {target.TellAddress} {PresetSyncKeyword} {anchorKind} " +
               $"{num1.ToString(CultureInfo.InvariantCulture)} {position.X.ToString(CultureInfo.InvariantCulture)} " +
               $"{position.Y.ToString(CultureInfo.InvariantCulture)} {position.Z.ToString(CultureInfo.InvariantCulture)} " +
               $"{rotation.ToString(CultureInfo.InvariantCulture)} {compound}";
    }
}
