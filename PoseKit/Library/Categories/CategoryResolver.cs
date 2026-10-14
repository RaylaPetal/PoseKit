using System;
using System.Collections.Generic;
using System.Linq;

namespace PoseKit.Library.Categories;

/// <summary>
/// Gives every library entry its themes, acts and hidden flag, later layers winning: automatic
/// categories (with their pairing fallbacks), then the player's categories.json, where rules add and
/// per-animation entries replace.
///
/// Automatic categories are worked out per animation, not per option: every option of one animation
/// (its roles, sizes and variants) gets the same themes and acts, so the animation is always listed
/// whole. Only the player's own entries can then tell its options apart.
/// </summary>
public static class CategoryResolver
{
    public static void Apply(IEnumerable<LibraryEntry> index, CategoryFile file)
    {
        foreach (var animation in index.GroupBy(e => e.AnimationKey))
        {
            var options = animation.ToList();
            var (themes, acts) = AutoCategories.Categorize(AutoCategories.Combine(options.Select(e =>
                new AutoCategoryInput(e.Mod.ModName, e.Group.Name, e.Option.Name, e.Group.Description,
                    e.Option.Description, e.IsCouple)).ToList()));

            foreach (var entry in options)
            {
                var entryThemes = new HashSet<string>(themes, StringComparer.OrdinalIgnoreCase);
                var entryActs = new HashSet<string>(acts, StringComparer.OrdinalIgnoreCase);
                var hidden = file.Source.Apply(entry.Mod.ModName, entry.Group.Name, entry.Option.Name, entryThemes, entryActs) ?? false;

                entry.Hidden = hidden;
                entry.Themes = entryThemes;
                entry.Acts = entryActs;
            }
        }
    }

    /// Automatic names first in their fixed order, then the player's own alphabetically.
    public static List<string> Order(IEnumerable<string> names, IReadOnlyList<string> automaticOrder) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => IndexOf(automaticOrder, n))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int IndexOf(IReadOnlyList<string> order, string name)
    {
        for (var i = 0; i < order.Count; i++)
            if (string.Equals(order[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }
}
