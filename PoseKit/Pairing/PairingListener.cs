namespace PoseKit.Pairing;

using System;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using PoseKit;

/// <summary>
/// Session-scoped, non-networked pairing handshake over /tell — no relay, no signing, no persistent
/// trust store (see design.md's rationale for deliberately not reusing xiv-collar's relay-assisted
/// PairingService: that solves persistent cross-session command authority, a much harder trust
/// problem than a momentary, mutual, both-present couple-pose ready-check).
///
/// Watches every incoming tell for the "posekitpair"/"posekitqueue" keywords. Sender identity always
/// comes from the message's own game-verified Sender field, never from text inside the message body.
/// </summary>
public sealed class PairingListener : IDisposable
{
    private const string InviteKeyword = "posekitpair invite";
    private const string AcceptKeyword = "posekitpair accept";
    private const string UnpairKeyword = "posekitpair unpair";
    private const string QueueKeyword = "posekitqueue";
    private const string PresetSyncKeyword = "posekitpresetsync";

    private readonly PairingState state;

    /// Raised when a "posekitqueue" readiness tell arrives from the currently-paired peer, carrying
    /// the display name of what they queued — CoupleQueueService reacts to this locally; nothing is
    /// ever sent back in response to it.
    public event Action<PartnerIdentity, string>? QueueSignalReceived;

    /// Raised when a "posekitpresetsync" tell arrives from the currently-paired peer — Plugin wires
    /// this to PresetManager.Save so the receiving side ends up with a matching preset (same pose and
    /// name, never the sender's offset). Nothing is ever sent back in response to it.
    public event Action<PartnerIdentity, PoseIdentifier, string>? PresetSyncReceived;

    public PairingListener(PairingState state)
    {
        this.state = state;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose() => Plugin.ChatGui.ChatMessage -= OnChatMessage;

    /// Inviter side, one click: sends exactly one invite tell and records it so a later accept can be
    /// matched against it, never activating pairing from an unsolicited claim.
    public void InitiatePairing(PartnerIdentity target)
    {
        var inviteId = Guid.NewGuid().ToString("N")[..8];
        state.SetOutgoingInvite(target, inviteId);
        PairingSender.Send(PairingComposer.ComposeInvite(target, inviteId));
    }

    /// Receiver side, one click: sends exactly one acknowledgement tell and activates the pairing on
    /// this side immediately — this side doesn't wait for anything further from the inviter.
    public void AcceptPending()
    {
        if (state.PendingInvite is not { } pending) return;
        PairingSender.Send(PairingComposer.ComposeAccept(pending.Sender, pending.InviteId));
        state.Activate(pending.Sender);
    }

    /// Sends a preset-sync tell to the current pairing peer, if any — no-op while unpaired. Called
    /// from the save-preset flow; see PairingComposer.ComposePresetSync for what's (and isn't) sent.
    public void SyncPreset(PoseIdentifier pose, string name)
    {
        if (state.Peer is { } peer)
            PairingSender.Send(PairingComposer.ComposePresetSync(peer, pose, name));
    }

    /// One click: clears the pairing locally and best-effort notifies the peer so they aren't left
    /// showing a stale "Paired with you" — the notice isn't required for this side's own state to be
    /// correct (Clear() already happened), so a lost/unsent tell is not a correctness problem, just a
    /// UX one for the other side.
    public void Unpair()
    {
        if (state.Peer is { } peer)
            PairingSender.Send(PairingComposer.ComposeUnpair(peer));
        state.Clear();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.TellIncoming) return;

        var text = message.Message.TextValue.Trim();
        var (senderName, senderWorld) = ExtractNameAndWorld(message.Sender);
        if (senderName is null || senderWorld is null) return;
        var sender = new PartnerIdentity(senderName, senderWorld);

        if (text.StartsWith(InviteKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var inviteId = text[InviteKeyword.Length..].Trim();
            if (inviteId.Length > 0) state.SetPendingInvite(sender, inviteId);
            return;
        }

        if (text.StartsWith(AcceptKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var inviteId = text[AcceptKeyword.Length..].Trim();
            if (state.OutgoingInvite is { } outgoing && outgoing.InviteId == inviteId && outgoing.Target.Equals(sender))
                state.Activate(sender);
            return;
        }

        if (text.StartsWith(UnpairKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (state.Active && state.Peer is { } peer && peer.Equals(sender))
                state.Clear();
            return;
        }

        if (text.StartsWith(QueueKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var name = text[QueueKeyword.Length..].Trim();
            if (name.Length > 0 && state.Active && state.Peer is { } peer && peer.Equals(sender))
                QueueSignalReceived?.Invoke(sender, name);
            return;
        }

        if (text.StartsWith(PresetSyncKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } syncPeer || !syncPeer.Equals(sender)) return;

            var (emoteModeToken, afterEmoteMode) = SplitFirstToken(text[PresetSyncKeyword.Length..].Trim());
            var (cposeToken, name) = SplitFirstToken(afterEmoteMode);
            if (name.Length == 0) return;
            if (!uint.TryParse(emoteModeToken, NumberStyles.None, CultureInfo.InvariantCulture, out var emoteModeId)) return;
            if (!byte.TryParse(cposeToken, NumberStyles.None, CultureInfo.InvariantCulture, out var cposeState)) return;

            PresetSyncReceived?.Invoke(sender, new PoseIdentifier(emoteModeId, cposeState), name);
        }
    }

    private static (string First, string Remainder) SplitFirstToken(string text)
    {
        var trimmed = text.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex < 0 ? (trimmed, "") : (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());
    }

    /// Prefers a PlayerPayload when present (structured, unambiguous); falls back to parsing the
    /// plain "Name Surname@World" text form, since not every chat type embeds a PlayerPayload for
    /// the sender. Mirrors the same approach used elsewhere for verified-sender chat parsing.
    private static (string? Name, string? World) ExtractNameAndWorld(SeString sender)
    {
        var playerPayload = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (playerPayload is not null)
            return (playerPayload.PlayerName, playerPayload.World.Value.Name.ExtractText());

        var text = sender.TextValue.Trim();
        var atIndex = text.IndexOf('@');
        return atIndex >= 0 ? (text[..atIndex].Trim(), text[(atIndex + 1)..].Trim()) : (null, null);
    }
}
