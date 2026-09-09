namespace PoseKit.Pairing;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using PoseKit.Presets;

/// <summary>Which kind of anchor a "posekitcouplecapture" request is hinting the partner should
/// capture for its own reply — see PairingComposer.ComposeCoupleCaptureRequest. AnchorKind: 0 = none,
/// 1 = spot (the partner should capture its own current spot), 2 = furniture (the partner should
/// capture its own position relative to the nearby furniture matching FurnitureEntryId, so both sides
/// anchor to the same physical item).</summary>
public readonly record struct AnchorHint(int AnchorKind, uint FurnitureEntryId, string FurnitureName);

/// <summary>A fully decoded captured pose/offset/anchor/Penumbra state — the shared payload shape
/// carried by both a "posekitcouplecapturereply" and a "posekitcoupleplay" relay. See
/// PairingComposer's ComposeCapturedStateTail for the wire shape.</summary>
public readonly record struct CapturedPoseState(PoseIdentifier Pose, PoseOffset Offset, PresetAnchor? Anchor, PenumbraLink? Penumbra);

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
    private const string OverrideToggleKeyword = "posekitoverride";
    private const string ForceSelectKeyword = "posekitforcequeue";
    private const string CoupleCaptureKeyword = "posekitcouplecapture";
    private const string CoupleCaptureReplyKeyword = "posekitcouplecapturereply";
    private const string CoupleRelayKeyword = "posekitcoupleplay";

    private readonly PairingState state;

    /// Raised when a "posekitqueue" readiness tell arrives from the currently-paired peer, carrying
    /// the display name of what they queued — CoupleQueueService reacts to this locally; nothing is
    /// ever sent back in response to it.
    public event Action<PartnerIdentity, string>? QueueSignalReceived;

    /// Raised when a "posekitcouplecapture" request arrives from the currently-paired peer — Plugin
    /// wires this to reply once with this side's own current pose/offset/anchor/Penumbra state (see
    /// ReplyToCoupleCapture), or to send nothing at all if there's nothing to capture.
    public event Action<PartnerIdentity, string, AnchorHint>? PartnerCaptureRequested;

    /// Raised when a "posekitcouplecapturereply" tell arrives — carries the request id so the
    /// requester can match it against its own in-flight save and discard anything stale/unmatched.
    public event Action<PartnerIdentity, string, CapturedPoseState>? CoupleCaptureReplyReceived;

    /// Raised when a "posekitcoupleplay" relay arrives from the currently-paired peer, naming the
    /// preset and carrying the captured partner-half state to (depending on mutual override) either
    /// prompt for accept/deny or apply immediately. Nothing is ever sent back in response.
    public event Action<PartnerIdentity, string, CapturedPoseState>? CoupleRelayReceived;

    /// Raised when a "posekitforcequeue" tell arrives from the currently-paired peer — Plugin wires
    /// this to resolve the named item against this side's own presets/discovered animations and play
    /// it if found. Nothing is ever sent back in response.
    public event Action<PartnerIdentity, string>? ForceSelectionReceived;

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

    /// Sends a capture request to the current pairing peer, if any — no-op while unpaired. Called
    /// from the save-preset flow when "include partner" is enabled; see
    /// PairingComposer.ComposeCoupleCaptureRequest for what's (and isn't) sent.
    public void RequestPartnerCapture(string requestId, PresetAnchor? anchorHint)
    {
        if (state.Peer is { } peer)
            PairingSender.Send(PairingComposer.ComposeCoupleCaptureRequest(peer, requestId, anchorHint));
    }

    /// Replies once to a capture request with this side's own captured state — never sent unprompted.
    public void ReplyToCoupleCapture(PartnerIdentity target, string requestId, CapturedPoseState captured) =>
        PairingSender.Send(PairingComposer.ComposeCoupleCaptureReply(target, requestId, captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra));

    /// Relays a captured partner half to the current pairing peer when playing a preset that carries
    /// one — no-op while unpaired. See PairingComposer.ComposeCoupleRelay.
    public void RelayCouplePreset(string presetName, CapturedPoseState captured)
    {
        if (state.Peer is { } peer)
            PairingSender.Send(PairingComposer.ComposeCoupleRelay(peer, presetName, captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra));
    }

    /// Sends this side's own override-toggle state to the current pairing peer, if any — no-op while
    /// unpaired (there's nothing to announce it to yet; Plugin re-sends it once pairing activates).
    public void SendOverrideToggle(bool enabled)
    {
        if (state.Peer is { } peer)
            PairingSender.Send(PairingComposer.ComposeOverrideToggle(peer, enabled));
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

        if (text.StartsWith(CoupleCaptureReplyKeyword, StringComparison.OrdinalIgnoreCase))
        {
            // Checked before CoupleCaptureKeyword below since it's a prefix of this longer keyword.
            if (!state.Active || state.Peer is not { } replyPeer || !replyPeer.Equals(sender)) return;
            var (requestId, tail) = SplitFirstToken(text[CoupleCaptureReplyKeyword.Length..].Trim());
            if (requestId.Length > 0 && TryParseCapturedState(tail, compoundParts: 5, out var captured, out _))
                CoupleCaptureReplyReceived?.Invoke(sender, requestId, captured);
            return;
        }

        if (text.StartsWith(CoupleCaptureKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } capturePeer || !capturePeer.Equals(sender)) return;
            if (TryParseAnchorHint(text[CoupleCaptureKeyword.Length..].Trim(), out var requestId, out var hint) && requestId.Length > 0)
                PartnerCaptureRequested?.Invoke(sender, requestId, hint);
            return;
        }

        if (text.StartsWith(CoupleRelayKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } relayPeer || !relayPeer.Equals(sender)) return;
            if (TryParseCapturedState(text[CoupleRelayKeyword.Length..].Trim(), compoundParts: 6, out var captured, out var presetName)
                && presetName.Length > 0)
                CoupleRelayReceived?.Invoke(sender, presetName, captured);
            return;
        }

        if (text.StartsWith(OverrideToggleKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var value = text[OverrideToggleKeyword.Length..].Trim();
            if (!state.Active || state.Peer is not { } togglePeer || !togglePeer.Equals(sender)) return;
            if (value.Equals("on", StringComparison.OrdinalIgnoreCase)) state.SetPartnerOverrideEnabled(true);
            else if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) state.SetPartnerOverrideEnabled(false);
            return;
        }

        if (text.StartsWith(ForceSelectKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var name = text[ForceSelectKeyword.Length..].Trim();
            if (name.Length > 0 && state.Active && state.Peer is { } forcePeer && forcePeer.Equals(sender))
                ForceSelectionReceived?.Invoke(sender, name);
        }
    }

    /// Mirrors PairingComposer.ComposeCoupleCaptureRequest's wire shape: "&lt;requestId&gt;
    /// &lt;anchorKind&gt; &lt;entryId&gt; &lt;furnitureName&gt;". Fails closed: any malformed/
    /// truncated field drops the whole tell.
    private static bool TryParseAnchorHint(string body, out string requestId, out AnchorHint hint)
    {
        hint = default;
        var (idToken, r1) = SplitFirstToken(body);
        var (anchorKindToken, r2) = SplitFirstToken(r1);
        var (entryIdToken, furnitureName) = SplitFirstToken(r2);
        requestId = idToken;

        if (!int.TryParse(anchorKindToken, NumberStyles.None, CultureInfo.InvariantCulture, out var anchorKind)) return false;
        if (!uint.TryParse(entryIdToken, NumberStyles.None, CultureInfo.InvariantCulture, out var entryId)) return false;

        hint = new AnchorHint(anchorKind, entryId, furnitureName);
        return true;
    }

    /// Mirrors PairingComposer.ComposeCapturedStateTail's wire shape exactly — see that method's doc
    /// for the field layout and free-text ordering. Fails closed: any malformed/truncated field drops
    /// the whole tell rather than guessing at a partial capture. `compoundParts` is 5 for a capture
    /// reply (no preset name) or 6 for a play relay (preset name last); `extra` carries that 6th
    /// field (the preset name) when present, empty otherwise.
    private static bool TryParseCapturedState(string body, int compoundParts, out CapturedPoseState state, out string extra)
    {
        state = default;
        extra = "";

        var (tokens, remainder) = SplitTokens(body, 13);
        if (tokens.Length < 13) return false;
        var ic = CultureInfo.InvariantCulture;

        if (!uint.TryParse(tokens[0], NumberStyles.None, ic, out var emoteModeId)) return false;
        if (!byte.TryParse(tokens[1], NumberStyles.None, ic, out var cposeState)) return false;
        if (!float.TryParse(tokens[2], NumberStyles.Float, ic, out var offX)) return false;
        if (!float.TryParse(tokens[3], NumberStyles.Float, ic, out var offY)) return false;
        if (!float.TryParse(tokens[4], NumberStyles.Float, ic, out var offZ)) return false;
        if (!float.TryParse(tokens[5], NumberStyles.Float, ic, out var offRot)) return false;
        if (!int.TryParse(tokens[6], NumberStyles.None, ic, out var anchorKind)) return false;
        if (!uint.TryParse(tokens[7], NumberStyles.None, ic, out var anchorNum)) return false;
        if (!float.TryParse(tokens[8], NumberStyles.Float, ic, out var ax)) return false;
        if (!float.TryParse(tokens[9], NumberStyles.Float, ic, out var ay)) return false;
        if (!float.TryParse(tokens[10], NumberStyles.Float, ic, out var az)) return false;
        if (!float.TryParse(tokens[11], NumberStyles.Float, ic, out var arot)) return false;
        if (!int.TryParse(tokens[12], NumberStyles.None, ic, out var hasPenumbra)) return false;

        var compound = remainder.Split('|', compoundParts);
        if (compound.Length < compoundParts) return false;
        var (furnitureName, groupName, optionName, modName, modDirectory) =
            (compound[0], compound[1], compound[2], compound[3], compound[4]);
        if (compoundParts > 5) extra = compound[5];

        PresetAnchor? anchor = anchorKind switch
        {
            1 => PresetAnchor.FromSpot(new LocationAnchor
            {
                TerritoryType = anchorNum, Position = new System.Numerics.Vector3(ax, ay, az), Rotation = arot,
            }),
            2 => PresetAnchor.FromFurniture(new FurnitureAnchor
            {
                EntryId = anchorNum, FurnitureName = furnitureName,
                RelativePosition = new System.Numerics.Vector3(ax, ay, az), RelativeRotation = arot,
            }),
            _ => null,
        };

        PenumbraLink? penumbra = hasPenumbra != 0
            ? new PenumbraLink { ModDirectory = modDirectory, ModName = modName, GroupName = groupName, OptionName = optionName }
            : null;
        if (penumbra != null && groupName.Length > 0)
            penumbra.GroupSelections[groupName] = [optionName];

        state = new CapturedPoseState(new PoseIdentifier(emoteModeId, cposeState), new PoseOffset { Position = new System.Numerics.Vector3(offX, offY, offZ), Rotation = offRot }, anchor, penumbra);
        return true;
    }

    private static (string First, string Remainder) SplitFirstToken(string text)
    {
        var trimmed = text.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex < 0 ? (trimmed, "") : (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());
    }

    /// Reads up to `count` leading whitespace-separated tokens, leaving everything after the last one
    /// (including any further whitespace-separated words) as the untouched remainder — used ahead of
    /// a trailing '|'-joined compound whose own fields may themselves contain spaces.
    private static (string[] Tokens, string Remainder) SplitTokens(string text, int count)
    {
        var tokens = new List<string>(count);
        var remaining = text;
        for (var i = 0; i < count; i++)
        {
            var (token, rest) = SplitFirstToken(remaining);
            if (token.Length == 0) break;
            tokens.Add(token);
            remaining = rest;
        }
        return (tokens.ToArray(), remaining);
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
