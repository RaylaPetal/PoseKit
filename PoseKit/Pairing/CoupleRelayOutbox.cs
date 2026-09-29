namespace PoseKit.Pairing;

using System;
using PoseKit.Presets;

/// <summary>
/// Sending side of a couple-preset play: relays the partner's half and plays this side's half only
/// once they accept, so both start together. See CoupleRelayInbox.
/// </summary>
public sealed class CoupleRelayOutbox : IDisposable
{
    // Longer than the partner's prompt timeout, so their decline normally arrives first.
    private const long AnswerTimeoutMs = 65_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;
    private readonly Action<NamedPose> playOwnHalf;

    private NamedPose? pending;
    private long relaySentAt;

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
