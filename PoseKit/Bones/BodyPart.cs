using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace PoseKit.Bones;

/// <summary>A body part Bone Align can line up — persisted in Configuration as its numeric value, so
/// only ever append new members (never reorder or remove).</summary>
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
}

public static class BodyParts
{
    /// Dropdown order — independent of the enum's (append-only) numeric order.
    public static readonly BodyPart[] All =
    [
        BodyPart.Penis, BodyPart.Vagina, BodyPart.Anus, BodyPart.LeftButtock, BodyPart.RightButtock,
        BodyPart.Mouth, BodyPart.LeftHand, BodyPart.RightHand, BodyPart.LeftFingers, BodyPart.RightFingers,
    ];

    public static string DisplayName(BodyPart part) => part switch
    {
        BodyPart.LeftButtock => "Left buttock",
        BodyPart.RightButtock => "Right buttock",
        BodyPart.LeftHand => "Left hand",
        BodyPart.RightHand => "Right hand",
        BodyPart.LeftFingers => "Left fingers",
        BodyPart.RightFingers => "Right fingers",
        _ => part.ToString(),
    };

    /// How each body part is located, as bone groups tried in order: the first group with at least
    /// one bone present wins, and that group's found bones are averaged. A single-bone group is just
    /// that bone; later groups are fallbacks (another body mod's naming, or a nearby vanilla bone).
    /// IVCS names (iv_*) are the IVCS body mod's own bones, j_* are the vanilla skeleton's — all
    /// verified against "/posekit bones".
    private static IReadOnlyList<string[]> BoneGroups(BodyPart part) => part switch
    {
        // iv_ochinko_a is the base, _f the tip.
        BodyPart.Penis => [["iv_ochinko_f"], ["iv_ochinko_e"]],
        BodyPart.Vagina => [["iv_omanko"]],
        BodyPart.Anus => [["iv_koumon"]],
        BodyPart.LeftButtock => [["iv_shiri_l"], ["j_shiri_l"]],
        BodyPart.RightButtock => [["iv_shiri_r"], ["j_shiri_r"]],
        // No single center bone — the midpoint of the upper and lower lips is the mouth opening.
        BodyPart.Mouth => [["j_f_ulip_01_l", "j_f_ulip_01_r", "j_f_dlip_01_l", "j_f_dlip_01_r"]],
        // Wrist/palm root — for grabbing, holding, spanking.
        BodyPart.LeftHand => [["j_te_l"]],
        BodyPart.RightHand => [["j_te_r"]],
        // Between the index and middle fingertips (IVCS tip joints), falling back to the vanilla
        // second joints when a character has no IVCS finger tips.
        BodyPart.LeftFingers => [["iv_hito_c_l", "iv_naka_c_l"], ["j_hito_b_l", "j_naka_b_l"]],
        BodyPart.RightFingers => [["iv_hito_c_r", "iv_naka_c_r"], ["j_hito_b_r", "j_naka_b_r"]],
        _ => [],
    };

    /// Where <paramref name="part"/> is on <paramref name="character"/> right now, in world space.
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

    // The pelvis's back (buttocks) and front (vagina, else penis base) — its front/back axis is far
    // more horizontal than a pelvis-bone-to-vagina line, which points mostly down.
    private static readonly string[][] PelvisBack = [["iv_shiri_l", "iv_shiri_r"], ["j_kosi"]];
    private static readonly string[][] PelvisFront = [["iv_omanko"], ["iv_ochinko_a"]];

    /// Which way <paramref name="part"/> faces, as a "from → to" pair of bone groups — derived from two
    /// bones' positions rather than any single bone's own rotation, since bone axis conventions aren't
    /// consistent across body mods. For pointing parts it's where they point (penis base → tip,
    /// knuckles → fingertips, wrist → knuckle, head → lips); for openings it's which way they open
    /// (vagina: pelvis back → front; anus and buttocks: front → back). Bone Align's facing match
    /// turns this player so the two chosen parts' directions face each other.
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
        _ => ([], []),
    };

    /// The (unnormalized) world direction <paramref name="part"/> faces right now — see DirectionGroups.
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
