namespace PoseKit.Bones;

/// <summary>Where a Bone Align came from: the Align button, or alignment memory's auto-align.</summary>
public enum AlignOrigin { Manual, Memory }

/// <summary>Everything one Bone Align needs to know up front — which parts, how much gap, how to pick
/// the facing, and who asked.</summary>
public sealed record AlignRequest(BodyPart Self, BodyPart Partner, float Gap, AlignFacing Facing, AlignOrigin Origin)
{
    /// What the Align button asks for: the Bone Align section's current settings.
    public static AlignRequest FromConfiguration(Configuration configuration) => new(
        configuration.BoneAlignSelf, configuration.BoneAlignPartner, configuration.BoneAlignGap,
        configuration.BoneAlignMatchFacing ? AlignFacing.Auto : AlignFacing.Unchanged, AlignOrigin.Manual);

    /// An auto-align replaying a remembered entry.
    public static AlignRequest FromMemory(AlignmentEntry entry) =>
        new(entry.Self, entry.Partner, entry.Gap, entry.Facing, AlignOrigin.Memory);
}

/// <summary>How Bone Align picks this player's facing. Stored in alignment memory by name, so only
/// ever rename with care.</summary>
public enum AlignFacing
{
    /// Match facing's full chain: shared-origin snap, else the part-direction turn with its 180-degree
    /// overlap check. What a manual Align with Match facing on asks for; never stored as a result.
    Auto,

    /// Same heading as the partner's drawn model.
    SameWay,

    /// Opposite heading to the partner's drawn model.
    Facing,

    /// A quarter turn from the partner's heading, to their left.
    QuarterLeft,

    /// A quarter turn from the partner's heading, to their right.
    QuarterRight,

    /// The two parts' directions turned to face each other, recomputed from the current bodies.
    PartDirection,

    /// No turn.
    Unchanged,
}
