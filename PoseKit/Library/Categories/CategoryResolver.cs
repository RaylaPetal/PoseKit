using System;
using System.Collections.Generic;
using System.Linq;

namespace PoseKit.Library.Categories;

/// <summary>
/// Gives every library entry its themes, acts and hidden flag, later layers winning: automatic
/// categories (with their pairing fallbacks), then the player's categories.json, where rules add and
/// per-animation entries replace.
/// </summary>
public static class CategoryResolver
{
    public static void Apply(IEnumerable<LibraryEntry> index, CategoryFile file)
    {
        foreach (var entry in index)
        {
            var themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var acts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var input = new AutoCategoryInput(entry.Mod.ModName, entry.Group.Name, entry.Option.Name,
                entry.Group.Description, entry.Option.Description, entry.IsCouple);
            AutoCategories.Apply(input, themes, acts);

            AutoCategories.AddPairingFallbacks(input, themes, acts);
            var hidden = file.Source.Apply(entry.Mod.ModName, entry.Group.Name, entry.Option.Name, themes, acts) ?? false;

            entry.Hidden = hidden;
            entry.Themes = themes;
            entry.Acts = acts;
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
