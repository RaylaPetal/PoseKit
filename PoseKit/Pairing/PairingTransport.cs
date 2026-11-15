namespace PoseKit.Pairing;

using System;
using System.Collections.Generic;
using PoseKit.Relay;

/// <summary>
/// The only way pairing lines leave this client: sealed to the verified partner and sent through the relay. Nothing is
/// ever sent as game chat. Delivery problems become one short [PoseKit] line each, never a silent failure.
/// </summary>
public static class PairingTransport
{
    // A burst of picks to an offline partner reports once, not once per pick.
    private static readonly TimeSpan NoticeCooldown = TimeSpan.FromSeconds(30);

    private static RelayService? relay;
    private static PairingState? state;
    private static readonly Dictionary<string, DateTime> lastNotice = new();

    public static void Configure(RelayService relayService, PairingState pairingState)
    {
        relay = relayService;
        state = pairingState;
    }

    /// False when there's no verified partner or no relay connection; the player is told which.
    public static bool Send(string line)
    {
        if (relay is null) return false;
        if (!relay.Connected)
        {
            Notice("unavailable");
            return false;
        }
        if (relay.Messenger.Session is not { } session) return false;
        relay.Messenger.SendLine(session, line, Notice);
        return true;
    }

    /// One line for a delivery result that needs the player's attention; delivered says nothing.
    public static void Notice(string result)
    {
        var text = result switch
        {
            "offline" => $"{state?.Peer?.Name ?? "Your partner"} seems to be offline, so they didn't get that.",
            "unavailable" => "The pairing service can't be reached right now. PoseKit keeps retrying; local posing still works.",
            "over_limit" => "That's a lot of pairing messages at once. Slow down a moment and try again.",
            "over_budget" => "The pairing service is busy for today. Local posing still works.",
            "refused" => null, // a clock or format problem; logged, nothing the player can act on
            _ => null,
        };
        if (result == "refused") Plugin.Log.Warning("[PoseKit] The relay refused a pairing message.");
        if (text is null) return;

        var now = DateTime.UtcNow;
        if (lastNotice.TryGetValue(result, out var last) && now - last < NoticeCooldown) return;
        lastNotice[result] = now;
        Plugin.ChatGui.PrintError($"[PoseKit] {text}");
    }
}
