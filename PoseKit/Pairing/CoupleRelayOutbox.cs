namespace PoseKit.Pairing;

using System;
using PoseKit.Movement;
using PoseKit.Presets;

/// <summary>
/// The clicking side of a couple-preset play: optionally walks to the partner first (auto-align),
/// then relays the partner's half for their accept/deny, and only plays this side's own half once
/// they accept — the same moment they start theirs, so both halves begin together instead of this
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
    private readonly AlignService alignService;
    private readonly Action<NamedPose> playOwnHalf;

    private NamedPose? pending;
    private bool relaySent;
    private long relaySentAt;

    /// The couple preset waiting to play, or null — UI-facing.
    public string? PendingPresetName => pending?.Name;

    /// True while still walking to the partner, before they've been prompted.
    public bool IsAligning => pending != null && !relaySent;

    public event Action? Changed;

    public CoupleRelayOutbox(PairingState pairingState, PairingListener pairingListener, AlignService alignService, Action<NamedPose> playOwnHalf)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;
        this.alignService = alignService;
        this.playOwnHalf = playOwnHalf;
        pairingListener.CoupleAnswerReceived += OnAnswerReceived;
        pairingState.Changed += OnPairingStateChanged;
    }

    public void Dispose()
    {
        pairingListener.CoupleAnswerReceived -= OnAnswerReceived;
        pairingState.Changed -= OnPairingStateChanged;
    }

    /// Starts (or replaces) a couple-preset play. With auto-align on, walks to the partner first and
    /// holds the relay until the walk has fully settled (see AlignService.IsBusy); otherwise — or if
    /// the walk couldn't start (partner not loaded, too far, busy) — relays immediately.
    public void Start(NamedPose preset, bool autoAlign)
    {
        pending = preset;
        relaySent = false;

        if (!(autoAlign && alignService.TryAutoAlignToPartner()))
            SendRelay();

        Changed?.Invoke();
    }

    public void Cancel() => Clear();

    private void SendRelay()
    {
        if (pending?.PartnerHalf is not { } half)
        {
            Clear();
            return;
        }

        pairingListener.RelayCouplePreset(pending.Name, new CapturedPoseState(half.Pose, half.Offset, half.Anchor, half.Penumbra));
        relaySent = true;
        relaySentAt = Environment.TickCount64;
        Changed?.Invoke();
    }

    private void OnAnswerReceived(PartnerIdentity sender, string presetName, bool accepted)
    {
        if (pending is not { } preset || !relaySent) return;
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
        relaySent = false;
        Changed?.Invoke();
    }

    /// Called every framework tick: sends the held relay once the walk has settled, and gives up on an
    /// answer that never arrives.
    public void Tick()
    {
        if (pending == null) return;

        if (!relaySent)
        {
            if (!alignService.IsBusy) SendRelay();
            return;
        }

        if (Environment.TickCount64 - relaySentAt > AnswerTimeoutMs)
        {
            Plugin.ChatGui.Print($"[PoseKit] No answer from your partner for \"{pending.Name}\".");
            Clear();
        }
    }
}
