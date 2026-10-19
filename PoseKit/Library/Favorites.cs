using System.Collections.Generic;
using System.Linq;
using PoseKit.Penumbra;

namespace PoseKit.Library;

/// <summary>
/// Animations (mod options with something to play) the player starred. Both the main window and the
/// library list them in a Favorites section of their own above the mods, so one animation deep in a
/// big pack's list group is a click away however many there are.
/// </summary>
public static class Favorites
{
    /// Bumped on every change, for views that cache their order.
    public static int Version { get; private set; }

    private static string Key(PoseModInfo mod, PoseModGroup group, PoseModOption option) =>
        $"{mod.ModDirectory}|{group.Name}|{option.Name}";

    public static bool IsFavorite(Configuration configuration, PoseModInfo mod, PoseModGroup group, PoseModOption option) =>
        configuration.FavoriteOptions.Contains(Key(mod, group, option));

    public static void Toggle(Configuration configuration, PoseModInfo mod, PoseModGroup group, PoseModOption option)
    {
        var key = Key(mod, group, option);
        if (!configuration.FavoriteOptions.Remove(key)) configuration.FavoriteOptions.Add(key);
        configuration.Save();
        Version++;
    }

    /// Every starred option that can play, by mod name, then in each mod's order.
    public static List<(PoseModInfo Mod, PoseModGroup Group, PoseModOption Option)> All(Configuration configuration,
        IEnumerable<PoseModInfo> mods) =>
        mods.OrderBy(m => m.ModName, System.StringComparer.OrdinalIgnoreCase)
            .SelectMany(m => m.Groups.SelectMany(g => g.Options
                .Where(o => o.Triggers.Count > 0 && IsFavorite(configuration, m, g, o))
                .Select(o => (m, g, o))))
            .ToList();

    /// Where a favorite lives, shown beside it: "Mod › Group", or the mod for its default files.
    public static string Context(PoseModInfo mod, PoseModGroup group) =>
        group.IsImplicit ? mod.ModName : $"{mod.ModName} › {group.Name}";
}
