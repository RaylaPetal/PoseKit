namespace PoseKit.Pairing;

using System;
using PoseKit.Presets;

/// <summary>
/// Queues a preset selection toward the current pairing peer and auto-plays it once the partner has
/// also queued something of their own — no cross-side preset-id matching, each side always plays
/// whatever it queued. Pairing itself is a standing, dedicated session state (see PairingState) that
/// exists independently of any preset — this only reacts to it, never initiates it. Any preset can be
/// queued while paired; nothing about a preset itself names a partner.
/// </summary>
public sealed class CoupleQueueService : IDisposable
{
    // Long enough that two people clicking a few seconds apart still connects, short enough that a
    // click from minutes ago can't surprise-fire a pose later. Mirrors the bounded-retry shape
    // PoseTrigger.Tick already uses for cpose cycling, just at a coarser, UI-facing timescale.
    private const long QueueTimeoutMs = 60_000;

    private readonly Plugin plugin;
    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;

    public NamedPose? QueuedSelection { get; private set; }
    private long queuedAt;

    /// Whether the partner's own readiness has been heard yet for the currently queued selection —
    /// UI-facing, so the preset library can show "waiting on partner" vs. "partner ready".
    public bool PartnerReady { get; private set; }
    private PartnerIdentity? partnerReadyFrom;

    public event Action? Changed;

    public CoupleQueueService(Plugin plugin, PairingState pairingState, PairingListener pairingListener)
    {
        this.plugin = plugin;
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

    /// Clicking any preset while paired: queues it toward whoever the current pairing peer is.
    /// Callers should only reach this while PairingState.Active is true — see
    /// PresetButtonsPanel.DrawPresetEntry, which plays solo (PlayPreset) instead when unpaired.
    public void QueueSelection(NamedPose preset)
    {
        if (!pairingState.Active || pairingState.Peer is not { } partner) return;

        // Replaces rather than stacks — a fresh click always overwrites whatever was queued before.
        QueuedSelection = preset;
        queuedAt = Environment.TickCount64;
        PairingSender.Send(PairingComposer.ComposeQueueSignal(partner));
        Changed?.Invoke();
        TryPlayIfBothReady();
    }

    private void OnQueueSignalReceived(PartnerIdentity sender)
    {
        PartnerReady = true;
        partnerReadyFrom = sender;
        Changed?.Invoke();
        TryPlayIfBothReady();
    }

    private void OnPairingStateChanged()
    {
        // A queued selection whose pairing changed peer (or dropped) underneath it is no longer
        // meaningful — clear it rather than let a stale readiness fire against the wrong partner.
        if (QueuedSelection != null && !pairingState.Active)
            ClearQueue();
    }

    private void TryPlayIfBothReady()
    {
        if (QueuedSelection is not { } preset) return;
        if (!PartnerReady) return;
        if (partnerReadyFrom is not { } readyFrom || pairingState.Peer is not { } partner || !readyFrom.Equals(partner)) return;

        ClearQueue();
        plugin.PlayPreset(preset);
    }

    private void ClearQueue()
    {
        QueuedSelection = null;
        PartnerReady = false;
        partnerReadyFrom = null;
        Changed?.Invoke();
    }

    /// Called every framework tick: clears a queued selection that never heard back from the partner.
    public void Tick()
    {
        if (QueuedSelection == null) return;
        if (Environment.TickCount64 - queuedAt > QueueTimeoutMs)
            ClearQueue();
    }
}
