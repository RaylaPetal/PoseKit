using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PoseKit;

/// <summary>
/// Enters doze through the emote agent, the emote menu's own path, instead of typing "/doze": the
/// slash command refuses away from a bed or chair, the agent call dozes in place and the server
/// shows it to everyone. Adapted from Synastry's AnywherePoseService (AGPL-3.0,
/// https://github.com/ShaalaXIV/Synastry).
/// </summary>
public sealed unsafe class AnywherePose
{
    private const ushort DozeEmoteId = 88;

    private readonly delegate* unmanaged<nint, ushort, nint, byte, byte, void> useEmote;

    /// False when the signature didn't resolve (e.g. after a patch); callers fall back to "/doze".
    public bool Available => useEmote != null;

    public AnywherePose()
    {
        try
        {
            useEmote = (delegate* unmanaged<nint, ushort, nint, byte, byte, void>)
                Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 40 84 ED 74 ?? 48 8B 4B ?? 48 8B 01 FF 90");
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "[PoseKit] Emote agent call did not resolve; doze will use /doze and needs a bed or chair.");
        }
    }

    /// Returns false when the call isn't possible, so the caller can send "/doze" instead.
    public bool EnterDoze()
    {
        if (!Available) return false;
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Emote);
        if (agent == null) return false;
        useEmote((nint)agent, DozeEmoteId, nint.Zero, 0, 0);
        return true;
    }
}
