using System.Collections.Generic;

namespace PoseKit.Penumbra;

/// <summary>Which selected options currently claim each pose — shared by the Animations tab's conflict
/// markers and alignment memory's resolution of hand-typed emotes.</summary>
public static class ActivePoseMap
{
    /// Every currently-*selected* option's pose triggers across the whole discovered-mods list, keyed
    /// by PoseIdentifier — only selected options in an *enabled* mod are actually "live" in Penumbra
    /// (a disabled mod contributes no file redirects at all, and Penumbra keeps remembering its last
    /// group selection even while it's off, so that stale selection must not count either), so those
    /// are the only ones that can genuinely collide. With a large curated pack like GoonersLife, it's
    /// easy to have e.g. two different groups (or two checked options in the same multi-select group)
    /// both claim "GroundSit Pose 3": only one of their file redirects actually wins in Penumbra, so
    /// playing either button may not produce what its own label promised.
    public static Dictionary<PoseIdentifier, List<(PoseModInfo Mod, PoseModOption Option)>> Build(List<PoseModInfo> mods)
    {
        var map = new Dictionary<PoseIdentifier, List<(PoseModInfo, PoseModOption)>>();
        foreach (var mod in mods)
        {
            if (!mod.Enabled) continue;

            foreach (var group in mod.Groups)
            {
                foreach (var option in group.Options)
                {
                    if (!group.Selected.Contains(option.Name)) continue;
                    foreach (var trigger in option.Triggers)
                    {
                        if (trigger.PoseIdentifier is not { } pid) continue;
                        if (!map.TryGetValue(pid, out var claimants))
                            map[pid] = claimants = [];
                        claimants.Add((mod, option));
                    }
                }
            }
        }
        return map;
    }
}
