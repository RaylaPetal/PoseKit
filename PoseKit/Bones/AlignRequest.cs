using System.Numerics;

namespace PoseKit.Bones;

/// <summary>
/// Where a Bone Align came from: the Align or Remember button, alignment memory's auto-align, or the
/// automatic re-remember after the player nudged a remembered animation's offset by hand.
/// </summary>
public enum AlignOrigin { Manual, Memory, Nudge }

/// <summary>Align moves this player; Measure only reads the current alignment, for Remember.</summary>
public enum AlignMode { Align, Measure }

/// <summary>The parameters of one Bone Align.</summary>
/// <param name="RelativeYaw">This player's drawn heading minus the partner's, to turn to before
/// sampling; null keeps the current facing.</param>
/// <param name="ContactOffset">Where to put the Self part relative to the Partner part, in the
/// partner's drawn frame; null stops <paramref name="Gap"/> short along the approach line.</param>
public sealed record AlignRequest(
    BodyPart Self,
    BodyPart Partner,
    float Gap,
    float? RelativeYaw,
    Vector3? ContactOffset,
    AlignOrigin Origin,
    AlignMode Mode = AlignMode.Align)
{
    /// The Align button: faces the partner's way first when that setting is on (a relative yaw of 0),
    /// otherwise keeps the current facing.
    public static AlignRequest FromConfiguration(Configuration configuration) => new(
        configuration.BoneAlignSelf, configuration.BoneAlignPartner, configuration.BoneAlignGap,
        configuration.BoneAlignFaceSameWay ? 0f : null, null, AlignOrigin.Manual);

    /// Remember reads how the player is placed now, facing included, so it never turns.
    public static AlignRequest ForMeasure(Configuration configuration) =>
        FromConfiguration(configuration) with { Mode = AlignMode.Measure, RelativeYaw = null };

    public static AlignRequest FromMemory(AlignmentEntry entry) => new(
        entry.Self, entry.Partner, entry.Gap, entry.RelativeYaw, entry.ContactOffset?.ToVector3(), AlignOrigin.Memory);

    /// Re-measures a remembered animation with its own parts, after a hand nudge.
    public static AlignRequest ForNudge(AlignmentEntry entry) => new(
        entry.Self, entry.Partner, entry.Gap, null, null, AlignOrigin.Nudge, AlignMode.Measure);
}

/// <summary>What a successful Align or Measure ended with, relative to the partner.</summary>
/// <param name="RelativeYaw">This player's drawn heading minus the partner's, in radians.</param>
/// <param name="ContactOffset">The Self part minus the Partner part, in the partner's drawn frame.</param>
public sealed record AlignResult(float RelativeYaw, Vector3 ContactOffset);
