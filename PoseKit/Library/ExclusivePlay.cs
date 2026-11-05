using System;
using System.Collections.Generic;
using System.Linq;

namespace PoseKit.Library;

public sealed record ExclusiveOption(string Name, IReadOnlySet<string> AnimationPaths);

public sealed record ExclusiveGroup(string Name, bool IsSingle, bool IsImplicit, IReadOnlyList<ExclusiveOption> Options, IReadOnlySet<string> Selected);

/// <summary>
/// Which settings make a requested animation the one that plays. Packs reuse a few emote slots for
/// everything, so options left on by earlier plays claim the same files; a saved setup with no
/// conflicts still builds them up over a session.
/// </summary>
public static class ExclusivePlay
{
    private static readonly string[] OffNames = ["none", "off", "disabled"];

    /// The groups whose selection must change for this option to win, with their new selections.
    /// Empty when nothing needs to change. A mod's default files can't be turned off, so they're skipped.
    public static Dictionary<string, HashSet<string>> SelectionChanges(IReadOnlyList<ExclusiveGroup> groups, string targetGroup, string targetOption)
    {
        var changes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var target = groups.FirstOrDefault(group => group.Name == targetGroup);
        if (target == null) return changes;

        // Selections go to Penumbra by name, and some packs reuse a name within a group, so every
        // option under this name counts as what's being played.
        var paths = target.Options.Where(candidate => candidate.Name == targetOption)
            .SelectMany(candidate => candidate.AnimationPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!target.Options.Any(candidate => candidate.Name == targetOption)) return changes;

        foreach (var group in groups)
        {
            if (group.IsImplicit) continue;

            var isTarget = ReferenceEquals(group, target);
            var conflicting = group.Options.Where(candidate =>
                group.Selected.Contains(candidate.Name) && !(isTarget && candidate.Name == targetOption) &&
                candidate.AnimationPaths.Overlaps(paths)).Select(candidate => candidate.Name).ToHashSet(StringComparer.Ordinal);

            HashSet<string> selection;
            if (isTarget)
            {
                selection = group.IsSingle ? [targetOption] : [.. group.Selected.Except(conflicting), targetOption];
            }
            else
            {
                if (conflicting.Count == 0) continue;
                selection = group.IsSingle ? OffState(group) : [.. group.Selected.Except(conflicting)];
            }

            if (!selection.SetEquals(group.Selected))
                changes[group.Name] = selection;
        }
        return changes;
    }

    /// The same off state the main window's Disabled entry uses: an animation-free option named as off,
    /// else any animation-free one, else nothing selected.
    private static HashSet<string> OffState(ExclusiveGroup group)
    {
        var free = group.Options.Where(option => option.AnimationPaths.Count == 0).ToList();
        var off = free.FirstOrDefault(option => OffNames.Contains(option.Name, StringComparer.OrdinalIgnoreCase)) ?? free.FirstOrDefault();
        return off != null ? [off.Name] : [];
    }

    /// The priority this mod needs to beat the mods actively claiming its files. It must be strictly
    /// higher, since a tie doesn't reliably go its way.
    public static int RequiredPriority(int own, IEnumerable<int> claimantPriorities)
    {
        var highest = claimantPriorities.DefaultIfEmpty(int.MinValue).Max();
        return highest < own ? own : highest + 1;
    }

    /// Normalizes a path Penumbra reports, for comparing with one built from the mod folder.
    public static string NormalizePath(string path) => path.Replace('\\', '/').ToLowerInvariant();

    /// What won each game path that didn't resolve to one of the option's own files. Null owner means
    /// the game's own file; otherwise the top folder under the mod root, or the whole path if outside it.
    public static List<(string GamePath, string? Owner)> Losses(string modRoot, string modDirectory,
        IReadOnlyDictionary<string, IReadOnlySet<string>> redirects, IReadOnlyDictionary<string, string> resolved)
    {
        var root = NormalizePath(modRoot).TrimEnd('/') + "/";
        var losses = new List<(string, string?)>();
        foreach (var (gamePath, files) in redirects)
        {
            if (!resolved.TryGetValue(gamePath, out var actual)) continue;

            var normalized = NormalizePath(actual);
            if (files.Any(file => normalized == NormalizePath($"{root}{modDirectory}/{file}"))) continue;

            if (normalized == NormalizePath(gamePath)) losses.Add((gamePath, null));
            else if (normalized.StartsWith(root, StringComparison.Ordinal))
                losses.Add((gamePath, normalized[root.Length..].Split('/')[0]));
            else losses.Add((gamePath, actual));
        }
        return losses;
    }
}
