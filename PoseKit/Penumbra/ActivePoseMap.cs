using System.Collections.Generic;

namespace PoseKit.Penumbra;

/// <summary>Which selected options currently claim each pose — shared by the Animations tab's conflict
/// markers and alignment memory's resolution of hand-typed emotes.</summary>
public static class ActivePoseMap
{
    /// Maps each pose to the selected options (in enabled mods only) that replace it. More than one
    /// claimant means a conflict: only one redirect actually wins in Penumbra.
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
