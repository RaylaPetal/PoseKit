using System;
using System.Collections.Generic;
using System.Linq;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Library;

public enum LibraryTypeFilter { All, Solo, Couple }

/// <summary>
/// The quick library's filtered contents: the main window's mods and option groups, limited to what
/// can play in the current context, plus the starred ones. Cached until the search, filter, context,
/// favorites or scan change, since they span 1000+ options; presets are few and filtered each call.
/// </summary>
public sealed class LibraryView
{
    public sealed record Row(LibraryEntry Entry, List<PoseTriggerHint> Triggers, bool Favorite);

    /// One option group: in most packs the animation itself, its options the roles or variants.
    public sealed record Animation(PoseModGroup Group, List<Row> Rows);

    public sealed record ModBlock(PoseModInfo Mod, List<Animation> Animations);

    private (int IndexVersion, int FavoritesVersion, ILibraryContext Context, string Search, LibraryTypeFilter Filter)? cachedKey;
    private List<ModBlock> cachedTree = [];
    private List<Row> cachedFavorites = [];

    /// The starred rows of the last GetTree, for the Favorites section; they stay in the tree too.
    public List<Row> Favorites => cachedFavorites;

    /// Mod, then option group, then options, each showing only what can play here.
    public List<ModBlock> GetTree(IReadOnlyList<LibraryEntry> index, int indexVersion, Configuration configuration,
        ILibraryContext context, string search, LibraryTypeFilter filter)
    {
        var text = search.Trim().ToLowerInvariant();
        var key = (indexVersion, Library.Favorites.Version, context, text, filter);
        if (cachedKey == key) return cachedTree;

        cachedTree = index
            .Where(entry => EntryMatches(entry, text, filter))
            .Select(entry => new Row(entry, entry.Option.Triggers.Where(t => context.Sections.Any(s => s.Includes(t))).ToList(),
                Library.Favorites.IsFavorite(configuration, entry.Mod, entry.Group, entry.Option)))
            .Where(row => row.Triggers.Count > 0)
            .GroupBy(row => row.Entry.Mod)
            .OrderBy(mod => mod.Key.ModName, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new ModBlock(mod.Key,
                mod.GroupBy(row => row.Entry.Group).Select(group => new Animation(group.Key, group.ToList())).ToList()))
            .ToList();
        cachedFavorites = cachedTree.SelectMany(m => m.Animations).SelectMany(a => a.Rows).Where(r => r.Favorite).ToList();
        cachedKey = key;
        return cachedTree;
    }

    public static List<NamedPose> GetPresets(IReadOnlyList<NamedPose> presets, ILibraryContext context, string search,
        LibraryTypeFilter filter)
    {
        var text = search.Trim();
        return presets
            .Where(context.PresetFits)
            .Where(p => filter switch
            {
                LibraryTypeFilter.Solo => p.PartnerHalf == null,
                LibraryTypeFilter.Couple => p.PartnerHalf != null,
                _ => true,
            })
            .Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool EntryMatches(LibraryEntry entry, string lowerSearch, LibraryTypeFilter filter)
    {
        if (filter == LibraryTypeFilter.Solo && entry.IsCouple) return false;
        if (filter == LibraryTypeFilter.Couple && !entry.IsCouple) return false;
        return lowerSearch.Length == 0 || entry.SearchText.Contains(lowerSearch, StringComparison.Ordinal);
    }
}
