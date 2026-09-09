namespace PoseKit.Pairing;

using System;
using PoseKit.Presets;

/// <summary>
/// Drives the "include partner" save flow: sends a capture request to the current pairing peer, waits
/// (via Tick, not async — mirrors CoupleQueueService's polling shape) for a matching reply, and
/// completes the save with or without a captured PartnerHalf once the reply arrives or the request
/// times out. Only one request is ever in flight — a second save attempt while one is pending simply
/// replaces it, same as CoupleQueueService's "a fresh click always overwrites" rule.
/// </summary>
public sealed class CouplePresetCaptureService : IDisposable
{
    // Long enough for a same-room /tell round trip, short enough not to leave the Save button
    // hanging if the partner's client doesn't reply at all.
    private const long CaptureTimeoutMs = 5_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;
    private readonly PresetManager presetManager;

    private string? pendingRequestId;
    private string? pendingName;
    private PoseIdentifier pendingPose;
    private PoseOffset pendingOffset;
    private PenumbraLink? pendingPenumbra;
    private PresetAnchor? pendingAnchor;
    private long pendingDeadline;

    /// Raised once the save actually completes (with or without a captured partner half) — lets the
    /// UI pick up LoadedPreset/etc. the same way an immediate local save already does.
    public event Action<NamedPose>? Saved;

    public CouplePresetCaptureService(PairingState pairingState, PairingListener pairingListener, PresetManager presetManager)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;
        this.presetManager = presetManager;
        pairingListener.CoupleCaptureReplyReceived += OnReplyReceived;
    }

    public void Dispose() => pairingListener.CoupleCaptureReplyReceived -= OnReplyReceived;

    /// Starts (or replaces) an in-flight "include partner" save. If not currently paired, completes
    /// the save immediately with no partner half, exactly as if the option had been left unchecked.
    public void RequestAndSave(string name, PoseIdentifier pose, PoseOffset offset, PenumbraLink? penumbra, PresetAnchor? anchor)
    {
        if (pairingState.Peer == null)
        {
            Complete(name, pose, offset, penumbra, anchor, null);
            return;
        }

        var requestId = Guid.NewGuid().ToString("N")[..8];
        pendingRequestId = requestId;
        pendingName = name;
        pendingPose = pose;
        pendingOffset = offset;
        pendingPenumbra = penumbra;
        pendingAnchor = anchor;
        pendingDeadline = Environment.TickCount64 + CaptureTimeoutMs;

        pairingListener.RequestPartnerCapture(requestId, anchor);
    }

    private void OnReplyReceived(PartnerIdentity sender, string requestId, CapturedPoseState captured)
    {
        if (pendingRequestId != requestId) return; // stale/duplicate/unmatched — discard

        var partnerHalf = new PartnerHalf
        {
            Partner = sender, Pose = captured.Pose, Offset = captured.Offset, Anchor = captured.Anchor, Penumbra = captured.Penumbra,
        };
        Complete(pendingName!, pendingPose, pendingOffset, pendingPenumbra, pendingAnchor, partnerHalf);
    }

    /// Called every framework tick: completes a pending save with no partner half once it's waited
    /// longer than the capture timeout with no reply — per couple-preset-relay's "silently omitted"
    /// requirement.
    public void Tick()
    {
        if (pendingRequestId == null) return;
        if (Environment.TickCount64 < pendingDeadline) return;
        Complete(pendingName!, pendingPose, pendingOffset, pendingPenumbra, pendingAnchor, null);
    }

    private void Complete(string name, PoseIdentifier pose, PoseOffset offset, PenumbraLink? penumbra, PresetAnchor? anchor, PartnerHalf? partnerHalf)
    {
        pendingRequestId = null;
        pendingName = null;

        var saved = presetManager.Save(name, pose, offset, penumbra, anchor, partnerHalf);
        if (saved != null) Saved?.Invoke(saved);
    }
}
