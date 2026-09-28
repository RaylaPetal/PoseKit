namespace PoseKit.Bones;

/// <summary>
/// Which animation mod this player's current pose comes from, down to the trigger — alignment
/// memory's key source (see AlignmentMemory.KeyFor). Set by PoseKit's own plays (trigger buttons,
/// quick-pick, couple queue, presets), or resolved from Penumbra's selected options when an emote is
/// typed by hand; absent when neither can tell unambiguously.
/// </summary>
/// <param name="Group">The option group's name; empty for a mod's implicit default files.</param>
/// <param name="Trigger">The trigger's slash command (without "/") or pose display name — the same
/// text pairing's trigger hash uses.</param>
/// <param name="Pose">The pose this play enters, when the trigger names one; null for a slash-command
/// emote.</param>
/// <param name="FromPartnerAnchoredPreset">Started by a preset carrying a partner anchor, whose own
/// placement takes precedence over auto-align.</param>
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
