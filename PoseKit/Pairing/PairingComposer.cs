namespace PoseKit.Pairing;

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
    private const string PresetSyncKeyword = "posekitpresetsync";
    private const string OverrideToggleKeyword = "posekitoverride";
    private const string ForceSelectKeyword = "posekitforcequeue";

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
    /// entry under the same name. Only the anchor *kind* (none/spot/furniture) and, for furniture,
    /// which item — never a captured position/rotation — plus the name travel across; pose, offset,
    /// Penumbra link, and any world-space anchor coordinates are deliberately NOT sent. The receiving
    /// side captures its own current pose/offset/Penumbra link *and* its own anchor (its own current
    /// spot, or its own position relative to a matching nearby furniture item) instead (see
    /// PairingListener's PresetSyncReceived wiring in Plugin.cs) — a couple pose typically has each
    /// side already sitting/standing in its own different spot on the same furniture (or standing
    /// near, not on top of, each other for a spot anchor) by the time one of them saves it as a
    /// preset, so sending this side's own anchor across would anchor the receiver to *this* side's
    /// spot instead of their own.
    ///
    /// Wire shape: "&lt;anchorKind 0/1/2&gt; &lt;furnitureEntryId&gt; &lt;furnitureName&gt;|&lt;name&gt;"
    /// — furnitureEntryId is 0 and furnitureName is empty unless anchorKind is 2 (furniture).
    public static string ComposePresetSync(PartnerIdentity target, PresetAnchor? anchor, string name)
    {
        var anchorKind = anchor?.Spot != null ? 1 : anchor?.Furniture != null ? 2 : 0;
        var entryId = anchor?.Furniture?.EntryId ?? 0u;
        var compound = string.Join('|', anchor?.Furniture?.FurnitureName ?? "", name);

        return $"/tell {target.TellAddress} {PresetSyncKeyword} {anchorKind} " +
               $"{entryId.ToString(CultureInfo.InvariantCulture)} {compound}";
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
