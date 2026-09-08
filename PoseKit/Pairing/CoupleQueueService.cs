namespace PoseKit.Pairing;

using System;

/// <summary>
/// Queues a selection toward the current pairing peer and auto-plays it once the partner has also
/// queued something of their own — no cross-side matching, each side always plays whatever it
/// queued. Pairing itself is a standing, dedicated session state (see PairingState) that exists
/// independently of any preset — this only reacts to it, never initiates it.
///
/// Generalized over what "a selection" is (a display name + a callback to actually play it) rather
/// than tied to NamedPose specifically, so both the Presets tab (saved presets) and the Animations
/// tab (Penumbra-discovered poses, which aren't NamedPose at all) can queue through the same service.
/// </summary>
public sealed class CoupleQueueService : IDisposable
{
    // Long enough that two people clicking a few seconds apart still connects, short enough that a
    // click from minutes ago can't surprise-fire a pose later. Mirrors the bounded-retry shape
    // PoseTrigger.Tick already uses for cpose cycling, just at a coarser, UI-facing timescale.
    private const long QueueTimeoutMs = 60_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;

    private Action? queuedPlay;
    private long queuedAt;

    /// Display name of this side's own queued selection, or null if nothing's queued — UI-facing, so
    /// both the preset library and the Animations tab can highlight whichever button was clicked.
    public string? QueuedSelectionName { get; private set; }

    /// Display name of whatever the partner queued, once their readiness tell has arrived — null
    /// until then. Shown in the couple-pairing section so each side can see the other's pick.
    public string? PartnerSelectionName { get; private set; }
    private PartnerIdentity? partnerSelectionFrom;

    public event Action? Changed;

    public CoupleQueueService(PairingState pairingState, PairingListener pairingListener)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;

        pairingState.Changed += OnPairingStateChanged;
        pairingListener.QueueSignalReceived += OnQueueSignalReceived;
    }

    public void Dispose()
    {
        pairingState.Changed -= OnPairingStateChanged;
        pairingListener.QueueSignalReceived -= OnQueueSignalReceived;
    }

    /// Clicking anything while paired (a preset, or a Penumbra-discovered pose's trigger button):
    /// queues it toward whoever the current pairing peer is, deferring `play` until both sides have
    /// queued something — nothing happens locally yet, same as the partner's side. Callers should
    /// only reach this while PairingState.Active is true; play immediately instead when unpaired.
    public void QueueSelection(string displayName, Action play)
    {
        if (!pairingState.Active || pairingState.Peer is not { } partner) return;

        // Replaces rather than stacks — a fresh click always overwrites whatever was queued before.
        QueuedSelectionName = displayName;
        queuedPlay = play;
        queuedAt = Environment.TickCount64;
        PairingSender.Send(PairingComposer.ComposeQueueSignal(partner, displayName));
        Changed?.Invoke();
        TryPlayIfBothReady();
    }

    /// Called instead of QueueSelection when mutual override is active and this side already has a
    /// queued pick of its own — see PairingState.MutualOverrideActive. Forces `forcedDisplayName` on
    /// the partner and plays this side's own already-queued pick immediately, without waiting on a
    /// reply (this side already knows both halves by construction: its own first pick, and the
    /// forced pick it just chose for the partner). Falls back to a normal QueueSelection if nothing
    /// of this side's own was queued yet — there would be nothing to play immediately.
    public void TryForceSelect(string forcedDisplayName, Action fallbackPlay)
    {
        if (queuedPlay is not { } ownPlay || pairingState.Peer is not { } partner)
        {
            QueueSelection(forcedDisplayName, fallbackPlay);
            return;
        }

        PairingSender.Send(PairingComposer.ComposeForceSelection(partner, forcedDisplayName));
        ClearQueue();
        ownPlay();
    }

    private void OnQueueSignalReceived(PartnerIdentity sender, string name)
    {
        PartnerSelectionName = name;
        partnerSelectionFrom = sender;
        Changed?.Invoke();
        TryPlayIfBothReady();
    }

    private void OnPairingStateChanged()
    {
        // A queued selection whose pairing changed peer (or dropped) underneath it is no longer
        // meaningful — clear it rather than let a stale readiness fire against the wrong partner.
        if (!pairingState.Active)
            ClearQueue();
    }

    private void TryPlayIfBothReady()
    {
        if (queuedPlay is not { } play) return;
        if (partnerSelectionFrom is not { } readyFrom || pairingState.Peer is not { } partner || !readyFrom.Equals(partner)) return;

        ClearQueue();
        play();
    }

    private void ClearQueue()
    {
        QueuedSelectionName = null;
        queuedPlay = null;
        PartnerSelectionName = null;
        partnerSelectionFrom = null;
        Changed?.Invoke();
    }

    /// Called every framework tick: clears a queued selection that never heard back from the partner.
    public void Tick()
    {
        if (queuedPlay == null) return;
        if (Environment.TickCount64 - queuedAt > QueueTimeoutMs)
            ClearQueue();
    }
}
