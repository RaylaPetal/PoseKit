namespace PoseKit.Pairing;

using System;

/// <summary>
/// Holds a relayed couple-preset play awaiting this side's explicit accept/deny, unless mutual
/// override is active for the current pairing — in which case it's applied immediately with no
/// prompt at all (see AutoApply). Mirrors CoupleQueueService's Tick-driven timeout shape.
/// </summary>
public sealed class CoupleRelayInbox : IDisposable
{
    // Matches CoupleQueueService.QueueTimeoutMs — same "long enough to notice, short enough not to
    // surprise-fire later" reasoning, just gating a prompt instead of an auto-play.
    private const long PromptTimeoutMs = 60_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;

    private long receivedAt;

    public PartnerIdentity? Sender { get; private set; }
    public string? PresetName { get; private set; }
    public CapturedPoseState? Captured { get; private set; }

    public event Action? Changed;

    /// Raised instead of setting a pending prompt when mutual override is active — the caller should
    /// apply the captured state immediately.
    public event Action<CapturedPoseState>? AutoApply;

    public CoupleRelayInbox(PairingState pairingState, PairingListener pairingListener)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;
        pairingListener.CoupleRelayReceived += OnReceived;
        pairingState.Changed += OnPairingStateChanged;
    }

    public void Dispose()
    {
        pairingListener.CoupleRelayReceived -= OnReceived;
        pairingState.Changed -= OnPairingStateChanged;
    }

    private void OnReceived(PartnerIdentity sender, string presetName, CapturedPoseState captured)
    {
        if (pairingState.MutualOverrideActive)
        {
            AutoApply?.Invoke(captured);
            return;
        }

        Sender = sender;
        PresetName = presetName;
        Captured = captured;
        receivedAt = Environment.TickCount64;
        Changed?.Invoke();
    }

    /// Clears the pending prompt and hands back the captured state to apply — null if there was
    /// nothing pending.
    public CapturedPoseState? Accept()
    {
        var captured = Captured;
        Clear();
        return captured;
    }

    public void Deny() => Clear();

    private void OnPairingStateChanged()
    {
        if (!pairingState.Active) Clear();
    }

    private void Clear()
    {
        Sender = null;
        PresetName = null;
        Captured = null;
        Changed?.Invoke();
    }

    /// Called every framework tick: dismisses an unanswered prompt once it's been open longer than
    /// the timeout, treated the same as an explicit deny.
    public void Tick()
    {
        if (Captured == null) return;
        if (Environment.TickCount64 - receivedAt > PromptTimeoutMs) Clear();
    }
}
