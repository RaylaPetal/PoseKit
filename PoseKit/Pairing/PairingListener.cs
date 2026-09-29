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

/// <summary>The anchor kind a capture request asks the partner to capture: 0 = none, 1 = spot,
/// 2 = furniture (the item matching FurnitureEntryId).</summary>
public readonly record struct AnchorHint(int AnchorKind, uint FurnitureEntryId, string FurnitureName);

/// <summary>A decoded pose state from a capture reply or couple relay.</summary>
public readonly record struct CapturedPoseState(PoseIdentifier Pose, PoseOffset Offset, PresetAnchor? Anchor, PenumbraLink? Penumbra);

/// <summary>The hashes carried by a forced selection.</summary>
public readonly record struct ForceSelectionHashes(string ModDirectoryHash, string GroupNameHash, string OptionNameHash, string TriggerHash)
{
    public bool HasPenumbraData => ModDirectoryHash != "0";
}

/// <summary>
/// The pairing protocol over /tell, for this session only. No server is involved.
///
/// The sender is always taken from the game's own sender field, never from the message text.
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
    private const string CoupleAnswerKeyword = "posekitcoupleanswer";
    private const string BoneAlignKeyword = "posekitalign";

    // Long enough to survive a lull, short enough that a forgotten pairing ends.
    private const long StaleTimeoutMs = 2 * 60 * 60 * 1000;

    private readonly PairingState state;

    public event Action<PartnerIdentity, string>? QueueSignalReceived;

    public event Action<PartnerIdentity, string, AnchorHint>? PartnerCaptureRequested;

    public event Action<PartnerIdentity, string, CapturedPoseState>? CoupleCaptureReplyReceived;

    /// Must be answered exactly once via AnswerCoupleRelay.
    public event Action<PartnerIdentity, string, CapturedPoseState>? CoupleRelayReceived;

    public event Action<PartnerIdentity, string, bool>? CoupleAnswerReceived;

    public event Action<PartnerIdentity>? PartnerBoneAlignStarted;

    public event Action<PartnerIdentity, string, ForceSelectionHashes>? ForceSelectionReceived;

    public PairingListener(PairingState state)
    {
        this.state = state;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose() => Plugin.ChatGui.ChatMessage -= OnChatMessage;

    /// Ends an idle pairing locally. The partner's timer ends theirs at about the same time.
    public void Tick()
    {
        if (!state.Active) return;
        if (state.TicksSinceActivity > StaleTimeoutMs)
            state.Clear();
    }

    public void InitiatePairing(PartnerIdentity target)
    {
        var inviteId = Guid.NewGuid().ToString("N")[..8];
        state.SetOutgoingInvite(target, inviteId);
        PairingSender.Send(PairingComposer.ComposeInvite(target, inviteId));
        state.Touch();
    }

    public void AcceptPending()
    {
        if (state.PendingInvite is not { } pending) return;
        PairingSender.Send(PairingComposer.ComposeAccept(pending.Sender, pending.InviteId));
        state.Activate(pending.Sender);
    }

    public void RequestPartnerCapture(string requestId, PresetAnchor? anchorHint)
    {
        if (state.Peer is not { } peer) return;
        PairingSender.Send(PairingComposer.ComposeCoupleCaptureRequest(peer, requestId, anchorHint));
        state.Touch();
    }

    public void ReplyToCoupleCapture(PartnerIdentity target, string requestId, CapturedPoseState captured)
    {
        PairingSender.Send(PairingComposer.ComposeCoupleCaptureReply(target, requestId, captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra));
        state.Touch();
    }

    public void RelayCouplePreset(string presetName, CapturedPoseState captured)
    {
        if (state.Peer is not { } peer) return;
        PairingSender.Send(PairingComposer.ComposeCoupleRelay(peer, presetName, captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra));
        state.Touch();
    }

    public void AnnounceBoneAlign()
    {
        if (state.Peer is not { } peer) return;
        PairingSender.Send(PairingComposer.ComposeBoneAlign(peer));
        state.Touch();
    }

    public void AnswerCoupleRelay(string presetName, bool accepted)
    {
        if (state.Peer is not { } peer) return;
        PairingSender.Send(PairingComposer.ComposeCoupleAnswer(peer, presetName, accepted));
        state.Touch();
    }

    public void SendOverrideToggle(bool enabled)
    {
        if (state.Peer is not { } peer) return;
        PairingSender.Send(PairingComposer.ComposeOverrideToggle(peer, enabled));
        state.Touch();
    }

    /// Clears locally and tells the partner, best effort.
    public void Unpair()
    {
        if (state.Peer is { } peer)
        {
            PairingSender.Send(PairingComposer.ComposeUnpair(peer));
            state.Touch();
        }
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
            {
                state.Touch();
                QueueSignalReceived?.Invoke(sender, name);
            }
            return;
        }

        if (text.StartsWith(CoupleCaptureReplyKeyword, StringComparison.OrdinalIgnoreCase))
        {
            // Must come before CoupleCaptureKeyword, which is a prefix of this one.
            if (!state.Active || state.Peer is not { } replyPeer || !replyPeer.Equals(sender)) return;
            var (requestId, tail) = SplitFirstToken(text[CoupleCaptureReplyKeyword.Length..].Trim());
            if (requestId.Length > 0 && TryParseCapturedState(tail, compoundParts: 2, out var captured, out _))
            {
                state.Touch();
                CoupleCaptureReplyReceived?.Invoke(sender, requestId, captured);
            }
            return;
        }

        if (text.StartsWith(CoupleCaptureKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } capturePeer || !capturePeer.Equals(sender)) return;
            if (TryParseAnchorHint(text[CoupleCaptureKeyword.Length..].Trim(), out var requestId, out var hint) && requestId.Length > 0)
            {
                state.Touch();
                PartnerCaptureRequested?.Invoke(sender, requestId, hint);
            }
            return;
        }

        if (text.StartsWith(CoupleRelayKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } relayPeer || !relayPeer.Equals(sender)) return;
            if (TryParseCapturedState(text[CoupleRelayKeyword.Length..].Trim(), compoundParts: 3, out var captured, out var presetName)
                && presetName.Length > 0)
            {
                state.Touch();
                CoupleRelayReceived?.Invoke(sender, presetName, captured);
            }
            return;
        }

        if (text.StartsWith(BoneAlignKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } alignPeer || !alignPeer.Equals(sender)) return;
            state.Touch();
            PartnerBoneAlignStarted?.Invoke(sender);
            return;
        }

        if (text.StartsWith(CoupleAnswerKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } answerPeer || !answerPeer.Equals(sender)) return;
            var (verdict, presetName) = SplitFirstToken(text[CoupleAnswerKeyword.Length..].Trim());
            if (verdict is "0" or "1" && presetName.Length > 0)
            {
                state.Touch();
                CoupleAnswerReceived?.Invoke(sender, presetName, verdict == "1");
            }
            return;
        }

        if (text.StartsWith(OverrideToggleKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var value = text[OverrideToggleKeyword.Length..].Trim();
            if (!state.Active || state.Peer is not { } togglePeer || !togglePeer.Equals(sender)) return;
            state.Touch();

            // The toggle is shared, so mirror it locally. Never reply, or the two sides would
            // ping-pong forever.
            if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                state.SetPartnerOverrideEnabled(true);
                state.SetLocalOverrideEnabled(true);
            }
            else if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                state.SetPartnerOverrideEnabled(false);
                state.SetLocalOverrideEnabled(false);
            }
            return;
        }

        if (text.StartsWith(ForceSelectKeyword, StringComparison.OrdinalIgnoreCase))
        {
            var (tokens, name) = SplitTokens(text[ForceSelectKeyword.Length..].Trim(), 4);
            if (tokens.Length == 4 && name.Length > 0 && state.Active && state.Peer is { } forcePeer && forcePeer.Equals(sender))
            {
                state.Touch();
                var hashes = new ForceSelectionHashes(tokens[0], tokens[1], tokens[2], tokens[3]);
                ForceSelectionReceived?.Invoke(sender, name, hashes);
            }
        }
    }

    /// "&lt;requestId&gt; &lt;anchorKind&gt; &lt;entryId&gt; &lt;furnitureName&gt;". Any bad field
    /// drops the tell.
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

    /// Parses PairingComposer.ComposeCapturedStateTail. Any bad field drops the tell.
    /// <paramref name="compoundParts"/> is 3 when a preset name follows, returned in
    /// <paramref name="extra"/>.
    private static bool TryParseCapturedState(string body, int compoundParts, out CapturedPoseState state, out string extra)
    {
        state = default;
        extra = "";

        var (tokens, remainder) = SplitTokens(body, 16);
        if (tokens.Length < 16) return false;
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
        var modDirectoryHash = tokens[13];
        var groupNameHash = tokens[14];
        var optionNameHash = tokens[15];

        var compound = remainder.Split('|', compoundParts);
        if (compound.Length < compoundParts) return false;
        var furnitureName = compound[0];
        var modName = compound[1];
        if (compoundParts > 2) extra = compound[2];

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

        // Hashes are stored as "#<hash>" and resolved against local mods at play time.
        PenumbraLink? penumbra = hasPenumbra != 0
            ? new PenumbraLink
            {
                ModDirectory = $"#{modDirectoryHash}",
                ModName = modName,
                GroupName = groupNameHash == "0" ? "" : $"#{groupNameHash}",
                OptionName = groupNameHash == "0" ? "" : $"#{optionNameHash}",
            }
            : null;

        state = new CapturedPoseState(new PoseIdentifier(emoteModeId, cposeState), new PoseOffset { Position = new System.Numerics.Vector3(offX, offY, offZ), Rotation = offRot }, anchor, penumbra);
        return true;
    }

    private static (string First, string Remainder) SplitFirstToken(string text)
    {
        var trimmed = text.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex < 0 ? (trimmed, "") : (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());
    }

    /// Reads up to <paramref name="count"/> leading tokens and returns the rest untouched.
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

    /// Uses the PlayerPayload when present, else the "Name Surname@World" text.
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
