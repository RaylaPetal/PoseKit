namespace PoseKit.Pairing;

using System;
using PoseKit.Sync;

/// <summary>The only place that sends pairing tells. Every send comes from a single user action:
/// one click, one tell, no automatic replies or retries.</summary>
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
