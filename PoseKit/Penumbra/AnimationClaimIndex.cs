using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PoseKit.Penumbra;

public readonly record struct AnimationClaim(string ModDirectory, string Group, string Option);

/// <summary>
/// Which installed mods claim which animation game paths, so a pack only outranks mods that really
/// compete for its files. Reads every mod's meta.json, so it's built off the framework thread; until
/// it's ready, playing just doesn't raise priorities.
/// </summary>
public sealed class AnimationClaimIndex
{
    private volatile Dictionary<string, List<AnimationClaim>>? claims;
    private int version;

    /// <param name="modDirectories">Read on the framework thread, since Penumbra is only asked there.</param>
    public void Rebuild(string? modRoot, IReadOnlyCollection<string> modDirectories)
    {
        var current = ++version;
        if (modRoot == null) return;

        Task.Run(() =>
        {
            var built = new Dictionary<string, List<AnimationClaim>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in modDirectories)
            {
                foreach (var (group, option, paths) in PenumbraPoseScanner.TryReadAnimationClaims(modRoot, mod))
                {
                    foreach (var path in paths)
                    {
                        if (!built.TryGetValue(path, out var list)) built[path] = list = [];
                        list.Add(new AnimationClaim(mod, group, option));
                    }
                }
            }
            if (current == version) claims = built;
        }).ContinueWith(task => Plugin.Log.Warning(task.Exception!, "[PoseKit] Couldn't index animation mods."), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// The other mods with an active option claiming any of these paths, as (mod, priority).
    public List<(string Mod, int Priority)> ActiveClaimants(IEnumerable<string> paths, string ownMod,
        IReadOnlyDictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Selections)> settings)
    {
        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        if (claims is not { } index) return [];

        foreach (var path in paths)
        {
            if (!index.TryGetValue(path, out var list)) continue;
            foreach (var claim in list)
            {
                if (claim.ModDirectory == ownMod || !settings.TryGetValue(claim.ModDirectory, out var mod) || !mod.Enabled) continue;
                if (claim.Group.Length == 0 || (mod.Selections.TryGetValue(claim.Group, out var selected) && selected.Contains(claim.Option)))
                    active[claim.ModDirectory] = mod.Priority;
            }
        }
        return active.Select(pair => (pair.Key, pair.Value)).ToList();
    }
}
