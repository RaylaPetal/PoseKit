namespace PoseKit.Pairing;

using System;

/// <summary>
/// Holds a relayed couple-preset play until this side accepts or denies it. Under mutual override
/// it's applied immediately.
///
/// Every relay gets exactly one answer, since the sender waits for it before playing its own half.
/// </summary>
public sealed class CoupleRelayInbox : IDisposable
{
    private const long PromptTimeoutMs = 60_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;

    private long receivedAt;

    public PartnerIdentity? Sender { get; private set; }
    public string? PresetName { get; private set; }
    public CapturedPoseState? Captured { get; private set; }

    public event Action? Changed;

    /// Raised instead of prompting when mutual override is active.
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
        // Decline a replaced relay so its sender isn't left waiting.
        if (PresetName is { } replaced)
            pairingListener.AnswerCoupleRelay(replaced, accepted: false);

        if (pairingState.MutualOverrideActive)
        {
            Clear();
            pairingListener.AnswerCoupleRelay(presetName, accepted: true);
            AutoApply?.Invoke(captured);
            return;
        }

        Sender = sender;
        PresetName = presetName;
        Captured = captured;
        receivedAt = Environment.TickCount64;
        Changed?.Invoke();
    }

    /// Returns the captured state to apply, or null if nothing was pending.
    public CapturedPoseState? Accept()
    {
        var captured = Captured;
        if (PresetName is { } name)
            pairingListener.AnswerCoupleRelay(name, accepted: true);
        Clear();
        return captured;
    }

    public void Deny()
    {
        if (PresetName is { } name)
            pairingListener.AnswerCoupleRelay(name, accepted: false);
        Clear();
    }

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

    /// A timed-out prompt counts as a deny.
    public void Tick()
    {
        if (Captured == null) return;
        if (Environment.TickCount64 - receivedAt > PromptTimeoutMs) Deny();
    }
}
