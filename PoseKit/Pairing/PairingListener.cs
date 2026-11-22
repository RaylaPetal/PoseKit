namespace PoseKit.Pairing;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Presets;
using PoseKit.Relay;

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
/// One role of a library animation: its mod, group, option and trigger hashes ("0" for the implicit
/// group), plus the hash of the option's normalized name, so a partner with a sibling mod that
/// numbers its options differently can still find it.
/// </summary>
public readonly record struct RoleHashes(string ModDirectoryHash, string GroupNameHash, string OptionNameHash, string TriggerHash,
    string OptionKeyHash);

/// <summary>The answer to a couple relay. The numbers are the wire values.</summary>
public enum CoupleAnswer
{
    Declined = 0,
    Accepted = 1,

    /// Role relay only: the partner doesn't have the animation.
    Missing = 2,

    /// Role relay only: the prompt is showing. Tells the sender the partner's PoseKit understood the
    /// relay, since an older one ignores it and never answers.
    Received = 3,
}

/// <summary>
/// The pairing protocol, carried by PoseKit's relay (see Relay/). Invites and acceptances go to a character's inbox and
/// are trusted only when the claimed character is standing nearby and targeting this player, or is a remembered partner
/// on the same device key. Every later line is sealed to that partner's key, so the sender of a line is always the
/// verified partner, never something the text claims. Pairing state is for this session only.
/// </summary>
public sealed class PairingListener : IDisposable
{
    /// Only recognised in tells from older versions, to tell the player they need to update.
    private const string LegacyInviteKeyword = "posekitpair invite";
    private const string ConfirmKeyword = "posekitpair confirm";
    private const string UnpairKeyword = "posekitpair unpair";
    private const string QueueKeyword = "posekitqueue";
    private const string OverrideToggleKeyword = "posekitoverride";
    private const string ForceSelectKeyword = "posekitforcequeue";
    private const string CoupleCaptureKeyword = "posekitcouplecapture";
    private const string CoupleCaptureReplyKeyword = "posekitcouplecapturereply";
    private const string CoupleRelayKeyword = "posekitcoupleplay";
    private const string CoupleAnswerKeyword = "posekitcoupleanswer";
    private const string BoneAlignKeyword = "posekitalign";
    private const string RoleRelayKeyword = "posekitrole";
    private const string RelayCancelKeyword = "posekitcouplecancel";

    // Long enough to survive a lull, short enough that a forgotten pairing ends.
    private const long StaleTimeoutMs = 2 * 60 * 60 * 1000;

    // How long a claimed character has to be seen targeting this player before an invite or acceptance is trusted.
    private const long VerifyWindowMs = 10_000;
    // How long the accepting side waits for the inviter's sealed confirm.
    private const long ConfirmWindowMs = 15_000;

    private readonly PairingState state;
    private readonly RelayService relay;
    private readonly Configuration configuration;

    /// This side's nonce for the invite it sent; mixed into the pair key.
    private byte[]? outgoingNonce;

    /// An invite or acceptance waiting for its sender to be seen targeting this player.
    private sealed record Claim(string FromId, InviteBody Body, long Deadline);
    private Claim? verifyingInvite;
    private Claim? verifyingAccept;

    /// The verified invite behind state.PendingInvite, kept for its key and nonce.
    private Claim? pendingInvite;

    /// After accepting: the inviter's identity until their sealed confirm arrives.
    private (PartnerIdentity Peer, EcPublicKey Key, long Deadline)? awaitingConfirm;

    /// Senders already told they need to update this session, so a repeated old invite stays quiet.
    private readonly HashSet<string> legacyNoticeShown = new(StringComparer.OrdinalIgnoreCase);

    public event Action<PartnerIdentity, string>? QueueSignalReceived;

    public event Action<PartnerIdentity, string, AnchorHint>? PartnerCaptureRequested;

    public event Action<PartnerIdentity, string, CapturedPoseState>? CoupleCaptureReplyReceived;

    /// Must be answered exactly once via AnswerCoupleRelay.
    public event Action<PartnerIdentity, string, CapturedPoseState>? CoupleRelayReceived;

    public event Action<PartnerIdentity, string, CoupleAnswer>? CoupleAnswerReceived;

    /// Must be answered exactly once via AnswerCoupleRelay.
    public event Action<PartnerIdentity, string, RoleHashes>? RoleRelayReceived;

    /// The partner withdrew the relay with this name.
    public event Action<PartnerIdentity, string>? RelayCancelReceived;

    public event Action<PartnerIdentity>? PartnerBoneAlignStarted;

    public event Action<PartnerIdentity, string, ForceSelectionHashes>? ForceSelectionReceived;

    /// Only the partner's own unpair; an idle timeout or this side's Unpair doesn't raise it.
    public event Action<PartnerIdentity>? PartnerUnpaired;

    public PairingListener(PairingState state, RelayService relay, Configuration configuration)
    {
        this.state = state;
        this.relay = relay;
        this.configuration = configuration;
        PairingTransport.Configure(relay, state);
        relay.Messenger.LineReceived += OnLineReceived;
        relay.Messenger.InviteReceived += OnInviteReceived;
        state.Changed += OnStateChanged;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        relay.Messenger.LineReceived -= OnLineReceived;
        relay.Messenger.InviteReceived -= OnInviteReceived;
        state.Changed -= OnStateChanged;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
    }

    /// Ends an idle pairing locally (the partner's timer ends theirs at about the same time), and settles claims
    /// waiting for their targeting check.
    public void Tick()
    {
        if (state.Active && state.TicksSinceActivity > StaleTimeoutMs)
            state.Clear();

        var now = Environment.TickCount64;
        if (verifyingInvite is { } invite)
        {
            if (state.Active) verifyingInvite = null;
            else if (Verified(invite)) ShowInvite(invite);
            else if (now > invite.Deadline) verifyingInvite = null; // a claim nobody backed up: dropped silently
        }

        if (verifyingAccept is { } accept)
        {
            if (state.OutgoingInvite is not { } outgoing || !outgoing.Target.Matches(accept.Body.Name, accept.Body.World)) verifyingAccept = null;
            else if (Verified(accept)) CompleteAsInviter(accept);
            else if (now > accept.Deadline)
            {
                verifyingAccept = null;
                state.CancelOutgoingInvite();
                Echo($"Couldn't verify {accept.Body.Name}. Ask them to target you and accept again.");
            }
        }

        if (awaitingConfirm is { } waiting && now > waiting.Deadline)
        {
            awaitingConfirm = null;
            relay.Messenger.Session = null;
            Echo($"Couldn't confirm the pairing with {waiting.Peer.Name}. Ask them to invite you again while you target them.");
        }
    }

    /// Sends an invite to a nearby player (targeting them, so their client can verify it's really this player) or to a
    /// remembered partner anywhere.
    public void InitiatePairing(PartnerIdentity target)
    {
        if (!relay.Connected)
        {
            PairingTransport.Notice("unavailable");
            return;
        }
        if (relay.Self is not { } self) return;

        var nearby = FindNearby(target);
        if (nearby == null && RememberedPartners.Find(configuration, target) == null)
        {
            Echo($"{target.Name} isn't nearby. Stand near them to pair the first time; after that you can pair again from anywhere.");
            return;
        }
        if (nearby != null) Plugin.TargetManager.Target = nearby;

        var inviteId = Guid.NewGuid().ToString("N")[..8];
        outgoingNonce = RelayCrypto.RandomBytes(16);
        state.SetOutgoingInvite(target, inviteId);
        relay.Messenger.SendInvite(RelayCrypto.InboxId(target.TellAddress), Body(InviteBody.InviteKind, inviteId, self, outgoingNonce), result =>
        {
            if (result == "delivered" || state.OutgoingInvite?.InviteId != inviteId) return;
            state.CancelOutgoingInvite();
            if (result == "offline")
                Echo($"{target.Name} isn't online with a PoseKit that can pair. If they're on, they need to update PoseKit.");
            else
                PairingTransport.Notice(result);
        });
        Echo($"Pairing invite sent to {target}.");
    }

    public void AcceptPending()
    {
        if (state.PendingInvite is not { } pending || pendingInvite is not { } invite) return;
        if (!relay.Connected || relay.Self is not { } self)
        {
            PairingTransport.Notice("unavailable");
            return;
        }

        // Targeting the inviter lets their client verify this acceptance came from this character.
        if (FindNearby(pending.Sender) is { } nearby) Plugin.TargetManager.Target = nearby;

        var nonce = RelayCrypto.RandomBytes(16);
        var key = PeerCrypto.DeriveKey(relay.Identity.Key, relay.Identity.Id, nonce, invite.Body.Key, invite.FromId,
            RelayCrypto.Base64UrlDecode(invite.Body.Nonce));
        relay.Messenger.Session = new PeerSession(invite.FromId, invite.Body.Key, key);
        awaitingConfirm = (pending.Sender, invite.Body.Key, Environment.TickCount64 + ConfirmWindowMs);
        pendingInvite = null;
        state.DismissPendingInvite();

        relay.Messenger.SendInvite(RelayCrypto.InboxId(pending.Sender.TellAddress), Body(InviteBody.AcceptKind, pending.InviteId, self, nonce), result =>
        {
            if (result == "delivered" || awaitingConfirm == null) return;
            awaitingConfirm = null;
            relay.Messenger.Session = null;
            if (result == "offline") Echo($"{pending.Sender.Name} isn't online any more.");
            else PairingTransport.Notice(result);
        });
        Echo($"Accepted {pending.Sender.Name}'s invite. Confirming...");
    }

    public void RequestPartnerCapture(string requestId, PresetAnchor? anchorHint)
    {
        if (state.Peer is null) return;
        PairingTransport.Send(PairingComposer.ComposeCoupleCaptureRequest(requestId, anchorHint));
        state.Touch();
    }

    public void ReplyToCoupleCapture(PartnerIdentity target, string requestId, CapturedPoseState captured)
    {
        if (state.Peer is not { } peer || !peer.Equals(target)) return;
        PairingTransport.Send(PairingComposer.ComposeCoupleCaptureReply(requestId, captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra));
        state.Touch();
    }

    public void RelayCouplePreset(string presetName, CapturedPoseState captured)
    {
        if (state.Peer is null) return;
        PairingTransport.Send(PairingComposer.ComposeCoupleRelay(presetName, captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra));
        state.Touch();
    }

    public void AnnounceBoneAlign()
    {
        if (state.Peer is null) return;
        PairingTransport.Send(PairingComposer.ComposeBoneAlign());
        state.Touch();
    }

    public void AnswerCoupleRelay(string presetName, CoupleAnswer answer)
    {
        if (state.Peer is null) return;
        PairingTransport.Send(PairingComposer.ComposeCoupleAnswer(presetName, answer));
        state.Touch();
    }

    public void RelayRole(string name, RoleHashes role)
    {
        if (state.Peer is null) return;
        PairingTransport.Send(PairingComposer.ComposeRoleRelay(name, role));
        state.Touch();
    }

    /// Withdraws a relay the partner hasn't answered yet.
    public void CancelRelay(string name)
    {
        if (state.Peer is null) return;
        PairingTransport.Send(PairingComposer.ComposeRelayCancel(name));
        state.Touch();
    }

    public void SendOverrideToggle(bool enabled)
    {
        if (state.Peer is null) return;
        PairingTransport.Send(PairingComposer.ComposeOverrideToggle(enabled));
        state.Touch();
    }

    /// Clears locally and tells the partner, best effort.
    public void Unpair()
    {
        if (state.Peer is { } peer)
        {
            PairingTransport.Send(PairingComposer.ComposeUnpair());
            Echo($"Ended the pairing with {peer.Name}.");
        }
        state.Clear();
    }

    // ---- relay invites ------------------------------------------------------------------------------------------

    private InviteBody Body(string kind, string inviteId, PartnerIdentity self, byte[] nonce)
    {
        var key = relay.Identity.PublicKey;
        return new InviteBody
        {
            Kind = kind, InviteId = inviteId, Name = self.Name, World = self.World,
            Nonce = RelayCrypto.Base64UrlEncode(nonce), X = key.X, Y = key.Y,
        };
    }

    private void OnInviteReceived(string fromId, InviteBody body)
    {
        var claimed = new PartnerIdentity(body.Name, body.World);
        if (relay.Self is { } self && self.Equals(claimed)) return;
        var claim = new Claim(fromId, body, Environment.TickCount64 + VerifyWindowMs);

        if (body.Kind == InviteBody.InviteKind)
        {
            if (state.Active || awaitingConfirm != null) return;
            // Two devices claiming one character at once: one of them is lying, and there's no telling which.
            if (verifyingInvite is { } other && other.FromId != fromId && claimed.Matches(other.Body.Name, other.Body.World))
            {
                verifyingInvite = null;
                Echo($"Got two pairing invites claiming to be {claimed.Name}, so neither was trusted. Ask them to invite you again.");
                return;
            }
            verifyingInvite = claim;
            if (Verified(claim)) ShowInvite(claim);
        }
        else if (body.Kind == InviteBody.AcceptKind)
        {
            if (state.OutgoingInvite is not { } outgoing || outgoing.InviteId != body.InviteId || !outgoing.Target.Equals(claimed)) return;
            if (verifyingAccept is { } other && other.FromId != fromId)
            {
                verifyingAccept = null;
                state.CancelOutgoingInvite();
                Echo($"Got two answers claiming to be {claimed.Name}, so neither was trusted. Invite them again.");
                return;
            }
            verifyingAccept = claim;
            if (Verified(claim)) CompleteAsInviter(claim);
        }
    }

    /// A remembered partner on the same device key, or the claimed character standing nearby targeting this player.
    private bool Verified(Claim claim)
    {
        var claimed = new PartnerIdentity(claim.Body.Name, claim.Body.World);
        if (RememberedPartners.Trusts(configuration, claimed, claim.FromId)) return true;
        var self = Plugin.ObjectTable.LocalPlayer;
        return self != null && FindNearby(claimed) is { } character && character.TargetObjectId == self.GameObjectId;
    }

    private void ShowInvite(Claim invite)
    {
        verifyingInvite = null;
        pendingInvite = invite;
        var sender = new PartnerIdentity(invite.Body.Name, invite.Body.World);
        state.SetPendingInvite(sender, invite.Body.InviteId);
        Echo($"{sender.Name} invited you to pair. Accept in the notification, the library (/posekit lib) or PoseKit's pairing panel.");
    }

    private void CompleteAsInviter(Claim accept)
    {
        verifyingAccept = null;
        if (outgoingNonce is not { } nonce || state.OutgoingInvite is not { } outgoing) return;
        var key = PeerCrypto.DeriveKey(relay.Identity.Key, relay.Identity.Id, nonce, accept.Body.Key, accept.FromId,
            RelayCrypto.Base64UrlDecode(accept.Body.Nonce));
        relay.Messenger.Session = new PeerSession(accept.FromId, accept.Body.Key, key);
        // Confirm before activating: activation can send lines (an override already on), and the accepting side only
        // takes lines once the confirm has paired it.
        PairingTransport.Send(PairingComposer.ComposeConfirm());
        state.Activate(outgoing.Target);
        RememberedPartners.Remember(configuration, outgoing.Target, accept.Body.Key);
        Echo($"Paired with {outgoing.Target.Name}.");
    }

    private void OnStateChanged()
    {
        if (!state.Active && awaitingConfirm == null) relay.Messenger.Session = null;
        if (state.PendingInvite == null) pendingInvite = null;
    }

    /// The character by name and home world, if it's loaded nearby.
    private static IPlayerCharacter? FindNearby(PartnerIdentity identity)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter player && PartnerIdentity.FromCharacter(player).Equals(identity) &&
                player.GameObjectId != Plugin.ObjectTable.LocalPlayer?.GameObjectId)
                return player;
        }
        return null;
    }

    /// Whether this character could receive an invite now: loaded nearby, or remembered.
    public bool CanReach(PartnerIdentity identity) =>
        FindNearby(identity) != null || RememberedPartners.Find(configuration, identity) != null;

    /// A meaningful pairing event, in place of the hidden tell that carried it.
    private static void Echo(string text) => Plugin.ChatGui.Print($"[PoseKit] {text}");

    /// Every PoseKit keyword starts with this, newer ones this version doesn't know included.
    private const string ProtocolPrefix = "posekit";

    /// <summary>
    /// Older PoseKit versions still pair over tells. Those tells are hidden, never answered, and an invite gets one
    /// local line saying the sender needs to update.
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        var sent = message.LogKind == XivChatType.TellOutgoing;
        if (!sent && message.LogKind != XivChatType.TellIncoming) return;

        var text = message.Message.TextValue.Trim();
        if (!text.StartsWith(ProtocolPrefix, StringComparison.OrdinalIgnoreCase)) return;
        message.PreventOriginal();
        if (sent || !text.StartsWith(LegacyInviteKeyword, StringComparison.OrdinalIgnoreCase)) return;

        var (senderName, senderWorld) = ExtractNameAndWorld(message.Sender);
        if (senderName is null || senderWorld is null || !legacyNoticeShown.Add($"{senderName}@{senderWorld}")) return;
        Echo($"{senderName} tried to pair from an older PoseKit. Ask them to update PoseKit to pair with you.");
    }

    /// A sealed line from the session's partner. The sender is the partner whose key opened it.
    private void OnLineReceived(PeerSession session, string text)
    {
        text = text.Trim();
        if (!text.StartsWith(ProtocolPrefix, StringComparison.OrdinalIgnoreCase)) return;

        if (text.Equals(ConfirmKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (awaitingConfirm is not { } waiting || session.PeerId != relay.Messenger.Session?.PeerId) return;
            awaitingConfirm = null;
            state.Activate(waiting.Peer);
            RememberedPartners.Remember(configuration, waiting.Peer, waiting.Key);
            Echo($"Paired with {waiting.Peer.Name}.");
            return;
        }

        if (state.Peer is not { } sender) return;

        if (text.StartsWith(UnpairKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (state.Active && state.Peer is { } peer && peer.Equals(sender))
            {
                state.Clear();
                Echo($"{sender.Name} ended the pairing.");
                PartnerUnpaired?.Invoke(sender);
            }
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
            if (verdict is "0" or "1" or "2" or "3" && presetName.Length > 0)
            {
                state.Touch();
                CoupleAnswerReceived?.Invoke(sender, presetName, (CoupleAnswer)(verdict[0] - '0'));
            }
            return;
        }

        if (text.StartsWith(RelayCancelKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } cancelPeer || !cancelPeer.Equals(sender)) return;
            var cancelledName = text[RelayCancelKeyword.Length..].Trim();
            if (cancelledName.Length > 0)
            {
                state.Touch();
                RelayCancelReceived?.Invoke(sender, cancelledName);
            }
            return;
        }

        if (text.StartsWith(RoleRelayKeyword, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Active || state.Peer is not { } rolePeer || !rolePeer.Equals(sender)) return;
            var (tokens, name) = SplitTokens(text[RoleRelayKeyword.Length..].Trim(), 5);
            if (tokens.Length == 5 && name.Length > 0)
            {
                state.Touch();
                RoleRelayReceived?.Invoke(sender, name, new RoleHashes(tokens[0], tokens[1], tokens[2], tokens[3], tokens[4]));
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
                Echo($"{sender.Name} turned override on.");
            }
            else if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                state.SetPartnerOverrideEnabled(false);
                state.SetLocalOverrideEnabled(false);
                Echo($"{sender.Name} turned override off.");
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
