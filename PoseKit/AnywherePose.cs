using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PoseKit;

/// <summary>
/// Enters doze and chair sit through the emote agent, the emote menu's own path, instead of typing
/// "/doze" or "/sit": away from furniture "/doze" is refused and "/sit" ground-sits instead, while the
/// agent call enters the pose in place and the server shows it to everyone. Adapted from Synastry's AnywherePoseService (AGPL-3.0,
/// https://github.com/ShaalaXIV/Synastry).
/// </summary>
public sealed unsafe class AnywherePose
{
    private const ushort DozeEmoteId = 88;
    // The chair-sit emote the game itself uses on a seat; "/sit" (50) picks ground sit off furniture.
    private const ushort ChairSitEmoteId = 96;

    private readonly delegate* unmanaged<nint, ushort, nint, byte, byte, void> useEmote;

    /// False when the signature didn't resolve (e.g. after a patch); callers fall back to "/doze" and "/sit".
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
            Plugin.Log.Warning(e, "[PoseKit] Emote agent call did not resolve; doze and chair sit will use /doze and /sit and need furniture.");
        }
    }

    /// Returns false when the call isn't possible, so the caller can send "/doze" instead.
    public bool EnterDoze() => UseEmote(DozeEmoteId);

    /// Returns false when the call isn't possible, so the caller can send "/sit" instead.
    public bool EnterSit() => UseEmote(ChairSitEmoteId);

    private bool UseEmote(ushort emoteId)
    {
        if (!Available) return false;
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Emote);
        if (agent == null) return false;
        useEmote((nint)agent, emoteId, nint.Zero, 0, 0);
        return true;
    }
}
