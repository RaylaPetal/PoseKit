using System;
using System.Collections.Generic;
using System.Linq;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Library;

/// New values go last, since the setting is saved by number.
public enum LibraryTypeFilter { All, Solo, Couple, FxF, MxF }

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

    private (int IndexVersion, int TagVersion, int FavoritesVersion, ILibraryContext Context, string Search, LibraryTypeFilter Filter)? cachedKey;
    private List<ModBlock> cachedTree = [];
    private List<ModBlock> cachedDoesntFit = [];
    private List<Row> cachedFavorites = [];
    private Dictionary<string, LibraryEntry> entriesById = new(StringComparer.Ordinal);
    private int entriesByIdVersion = -1;

    /// The starred rows of the last GetTree, for the Favorites section; they stay in the tree too.
    public List<Row> Favorites => cachedFavorites;

    /// From the last GetTree: options whose declared pairing contradicts the filter.
    public List<ModBlock> DoesntFit => cachedDoesntFit;

    /// Mod, then option group, then options, each showing only what can play here. These are the
    /// options that fit; with no pairing filter, that's everything the filter lists.
    public List<ModBlock> GetTree(IReadOnlyList<LibraryEntry> index, int indexVersion, int tagVersion, Configuration configuration,
        ILibraryContext context, string search, LibraryTypeFilter filter)
    {
        var text = search.Trim().ToLowerInvariant();
        var key = (indexVersion, tagVersion, Library.Favorites.Version, context, text, filter);
        if (cachedKey == key) return cachedTree;

        var rows = index
            .Where(entry => text.Length == 0 || entry.SearchText.Contains(text, StringComparison.Ordinal))
            .Select(entry => (Bucket: LibraryBuckets.Classify(filter, entry.Pairing, entry.PairingIsDefault, entry.IsCouple),
                Row: new Row(entry, entry.Option.Triggers.Where(t => context.Sections.Any(s => s.Includes(t))).ToList(),
                    Library.Favorites.IsFavorite(configuration, entry.Mod, entry.Group, entry.Option))))
            .Where(item => item.Bucket != LibraryBucket.NotListed && item.Row.Triggers.Count > 0)
            .ToList();

        cachedTree = TreeOf(rows, LibraryBucket.Fits);
        cachedDoesntFit = TreeOf(rows, LibraryBucket.DoesntFit);
        cachedFavorites = rows.Where(item => item.Bucket == LibraryBucket.Fits && item.Row.Favorite)
            .Select(item => item.Row).ToList();
        cachedKey = key;
        return cachedTree;
    }

    private static List<ModBlock> TreeOf(List<(LibraryBucket Bucket, Row Row)> rows, LibraryBucket bucket) =>
        rows.Where(item => item.Bucket == bucket)
            .Select(item => item.Row)
            .GroupBy(row => row.Entry.Mod)
            .OrderBy(mod => mod.Key.ModName, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new ModBlock(mod.Key,
                mod.GroupBy(row => row.Entry.Group).Select(group => new Animation(group.Key, group.ToList())).ToList()))
            .ToList();

    /// The recorded animations that exist and can play here, newest first. The pairing filter doesn't apply:
    /// the player has already used these, whatever their tags say.
    public List<Row> GetRecent(IReadOnlyList<LibraryEntry> index, int indexVersion, IReadOnlyList<string> recent,
        Configuration configuration, ILibraryContext context, string search)
    {
        if (entriesByIdVersion != indexVersion)
        {
            entriesById = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
            foreach (var entry in index)
                entriesById.TryAdd(entry.Id, entry);
            entriesByIdVersion = indexVersion;
        }

        var text = search.Trim().ToLowerInvariant();
        var rows = new List<Row>(recent.Count);
        foreach (var id in recent)
        {
            if (!entriesById.TryGetValue(id, out var entry) || !entry.SearchText.Contains(text, StringComparison.Ordinal)) continue;

            var triggers = entry.Option.Triggers.Where(t => context.Sections.Any(s => s.Includes(t))).ToList();
            if (triggers.Count > 0)
                rows.Add(new Row(entry, triggers, Library.Favorites.IsFavorite(configuration, entry.Mod, entry.Group, entry.Option)));
        }
        return rows;
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
                LibraryTypeFilter.Couple or LibraryTypeFilter.FxF or LibraryTypeFilter.MxF => p.PartnerHalf != null,
                _ => true,
            })
            .Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
