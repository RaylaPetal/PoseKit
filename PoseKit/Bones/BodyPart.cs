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

    /// Vanilla bones outlining the body, used to check whether two bodies overlap.
    public static readonly string[] BodyOutline =
    [
        "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi", "j_kao",
        "j_ude_a_l", "j_ude_a_r", "j_ude_b_l", "j_ude_b_r",
        "j_asi_a_l", "j_asi_a_r", "j_asi_b_l", "j_asi_b_r", "j_asi_c_l", "j_asi_c_r", "j_asi_d_l", "j_asi_d_r",
    ];

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

    // Back and front of the pelvis, for a mostly horizontal front/back axis.
    private static readonly string[][] PelvisBack = [["iv_shiri_l", "iv_shiri_r"], ["j_kosi"]];
    private static readonly string[][] PelvisFront = [["iv_omanko"], ["iv_ochinko_a"]];

    /// Which way a part faces, as a from/to pair of bone groups. Bone rotations aren't used because
    /// their axes differ between body mods.
    private static (IReadOnlyList<string[]> From, IReadOnlyList<string[]> To) DirectionGroups(BodyPart part) => part switch
    {
        BodyPart.Penis => ([["iv_ochinko_a"]], [["iv_ochinko_f"], ["iv_ochinko_e"]]),
        BodyPart.Vagina => (PelvisBack, PelvisFront),
        BodyPart.Anus => (PelvisFront, PelvisBack),
        BodyPart.LeftButtock => (PelvisFront, [["iv_shiri_l"], ["j_shiri_l"]]),
        BodyPart.RightButtock => (PelvisFront, [["iv_shiri_r"], ["j_shiri_r"]]),
        BodyPart.Mouth => ([["j_kao"]], BoneGroups(BodyPart.Mouth)),
        BodyPart.LeftHand => ([["j_te_l"]], [["j_naka_a_l"]]),
        BodyPart.RightHand => ([["j_te_r"]], [["j_naka_a_r"]]),
        BodyPart.LeftFingers => ([["j_hito_a_l", "j_naka_a_l"]], BoneGroups(BodyPart.LeftFingers)),
        BodyPart.RightFingers => ([["j_hito_a_r", "j_naka_a_r"]], BoneGroups(BodyPart.RightFingers)),
        BodyPart.LeftFoot => ([["j_asi_d_l"]], [["j_asi_e_l"]]),
        BodyPart.RightFoot => ([["j_asi_d_r"]], [["j_asi_e_r"]]),
        BodyPart.LeftToes => ([["j_asi_d_l"]], BoneGroups(BodyPart.LeftToes)),
        BodyPart.RightToes => ([["j_asi_d_r"]], BoneGroups(BodyPart.RightToes)),
        _ => ([], []),
    };

    /// Unnormalized world direction.
    public static bool TryGetDirection(IPlayerCharacter character, BodyPart part, out Vector3 direction)
    {
        direction = default;
        var (from, to) = DirectionGroups(part);
        if (!TryLocateGroups(character, from, out var start) || !TryLocateGroups(character, to, out var end))
            return false;
        direction = end - start;
        return true;
    }
}
