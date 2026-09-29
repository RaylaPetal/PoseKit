namespace PoseKit.Pairing;

using System;
using PoseKit;
using PoseKit.Presets;

/// <summary>
/// Queues a selection while paired and plays it once the partner has queued something too. Each side
/// plays what it queued.
/// </summary>
public sealed class CoupleQueueService : IDisposable
{
    // Long enough for two people clicking a few seconds apart, short enough that an old click can't
    // fire unexpectedly.
    private const long QueueTimeoutMs = 60_000;

    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;

    private Action? queuedPlay;
    private long queuedAt;

    public string? QueuedSelectionName { get; private set; }

    public string? PartnerSelectionName { get; private set; }
    private PartnerIdentity? partnerSelectionFrom;

    public event Action? Changed;

    public CoupleQueueService(PairingState pairingState, PairingListener pairingListener)
    {
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;

        pairingState.Changed += OnPairingStateChanged;
        pairingListener.QueueSignalReceived += OnQueueSignalReceived;
        pairingListener.ForceSelectionReceived += OnForceSelectionReceived;
    }

    public void Dispose()
    {
        pairingState.Changed -= OnPairingStateChanged;
        pairingListener.QueueSignalReceived -= OnQueueSignalReceived;
        pairingListener.ForceSelectionReceived -= OnForceSelectionReceived;
    }

    /// Defers <paramref name="play"/> until both sides have queued. A new click replaces the old one.
    public void QueueSelection(string displayName, Action play)
    {
        if (!pairingState.Active || pairingState.Peer is not { } partner) return;

        QueuedSelectionName = displayName;
        queuedPlay = play;
        queuedAt = Environment.TickCount64;
        PairingSender.Send(PairingComposer.ComposeQueueSignal(partner, displayName));
        pairingState.Touch();
        Changed?.Invoke();
        TryPlayIfBothReady();
    }

    /// Under mutual override with a pick already queued: forces this selection on the partner and
    /// plays this side's queued pick now. Otherwise queues normally.
    public void TryForceSelect(string forcedDisplayName, Action fallbackPlay, PenumbraLink? penumbra = null, string? triggerText = null)
    {
        if (queuedPlay is not { } ownPlay || pairingState.Peer is not { } partner)
        {
            QueueSelection(forcedDisplayName, fallbackPlay);
            return;
        }

        PairingSender.Send(PairingComposer.ComposeForceSelection(partner, forcedDisplayName, penumbra, triggerText));
        pairingState.Touch();
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

    /// A forced selection always ends the round.
    private void OnForceSelectionReceived(PartnerIdentity sender, string name, ForceSelectionHashes hashes) => ClearQueue();

    private void OnPairingStateChanged()
    {
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

    public void Tick()
    {
        if (queuedPlay == null) return;
        if (Environment.TickCount64 - queuedAt > QueueTimeoutMs)
            ClearQueue();
    }
}
