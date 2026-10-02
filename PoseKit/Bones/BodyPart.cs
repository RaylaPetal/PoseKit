using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace PoseKit.Bones;

/// <summary>A body part Bone Align can line up. Saved by numeric value, so only append members.</summary>
public enum BodyPart
{
    Penis,
    Vagina,
    Anus,
    LeftButtock,
    RightButtock,
    Mouth,
    LeftHand,
    RightHand,
    LeftFingers,
    RightFingers,
    LeftFoot,
    RightFoot,
    LeftToes,
    RightToes,
}

public static class BodyParts
{
    /// Dropdown order.
    public static readonly BodyPart[] All =
    [
        BodyPart.Penis, BodyPart.Vagina, BodyPart.Anus, BodyPart.LeftButtock, BodyPart.RightButtock,
        BodyPart.Mouth, BodyPart.LeftHand, BodyPart.RightHand, BodyPart.LeftFingers, BodyPart.RightFingers,
        BodyPart.LeftFoot, BodyPart.RightFoot, BodyPart.LeftToes, BodyPart.RightToes,
    ];

    public static string DisplayName(BodyPart part) => part switch
    {
        BodyPart.LeftButtock => "Left buttock",
        BodyPart.RightButtock => "Right buttock",
        BodyPart.LeftHand => "Left hand",
        BodyPart.RightHand => "Right hand",
        BodyPart.LeftFingers => "Left fingers",
        BodyPart.RightFingers => "Right fingers",
        BodyPart.LeftFoot => "Left foot",
        BodyPart.RightFoot => "Right foot",
        BodyPart.LeftToes => "Left toes",
        BodyPart.RightToes => "Right toes",
        _ => part.ToString(),
    };

    /// Bone groups tried in order; the first group with any bone present is averaged. iv_* bones
    /// come from the IVCS skeleton, j_* from vanilla.
    private static IReadOnlyList<string[]> BoneGroups(BodyPart part) => part switch
    {
        // iv_ochinko_a is the base, _f the tip.
        BodyPart.Penis => [["iv_ochinko_f"], ["iv_ochinko_e"]],
        BodyPart.Vagina => [["iv_omanko"]],
        BodyPart.Anus => [["iv_koumon"]],
        BodyPart.LeftButtock => [["iv_shiri_l"], ["j_shiri_l"]],
        BodyPart.RightButtock => [["iv_shiri_r"], ["j_shiri_r"]],
        // Midpoint of the lips.
        BodyPart.Mouth => [["j_f_ulip_01_l", "j_f_ulip_01_r", "j_f_dlip_01_l", "j_f_dlip_01_r"]],
        BodyPart.LeftHand => [["j_te_l"]],
        BodyPart.RightHand => [["j_te_r"]],
        // Between the index and middle fingertips.
        BodyPart.LeftFingers => [["iv_hito_c_l", "iv_naka_c_l"], ["j_hito_b_l", "j_naka_b_l"]],
        BodyPart.RightFingers => [["iv_hito_c_r", "iv_naka_c_r"], ["j_hito_b_r", "j_naka_b_r"]],
        // Middle of the sole, between ankle and toe base.
        BodyPart.LeftFoot => [["j_asi_d_l", "j_asi_e_l"]],
        BodyPart.RightFoot => [["j_asi_d_r", "j_asi_e_r"]],
        // The five toe tips averaged.
        BodyPart.LeftToes => [LeftToeTips, ["j_asi_e_l"]],
        BodyPart.RightToes => [RightToeTips, ["j_asi_e_r"]],
        _ => [],
    };

    private static readonly string[] LeftToeTips =
        ["iv_asi_oya_b_l", "iv_asi_hito_b_l", "iv_asi_naka_b_l", "iv_asi_kusu_b_l", "iv_asi_ko_b_l"];
    private static readonly string[] RightToeTips =
        ["iv_asi_oya_b_r", "iv_asi_hito_b_r", "iv_asi_naka_b_r", "iv_asi_kusu_b_r", "iv_asi_ko_b_r"];

    public static bool TryLocate(IPlayerCharacter character, BodyPart part, out Vector3 world) =>
        TryLocateGroups(character, BoneGroups(part), out world);

    private static bool TryLocateGroups(IPlayerCharacter character, IReadOnlyList<string[]> groups, out Vector3 world)
    {
        foreach (var group in groups)
        {
            if (BoneReader.TryGetAverageBonePosition(character, group, out world))
                return true;
        }
        world = default;
        return false;
    }
}
