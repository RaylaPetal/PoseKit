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
        Changed?.Invoke();
    }

    public void Clear()
    {
        Peer = null;
        Active = false;
        OutgoingInvite = null;
        PendingInvite = null;
        Changed?.Invoke();
    }
}
