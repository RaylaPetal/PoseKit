namespace PoseKit.Pairing;

using System;
using PoseKit.Presets;

/// <summary>
/// Sending side of a couple preset: relays the partner's captured half and plays this side's half
/// only once they accept, so both start together. See CoupleRelayInbox.
/// </summary>
public sealed class CoupleRelayOutbox : IDisposable
{
    // Longer than the partner's prompt timeout, so their decline normally arrives first.
    private const long AnswerTimeoutMs = 65_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;
    private readonly Action<NamedPose> playOwnHalf;

    private (string Name, Action PlayOwn)? pending;
    private long relaySentAt;

    public string? PendingPresetName => pending?.Name;

    private string PartnerName => pairingState.Peer?.Name ?? "your partner";

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

        pending = (preset.Name, () => playOwnHalf(preset));
        relaySentAt = Environment.TickCount64;
        Changed?.Invoke();
        pairingListener.RelayCouplePreset(preset.Name, new CapturedPoseState(half.Pose, half.Offset, half.Anchor, half.Penumbra));
        Plugin.ChatGui.Print($"[PoseKit] Asked {PartnerName} to play \"{preset.Name}\" with you.");
    }

    /// Withdraws the relay on both sides.
    public void Cancel()
    {
        if (pending is { } waiting)
            pairingListener.CancelRelay(waiting.Name);
        Clear();
    }

    private void OnAnswerReceived(PartnerIdentity sender, string name, CoupleAnswer answer)
    {
        if (pending is not { } waiting) return;
        if (pairingState.Peer is not { } peer || !peer.Equals(sender)) return;
        if (!string.Equals(waiting.Name.Trim(), name.Trim(), StringComparison.Ordinal)) return;
        // Only role offers get a receipt, and this side no longer sends those.
        if (answer == CoupleAnswer.Received) return;

        Clear();
        if (answer == CoupleAnswer.Accepted)
        {
            Plugin.ChatGui.Print($"[PoseKit] {sender.Name} accepted \"{waiting.Name}\".");
            waiting.PlayOwn();
        }
        else
        {
            Plugin.ChatGui.Print($"[PoseKit] {sender.Name} declined \"{waiting.Name}\".");
        }
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
        if (pending is not { } waiting) return;
        if (Environment.TickCount64 - relaySentAt <= AnswerTimeoutMs) return;
        Plugin.ChatGui.Print($"[PoseKit] No answer from your partner for \"{waiting.Name}\".");
        Clear();
    }
}
