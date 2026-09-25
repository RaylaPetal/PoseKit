namespace PoseKit.Pairing;

using System;
using PoseKit.Presets;

/// <summary>
/// The clicking side of a couple-preset play: relays the partner's half for their accept/deny, and
/// only plays this side's own half once they accept — the same moment they start theirs, so both halves begin together instead of this
/// side jumping ahead the instant it clicked. The counterpart of CoupleRelayInbox, which answers
/// every relay exactly once. Mirrors CoupleQueueService's Tick-driven timeout shape.
/// </summary>
public sealed class CoupleRelayOutbox : IDisposable
{
    // Slightly longer than CoupleRelayInbox.PromptTimeoutMs, so the partner's own timeout (which
    // answers as declined) normally arrives first and this side can say so — this is only the backstop
    // for an answer that never arrives at all (e.g. the partner is on an older PoseKit that doesn't
    // answer).
    private const long AnswerTimeoutMs = 65_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;
    private readonly Action<NamedPose> playOwnHalf;

    private NamedPose? pending;
    private long relaySentAt;

    /// The couple preset waiting on the partner's answer, or null — UI-facing.
    public string? PendingPresetName => pending?.Name;

    public event Action? Changed;

    public CoupleRelayOutbox(PairingState pairingState, PairingListener pairingListener, Action<NamedPose> playOwnHalf)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;
        this.playOwnHalf = playOwnHalf;
        pairingListener.CoupleAnswerReceived += OnAnswerReceived;
        pairingState.Changed += OnPairingStateChanged;
    }

    public void Dispose()
    {
        pairingListener.CoupleAnswerReceived -= OnAnswerReceived;
        pairingState.Changed -= OnPairingStateChanged;
    }

    /// Starts (or replaces) a couple-preset play: relays the partner's half right away and waits for
    /// their answer. Callers only reach this for a preset that carries a partner half.
    public void Start(NamedPose preset)
    {
        if (preset.PartnerHalf is not { } half) return;

        pending = preset;
        relaySentAt = Environment.TickCount64;
        pairingListener.RelayCouplePreset(preset.Name, new CapturedPoseState(half.Pose, half.Offset, half.Anchor, half.Penumbra));
        Changed?.Invoke();
    }

    public void Cancel() => Clear();

    private void OnAnswerReceived(PartnerIdentity sender, string presetName, bool accepted)
    {
        if (pending is not { } preset) return;
        if (pairingState.Peer is not { } peer || !peer.Equals(sender)) return;
        if (!string.Equals(preset.Name.Trim(), presetName.Trim(), StringComparison.Ordinal)) return;

        Clear();
        if (accepted)
            playOwnHalf(preset);
        else
            Plugin.ChatGui.Print($"[PoseKit] {sender.Name} declined \"{preset.Name}\".");
    }

    private void OnPairingStateChanged()
    {
        if (!pairingState.Active) Clear();
    }

    private void Clear()
    {
        if (pending == null) return;
        pending = null;
        Changed?.Invoke();
    }

    /// Called every framework tick: gives up on an answer that never arrives.
    public void Tick()
    {
        if (pending == null) return;

        if (Environment.TickCount64 - relaySentAt > AnswerTimeoutMs)
        {
            Plugin.ChatGui.Print($"[PoseKit] No answer from your partner for \"{pending.Name}\".");
            Clear();
        }
    }
}
