namespace PoseKit.Bones;

/// <summary>
/// Which animation mod, option and trigger the current pose comes from. Used as the alignment
/// memory key.
/// </summary>
/// <param name="Group">The option group's name; empty for a mod's implicit default files.</param>
/// <param name="Trigger">The trigger's slash command (without "/") or pose display name.</param>
/// <param name="Pose">The pose this play enters; null for a slash-command emote.</param>
/// <param name="FromPartnerAnchoredPreset">Started by a partner-anchored preset, whose placement
/// takes precedence over auto-align.</param>
/// <param name="Generation">PoseTrigger.OffsetGeneration right after this play was triggered.</param>
/// <param name="SetAt">Environment.TickCount64 when this context was set.</param>
public sealed record PlayContext(
    string ModDirectory,
    string ModName,
    string Group,
    string Option,
    string Trigger,
    PoseIdentifier? Pose,
    bool FromPartnerAnchoredPreset,
    int Generation,
    long SetAt)
{
    public string Key => AlignmentMemory.KeyFor(ModDirectory, Group, Option, Trigger);
}
