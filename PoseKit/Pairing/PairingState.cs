namespace PoseKit.Pairing;

using System;

/// <summary>
/// A pairing between this client and a partner's — held in memory only, for the current game
/// session, never written to Configuration/disk and never sent to any server. Mirrors the
/// non-networked philosophy PoseKit.md already documents for /posekit sync: no relay, no persistent
/// trust store, just enough state to know who a queued couple preset should notify.
/// </summary>
public sealed class PairingState
{
    public PartnerIdentity? Peer { get; private set; }
    public bool Active { get; private set; }

    /// This side's own "override queue" checkbox.
    public bool LocalOverrideEnabled { get; private set; }

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

    public void Activate(PartnerIdentity peer)
    {
        Peer = peer;
        Active = true;
        OutgoingInvite = null;
        PendingInvite = null;
        PartnerOverrideEnabled = false;
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

    /// Only ever called from a verified toggle tell the partner actually sent — see
    /// PairingListener's OverrideToggleReceived dispatch.
    public void SetPartnerOverrideEnabled(bool enabled)
    {
        PartnerOverrideEnabled = enabled;
        Changed?.Invoke();
    }
}
