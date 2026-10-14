namespace PoseKit.Pairing;

using System;

/// <summary>
/// The current pairing, held in memory for this session only.
///
/// Override and solo play are standing local preferences that survive a re-pair. The activity
/// timestamp is used to unpair a pairing that's gone idle.
/// </summary>
public sealed class PairingState
{
    public PartnerIdentity? Peer { get; private set; }
    public bool Active { get; private set; }

    private long lastActivityTicks;

    public long TicksSinceActivity => Environment.TickCount64 - lastActivityTicks;

    /// Call on every pairing message sent or received, never on purely local actions.
    public void Touch() => lastActivityTicks = Environment.TickCount64;

    public bool LocalOverrideEnabled { get; private set; }

    /// Plays this side's clicks as if unpaired. Never announced to the partner.
    public bool SoloPlayEnabled { get; private set; }

    /// Only set from the partner's own toggle tell; reset on every new pairing.
    public bool PartnerOverrideEnabled { get; private set; }

    public bool MutualOverrideActive => Active && LocalOverrideEnabled && PartnerOverrideEnabled;

    /// Kept so an accept is only honored for an invite this side actually sent.
    public (PartnerIdentity Target, string InviteId)? OutgoingInvite { get; private set; }

    /// Never auto-accepted.
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

    /// Local only; a late accept is then ignored as unsolicited.
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

    public void SetLocalOverrideEnabled(bool enabled)
    {
        LocalOverrideEnabled = enabled;
        Changed?.Invoke();
    }

    public void SetSoloPlayEnabled(bool enabled)
    {
        SoloPlayEnabled = enabled;
        Changed?.Invoke();
    }

    public void SetPartnerOverrideEnabled(bool enabled)
    {
        PartnerOverrideEnabled = enabled;
        Changed?.Invoke();
    }
}
