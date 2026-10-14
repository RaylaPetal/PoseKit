namespace PoseKit.Pairing;

using System;
using System.Collections.Generic;
using PoseKit.Sync;

/// <summary>The only place that sends pairing tells. Every send comes from a single user action:
/// one click, one tell, no automatic replies or retries.
///
/// Tells are queued and sent at least <see cref="Spacing"/> apart: the game drops a tell that follows
/// another too quickly ("not heard"), and some actions send two at once (an accept and its hello).
/// </summary>
public static class PairingSender
{
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1.1);

    // More than this waiting means something is looping; the extras are dropped rather than spammed.
    private const int MaxQueued = 8;

    private static readonly Queue<string> Queue = new();
    private static DateTime nextSendAt = DateTime.MinValue;

    public static bool Send(string text)
    {
        if (!text.TrimStart().StartsWith("/tell ", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Warning("[PoseKit] Refused to send a pairing command that wasn't a /tell.");
            return false;
        }

        if (Queue.Count >= MaxQueued)
        {
            Plugin.Log.Warning("[PoseKit] Dropped a pairing tell: too many already waiting.");
            return false;
        }

        Queue.Enqueue(text);
        Tick();
        return true;
    }

    /// Sends the next queued tell once the spacing has passed. Called every framework update.
    public static void Tick()
    {
        if (Queue.Count == 0 || DateTime.UtcNow < nextSendAt) return;
        if (!Plugin.ClientState.IsLoggedIn)
        {
            Queue.Clear();
            return;
        }

        nextSendAt = DateTime.UtcNow + Spacing;
        ChatCommand.Execute(Queue.Dequeue());
    }
}
