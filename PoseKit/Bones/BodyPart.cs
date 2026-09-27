using System.Collections.Generic;

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
}

public static class BodyParts
{
    public static readonly BodyPart[] All =
    [
        BodyPart.Penis, BodyPart.Vagina, BodyPart.Anus, BodyPart.LeftButtock, BodyPart.RightButtock,
        BodyPart.Mouth, BodyPart.LeftHand, BodyPart.RightHand,
    ];

    public static string DisplayName(BodyPart part) => part switch
    {
        BodyPart.LeftButtock => "Left buttock",
        BodyPart.RightButtock => "Right buttock",
        BodyPart.LeftHand => "Left hand",
        BodyPart.RightHand => "Right hand",
        _ => part.ToString(),
    };

    /// Skeleton bone names for each body part, first match wins — so alternates (a different body
    /// mod's naming, or a nearby fallback bone) can be listed after the preferred one. IVCS names are
    /// the IVCS body mod's own bones (Ktisis shows them as "IVCS Penis A".."F", "IVCS Vagina", etc.);
    /// the j_* names are the vanilla skeleton's. Confirm or extend with "/posekit bones".
    public static IReadOnlyList<string> CandidateBones(BodyPart part) => part switch
    {
        BodyPart.Penis => ["iv_ochinko_f", "iv_ochinko_e"],
        BodyPart.Vagina => ["iv_omanko"],
        BodyPart.Anus => ["iv_koumon"],
        BodyPart.LeftButtock => ["iv_shiri_l", "j_shiri_l"],
        BodyPart.RightButtock => ["iv_shiri_r", "j_shiri_r"],
        BodyPart.Mouth => ["j_f_dlip", "j_f_ulip", "j_ago"],
        BodyPart.LeftHand => ["j_te_l"],
        BodyPart.RightHand => ["j_te_r"],
        _ => [],
    };
}
