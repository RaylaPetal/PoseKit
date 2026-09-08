namespace PoseKit.Pairing;

using System;
using PoseKit.Sync;

/// <summary>The one place in this plugin that can actually transmit a pairing-related chat message —
/// deliberately separate from PairingComposer (which only ever builds text) so every call site
/// capable of sending is grep-able in one file. Every call here originates from a direct, single UI
/// action: one click, one tell — never wired to fire without that per-action human trigger, no
/// auto-reply, no reacting to received chat with another send, no retry/resend loops.</summary>
public static class PairingSender
{
    public static bool Send(string text)
    {
        if (!text.TrimStart().StartsWith("/tell ", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Warning("[PoseKit] Refused to send a pairing command that wasn't a /tell.");
            return false;
        }

        ChatCommand.Execute(text);
        return true;
    }
}
