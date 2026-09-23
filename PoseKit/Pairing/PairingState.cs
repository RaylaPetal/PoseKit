namespace PoseKit.Pairing;

using System;

/// <summary>
/// A pairing between this client and a partner's — held in memory only, for the current game
/// session, never written to Configuration/disk and never sent to any server. Mirrors the
/// non-networked philosophy PoseKit.md already documents for /posekit sync: no relay, no persistent
/// trust store, just enough state to know who a queued couple preset should notify.
///
/// Also tracks two independent local-only standing preferences that survive a re-pair within the
/// same session — LocalOverrideEnabled (mutual force-select opt-in) and SoloPlayEnabled (this side's
/// own unconditional bypass) — plus an activity timestamp (Touch()/TicksSinceActivity) PairingListener
/// uses to auto-unpair a pairing that's gone stale. See design.md (pairing-solo-play-idle-unpair)
/// Decisions 3-5 for why the touch points live outside this class rather than a central hook.
/// </summary>
public sealed class PairingState
{
    public PartnerIdentity? Peer { get; private set; }
    public bool Active { get; private set; }

    private long lastActivityTicks;

    /// Elapsed time since the last pairing-protocol message sent or received with the current peer
    /// (or since Activate(), whichever is most recent) — what PairingListener.Tick checks against its
    /// stale-pairing timeout. Meaningless while !Active, but harmless to read either way.
    public long TicksSinceActivity => Environment.TickCount64 - lastActivityTicks;

    /// Called alongside every actual pairing-protocol send/receive with the current peer — never from
    /// a purely local action (e.g. SoloPlayEnabled itself, or dismissing an invite) — so a solo-play
    /// bypassed click, which sends nothing, correctly does not keep an otherwise-idle pairing alive.
    public void Touch() => lastActivityTicks = Environment.TickCount64;

    /// This side's own "override queue" checkbox.
    public bool LocalOverrideEnabled { get; private set; }

    /// This side's own "solo play" checkbox — bypasses queueing, force-select, and couple-preset
    /// relay entirely for this side's own clicks, playing exactly as if unpaired. Purely local: never
    /// announced to the partner, never mirrored from theirs. Like LocalOverrideEnabled, it's a
    /// standing local preference — untouched by Activate()/Clear() — not tied to any one partner.
    public bool SoloPlayEnabled { get; private set; }

    /// Learned from the partner's own toggle tell — never assumed, always something they actually
    /// sent. Reset on Clear()/Activate() so a stale reading from a previous pairing can never leak
    /// into a new one.
    public bool PartnerOverrideEnabled { get; private set; }

    /// What force-select actually gates on: both sides confirmed on to each other, not just this
    /// side's own checkbox.
    public bool MutualOverrideActive => Active && LocalOverrideEnabled && PartnerOverrideEnabled;

    /// An invite this side sent and hasn't heard an accept for yet — kept so a stray/late/forged
    /// accept tell can be matched against something this side actually sent, rather than activating
    /// from an unsolicited claim.
    public (PartnerIdentity Target, string InviteId)? OutgoingInvite { get; private set; }

    /// An invite received from someone else, waiting on this side's explicit Accept click — never
    /// auto-accepted.
    public (PartnerIdentity Sender, string InviteId)? PendingInvite { get; private set; }

    public event Action? Changed;

    public void SetOutgoingInvite(PartnerIdentity target, string inviteId)
    {
        OutgoingInvite = (target, inviteId);
        Changed?.Invoke();
    }

    public void SetPendingInvite(PartnerIdentity sender, string inviteId)
    {
        PendingInvite = (sender, inviteId);
        Changed?.Invoke();
    }

    public void DismissPendingInvite()
    {
        PendingInvite = null;
        Changed?.Invoke();
    }

    /// This side backing out of an invite it sent — e.g. a mistyped name/world with no accept
    /// coming, and no other way back to the entry field otherwise. Purely local: the target never
    /// gets told, since it never activates anything on their side by itself (only their own Accept
    /// click, matched against this exact invite id, would) — a late accept arriving after this just
    /// finds no matching OutgoingInvite and is ignored, same as any other unsolicited accept.
    public void CancelOutgoingInvite()
    {
        OutgoingInvite = null;
        Changed?.Invoke();
    }

    public void Activate(PartnerIdentity peer)
    {
        Peer = peer;
        Active = true;
        OutgoingInvite = null;
        PendingInvite = null;
        PartnerOverrideEnabled = false;
        Touch();
        Changed?.Invoke();
    }

    public void Clear()
    {
        Peer = null;
        Active = false;
        OutgoingInvite = null;
        PendingInvite = null;
        PartnerOverrideEnabled = false;
        Changed?.Invoke();
    }

    /// This side's own checkbox — persists across a re-pair (it's a standing preference, not tied to
    /// any one partner), unlike PartnerOverrideEnabled.
    public void SetLocalOverrideEnabled(bool enabled)
    {
        LocalOverrideEnabled = enabled;
        Changed?.Invoke();
    }

    /// This side's own checkbox — same standing-preference lifecycle as SetLocalOverrideEnabled.
    public void SetSoloPlayEnabled(bool enabled)
    {
        SoloPlayEnabled = enabled;
        Changed?.Invoke();
    }

    /// Only ever called from a verified toggle tell the partner actually sent — see
    /// PairingListener's OverrideToggleReceived dispatch.
    public void SetPartnerOverrideEnabled(bool enabled)
    {
        PartnerOverrideEnabled = enabled;
        Changed?.Invoke();
    }
}
