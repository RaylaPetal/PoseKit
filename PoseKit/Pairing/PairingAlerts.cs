using System;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PoseKit.Pairing;

/// <summary>
/// Plays a chat sound when something from the partner needs this player's attention: an invite, the
/// partner accepting this side's invite, their pick, an offer waiting for an answer, or their unpair.
/// Chat lines alone scroll away, and a partner who already picked ends up waiting. Nothing this side
/// did makes a sound.
/// </summary>
public sealed class PairingAlerts : IDisposable
{
    // One ding for a burst, such as an accept followed straight away by a pick.
    private const long CooldownMs = 2_000;

    private readonly Configuration configuration;
    private readonly PairingState state;
    private readonly PairingListener listener;
    private readonly CoupleRelayInbox inbox;

    private long lastPlayedAt = long.MinValue / 2;
    private bool failureLogged;

    // The state as of the last Changed, to tell what just happened.
    private string? lastInviteId;
    private bool wasActive;
    private PartnerIdentity? lastOutgoingTarget;
    private CoupleRelayRequest? lastRelay;

    public PairingAlerts(Configuration configuration, PairingState state, PairingListener listener, CoupleRelayInbox inbox)
    {
        this.configuration = configuration;
        this.state = state;
        this.listener = listener;
        this.inbox = inbox;

        state.Changed += OnPairingChanged;
        listener.QueueSignalReceived += OnPartnerPicked;
        listener.PartnerUnpaired += OnPartnerUnpaired;
        inbox.Changed += OnRelayChanged;
    }

    public void Dispose()
    {
        state.Changed -= OnPairingChanged;
        listener.QueueSignalReceived -= OnPartnerPicked;
        listener.PartnerUnpaired -= OnPartnerUnpaired;
        inbox.Changed -= OnRelayChanged;
    }

    private void OnPairingChanged()
    {
        var inviteId = state.PendingInvite?.InviteId;
        if (inviteId != null && inviteId != lastInviteId)
            Alert();
        // This side accepting clears its pending invite first, so only an accepted outgoing invite activates
        // with one on record.
        else if (state.Active && !wasActive && lastOutgoingTarget is { } invited && state.Peer is { } peer && invited.Equals(peer))
            Alert();

        lastInviteId = inviteId;
        wasActive = state.Active;
        lastOutgoingTarget = state.OutgoingInvite?.Target;
    }

    private void OnPartnerPicked(PartnerIdentity sender, string name) => Alert();

    private void OnPartnerUnpaired(PartnerIdentity sender) => Alert();

    /// Under mutual override a relay is applied without ever pending, so it stays quiet.
    private void OnRelayChanged()
    {
        var pending = inbox.Pending;
        if (pending != null && !ReferenceEquals(pending, lastRelay))
            Alert();
        lastRelay = pending;
    }

    private void Alert()
    {
        if (!configuration.PairingAlertSound) return;
        var now = Environment.TickCount64;
        if (now - lastPlayedAt < CooldownMs) return;
        lastPlayedAt = now;
        Play(configuration.PairingAlertSoundId);
    }

    /// The game's &lt;se.N&gt; sounds, at the player's own volume. Also the settings' Test button.
    public void Play(int soundId)
    {
        try
        {
            UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(soundId, 1, 16));
        }
        catch (Exception ex)
        {
            if (failureLogged) return;
            failureLogged = true;
            Plugin.Log.Warning(ex, "[PoseKit] Couldn't play the pairing alert sound.");
        }
    }
}
