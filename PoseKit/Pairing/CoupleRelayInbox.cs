namespace PoseKit.Pairing;

using System;

/// <summary>
/// A relayed play waiting on this side: a couple preset's partner half, or the other role of a
/// library animation the partner clicked. Exactly one of Captured and Role is set.
/// </summary>
public sealed record CoupleRelayRequest(PartnerIdentity Sender, string Name, CapturedPoseState? Captured, RoleHashes? Role);

/// <summary>
/// Holds a relayed couple-preset play or role until this side accepts or denies it. Under mutual
/// override it's applied immediately. A role this side doesn't have is answered as missing without
/// prompting.
///
/// Every relay gets exactly one answer, since the sender waits for it before playing its own half.
/// Role offers only come from partners on 3.0.0.0-3.0.0.4; this version queues library picks instead.
/// </summary>
public sealed class CoupleRelayInbox : IDisposable
{
    private const long PromptTimeoutMs = 60_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;
    private readonly Func<RoleHashes, bool> hasRole;

    private long receivedAt;

    public CoupleRelayRequest? Pending { get; private set; }

    public PartnerIdentity? Sender => Pending?.Sender;
    public string? PresetName => Pending?.Name;

    public event Action? Changed;

    /// Raised instead of prompting when mutual override is active.
    public event Action<CoupleRelayRequest>? AutoApply;

    public CoupleRelayInbox(PairingState pairingState, PairingListener pairingListener, Func<RoleHashes, bool> hasRole)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;
        this.hasRole = hasRole;
        pairingListener.CoupleRelayReceived += OnPresetReceived;
        pairingListener.RoleRelayReceived += OnRoleReceived;
        pairingListener.RelayCancelReceived += OnCancelReceived;
        pairingState.Changed += OnPairingStateChanged;
    }

    public void Dispose()
    {
        pairingListener.CoupleRelayReceived -= OnPresetReceived;
        pairingListener.RoleRelayReceived -= OnRoleReceived;
        pairingListener.RelayCancelReceived -= OnCancelReceived;
        pairingState.Changed -= OnPairingStateChanged;
    }

    private void OnPresetReceived(PartnerIdentity sender, string presetName, CapturedPoseState captured)
    {
        Receive(new CoupleRelayRequest(sender, presetName, captured, null));
        if (Pending == null) return;
        Plugin.ChatGui.Print($"[PoseKit] {sender.Name} wants to play \"{presetName}\" with you. Accept or decline in the library " +
                             "(/posekit lib) or PoseKit's pairing panel.");
    }

    private void OnRoleReceived(PartnerIdentity sender, string name, RoleHashes role)
    {
        if (!hasRole(role))
        {
            pairingListener.AnswerCoupleRelay(name, CoupleAnswer.Missing);
            Plugin.ChatGui.Print($"[PoseKit] {sender.Name} wants to play \"{name}\" with you, but it isn't in your mods.");
            return;
        }
        Receive(new CoupleRelayRequest(sender, name, null, role));
        if (Pending == null) return;

        // Role offers only come from 3.0.0.0-3.0.0.4, which wait for this receipt.
        pairingListener.AnswerCoupleRelay(name, CoupleAnswer.Received);
        // Library clicks can arrive while neither PoseKit window is open.
        Plugin.ChatGui.Print($"[PoseKit] {sender.Name} wants to play \"{name}\" with you. Accept or decline in the library " +
                                 "(/posekit lib) or PoseKit's pairing panel.");
    }

    private void Receive(CoupleRelayRequest request)
    {
        // Decline a replaced relay so its sender isn't left waiting. Not one with the same name: the
        // sender has replaced its own wait with the new relay, and a decline would end that too.
        if (PresetName is { } replaced && !string.Equals(replaced.Trim(), request.Name.Trim(), StringComparison.Ordinal))
            pairingListener.AnswerCoupleRelay(replaced, CoupleAnswer.Declined);

        if (pairingState.MutualOverrideActive)
        {
            Clear();
            pairingListener.AnswerCoupleRelay(request.Name, CoupleAnswer.Accepted);
            Plugin.ChatGui.Print($"[PoseKit] Playing \"{request.Name}\" with {request.Sender.Name}.");
            AutoApply?.Invoke(request);
            return;
        }

        Pending = request;
        receivedAt = Environment.TickCount64;
        Changed?.Invoke();
    }

    /// Returns the request to apply, or null if nothing was pending.
    public CoupleRelayRequest? Accept()
    {
        var request = Pending;
        if (request != null)
            pairingListener.AnswerCoupleRelay(request.Name, CoupleAnswer.Accepted);
        Clear();
        return request;
    }

    public void Deny()
    {
        if (PresetName is { } name)
            pairingListener.AnswerCoupleRelay(name, CoupleAnswer.Declined);
        Clear();
    }

    /// The sender withdrew it, so it's dropped without an answer.
    private void OnCancelReceived(PartnerIdentity sender, string name)
    {
        if (Pending is not { } pending || !pending.Sender.Equals(sender)) return;
        if (!string.Equals(pending.Name.Trim(), name.Trim(), StringComparison.Ordinal)) return;
        Clear();
        Plugin.ChatGui.Print($"[PoseKit] {sender.Name} cancelled \"{name}\".");
    }

    private void OnPairingStateChanged()
    {
        if (!pairingState.Active) Clear();
    }

    private void Clear()
    {
        Pending = null;
        Changed?.Invoke();
    }

    /// A timed-out prompt counts as a deny.
    public void Tick()
    {
        if (Pending == null) return;
        if (Environment.TickCount64 - receivedAt > PromptTimeoutMs) Deny();
    }
}
