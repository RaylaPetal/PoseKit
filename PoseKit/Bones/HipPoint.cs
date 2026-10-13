using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace PoseKit.Bones;

/// <summary>
/// Where the character's backside meets a surface: the midpoint of the buttocks bones, falling back
/// to the pelvis. Used to snap an animation onto a seat or bed.
/// </summary>
public static class HipPoint
{
    /// Tried in order; the first set with any bone present is averaged. iv_* bones come from the
    /// IVCS skeleton, j_* from vanilla.
    private static readonly string[][] BoneGroups =
    [
        ["iv_shiri_l", "iv_shiri_r"],
        ["j_shiri_l", "j_shiri_r"],
        ["j_kosi"],
    ];

    /// World position as drawn, including any render offset. False when no hip bone can be read.
    public static bool TryGet(IPlayerCharacter character, out Vector3 world)
    {
        foreach (var group in BoneGroups)
        {
            if (BoneReader.TryGetAverageBonePosition(character, group, out world))
                return true;
        }
        world = default;
        return false;
    }
}
