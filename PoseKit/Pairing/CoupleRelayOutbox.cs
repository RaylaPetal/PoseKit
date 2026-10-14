namespace PoseKit.Pairing;

using System;
using PoseKit.Presets;

/// <summary>
/// Sending side of a couple play: relays the partner's half (a couple preset's captured half, or the
/// other role of a library animation) and plays this side's half only once they accept, so both
/// start together. See CoupleRelayInbox.
/// </summary>
public sealed class CoupleRelayOutbox : IDisposable
{
    // Longer than the partner's prompt timeout, so their decline normally arrives first.
    private const long AnswerTimeoutMs = 65_000;

    // A role relay unacknowledged this long went to a partner whose PoseKit predates it.
    private const long RoleAckTimeoutMs = 6_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;
    private readonly Action<NamedPose> playOwnHalf;

    private (string Name, Action PlayOwn, bool IsRole)? pending;

    // A role played without waiting, kept so a late "missing" answer can still be explained.
    private (string Name, long SentAt)? instantRole;
    private long relaySentAt;
    private bool acknowledged;

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

        Wait(preset.Name, () => playOwnHalf(preset), isRole: false);
        pairingListener.RelayCouplePreset(preset.Name, new CapturedPoseState(half.Pose, half.Offset, half.Anchor, half.Penumbra));
        Plugin.ChatGui.Print($"[PoseKit] Asked {PartnerName} to play \"{preset.Name}\" with you.");
    }

    /// Sends the partner the other role of a library animation; <paramref name="playOwn"/> runs once
    /// they accept, or right away (with a notice) if they don't have it.
    public void StartRole(string name, RoleHashes role, Action playOwn)
    {
        // Both sides on override and up to date: the partner plays the role as soon as it arrives, so
        // there's nothing to wait for. Only a missing role comes back.
        if (pairingState.MutualOverrideActive && pairingState.PeerHello)
        {
            pairingListener.RelayRole(name, role);
            instantRole = (name, Environment.TickCount64);
            Plugin.ChatGui.Print($"[PoseKit] Playing \"{name}\" with {PartnerName}.");
            playOwn();
            return;
        }

        Wait(name, playOwn, isRole: true);
        pairingListener.RelayRole(name, role);
        Plugin.ChatGui.Print($"[PoseKit] Asked {PartnerName} to play the other role of \"{name}\".");
    }

    /// Withdraws the relay on both sides.
    public void Cancel()
    {
        if (pending is { } waiting)
            pairingListener.CancelRelay(waiting.Name);
        Clear();
    }

    private void Wait(string name, Action playOwn, bool isRole)
    {
        pending = (name, playOwn, isRole);
        relaySentAt = Environment.TickCount64;
        acknowledged = false;
        Changed?.Invoke();
    }

    private void OnAnswerReceived(PartnerIdentity sender, string name, CoupleAnswer answer)
    {
        if (answer == CoupleAnswer.Missing && instantRole is { } instant &&
            string.Equals(instant.Name.Trim(), name.Trim(), StringComparison.Ordinal) &&
            Environment.TickCount64 - instant.SentAt <= AnswerTimeoutMs)
        {
            instantRole = null;
            Plugin.ChatGui.Print($"[PoseKit] {sender.Name} doesn't have \"{instant.Name}\", so only your side plays.");
            return;
        }

        if (pending is not { } waiting) return;
        if (pairingState.Peer is not { } peer || !peer.Equals(sender)) return;
        if (!string.Equals(waiting.Name.Trim(), name.Trim(), StringComparison.Ordinal)) return;

        if (answer == CoupleAnswer.Received)
        {
            acknowledged = true;
            return;
        }

        Clear();
        switch (answer)
        {
            case CoupleAnswer.Accepted:
                Plugin.ChatGui.Print($"[PoseKit] {sender.Name} accepted \"{waiting.Name}\".");
                waiting.PlayOwn();
                break;
            case CoupleAnswer.Missing when waiting.IsRole:
                Plugin.ChatGui.Print($"[PoseKit] {sender.Name} doesn't have \"{waiting.Name}\", so only your side plays.");
                waiting.PlayOwn();
                break;
            default:
                Plugin.ChatGui.Print($"[PoseKit] {sender.Name} declined \"{waiting.Name}\".");
                break;
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

        // Only a partner without hello sends a receipt; an up-to-date one just answers.
        if (waiting.IsRole && !acknowledged && !pairingState.PeerHello && Environment.TickCount64 - relaySentAt > RoleAckTimeoutMs)
        {
            Plugin.ChatGui.Print("[PoseKit] Your partner's PoseKit can't take a role yet (they need to update), " +
                                 "so only your side plays.");
            Clear();
            waiting.PlayOwn();
            return;
        }

        if (Environment.TickCount64 - relaySentAt > AnswerTimeoutMs)
        {
            Plugin.ChatGui.Print($"[PoseKit] No answer from your partner for \"{waiting.Name}\".");
            Clear();
        }
    }
}
