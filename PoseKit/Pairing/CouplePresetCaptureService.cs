namespace PoseKit.Pairing;

using System;
using PoseKit.Presets;

/// <summary>
/// The "include partner" save: asks the partner for their pose state and saves once the reply
/// arrives, or without a partner half on timeout. A new save replaces a pending one.
/// </summary>
public sealed class CouplePresetCaptureService : IDisposable
{
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

    public event Action<NamedPose>? Saved;

    public CouplePresetCaptureService(PairingState pairingState, PairingListener pairingListener, PresetManager presetManager)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;
        this.presetManager = presetManager;
        pairingListener.CoupleCaptureReplyReceived += OnReplyReceived;
    }

    public void Dispose() => pairingListener.CoupleCaptureReplyReceived -= OnReplyReceived;

    /// Saves immediately without a partner half when not paired.
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
