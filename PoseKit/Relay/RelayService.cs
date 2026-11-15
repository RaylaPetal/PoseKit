using System;
using PoseKit.Pairing;

namespace PoseKit.Relay;

/// <summary>
/// Owns this install's relay connection: connected while logged in to a character, with that character's inbox
/// registered so invites can reach it. Pairing talks through <see cref="Messenger"/>.
/// </summary>
public sealed class RelayService : IDisposable
{
    private readonly RelaySocket socket;
    private string? registeredInbox;
    private bool overBudget;

    public RelayService(Configuration configuration)
    {
        Identity = new DeviceIdentity(configuration);
        socket = new RelaySocket(Identity, action => Plugin.Framework.RunOnFrameworkThread(action));
        Messenger = new PeerMessenger(socket, Identity);
        socket.StateChanged += state =>
        {
            // A fresh connection has no registration on the relay side.
            if (state != RelaySocketState.Connected) registeredInbox = null;
            else overBudget = false;
        };
        Messenger.RelayError += code =>
        {
            if (code == "over_budget") overBudget = true;
        };
    }

    public DeviceIdentity Identity { get; }
    public PeerMessenger Messenger { get; }
    public RelaySocketState State => socket.State;
    public bool Connected => socket.State == RelaySocketState.Connected;

    /// The relay refused work for today; shown as "pairing service busy".
    public bool OverBudget => overBudget;

    /// The logged-in character, in the form invites are addressed to.
    public PartnerIdentity? Self { get; private set; }

    public void Tick()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        Self = player != null && Plugin.ClientState.IsLoggedIn ? PartnerIdentity.FromCharacter(player) : null;
        socket.Wanted = Self != null;
        if (!Connected) return;

        var inbox = Self is { } self ? RelayCrypto.InboxId(self.TellAddress) : null;
        if (inbox == registeredInbox) return;
        registeredInbox = inbox;
        Messenger.Register(inbox);
    }

    public void Dispose() => socket.Dispose();
}
