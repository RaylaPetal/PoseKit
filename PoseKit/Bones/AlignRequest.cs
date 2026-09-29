namespace PoseKit.Bones;

/// <summary>Where a Bone Align came from: the Align button, or alignment memory's auto-align.</summary>
public enum AlignOrigin { Manual, Memory }

/// <summary>The parameters of one Bone Align.</summary>
public sealed record AlignRequest(BodyPart Self, BodyPart Partner, float Gap, AlignFacing Facing, AlignOrigin Origin)
{
    public static AlignRequest FromConfiguration(Configuration configuration) => new(
        configuration.BoneAlignSelf, configuration.BoneAlignPartner, configuration.BoneAlignGap,
        configuration.BoneAlignMatchFacing ? AlignFacing.Auto : AlignFacing.Unchanged, AlignOrigin.Manual);

    public static AlignRequest FromMemory(AlignmentEntry entry) =>
        new(entry.Self, entry.Partner, entry.Gap, entry.Facing, AlignOrigin.Memory);
}

/// <summary>How Bone Align picks this player's facing. Stored by name, so don't rename members.</summary>
public enum AlignFacing
{
    /// Let Bone Align decide. Never stored as a result.
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
