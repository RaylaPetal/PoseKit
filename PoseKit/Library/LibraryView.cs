using System;
using System.Collections.Generic;
using System.Linq;
using PoseKit.Library.Categories;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Library;

public enum LibraryTypeFilter { All, Solo, Couple }

public enum CategoryLevel { Home, All, Hidden, Root }

/// <summary>
/// Where the player is in the category tree. Category is null for a pairing root's category list (or
/// the rows of Solo, Idles and Dancing), and <see cref="LibraryView.OtherCategory"/> for a pairing
/// root's options with no category.
/// </summary>
public sealed record CategoryPath(CategoryLevel Level, string? Root = null, string? Category = null)
{
    public static readonly CategoryPath Home = new(CategoryLevel.Home);
}

/// <summary>
/// The quick library's filtered contents. Animation sections and category counts are cached until
/// the search, filter, path, context, categories or scan change, since they span 1000+ options;
/// presets are few and filtered each call.
/// </summary>
public sealed class LibraryView
{
    public const string OtherCategory = "Other";

    public sealed record Row(LibraryEntry Entry, List<PoseTriggerHint> Triggers);

    /// One option group: in most packs the animation itself, its options the roles or variants.
    public sealed record Animation(PoseModGroup Group, List<Row> Rows)
    {
        public bool IsCouple { get; } = Rows.Any(r => r.Entry.IsCouple);
    }

    public sealed record ModBlock(PoseModInfo Mod, List<Animation> Animations)
    {
        /// Animations, not options (see LibraryEntry.AnimationKey).
        public int Count { get; } = Animations.SelectMany(a => a.Rows).Select(r => r.Entry.AnimationKey).Distinct().Count();
    }

    /// Counts are of animations (see LibraryEntry.AnimationKey), not options, and include only those
    /// with a play control in the context that pass the type filter.
    public sealed class CategoryCounts
    {
        /// Every root on a visible option, in display order, with its playable count.
        public List<(string Name, int Count)> Roots { get; } = [];

        /// Per pairing root: its acts in display order with counts.
        public Dictionary<string, List<(string Name, int Count)>> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// Per pairing root: playable options with no act, when there are any.
        public Dictionary<string, int> Other { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int All { get; set; }
        public int Hidden { get; set; }
        public bool AnyHidden { get; set; }

        /// Names offered when tagging: the roots (themes) or automatic acts, plus the player's own.
        public List<string> KnownThemes { get; set; } = [];
        public List<string> KnownActs { get; set; } = [];
    }

    private (int IndexVersion, int CategoryVersion, ILibraryContext Context, string Search, LibraryTypeFilter Filter, CategoryPath Path)? cachedKey;
    private List<ModBlock> cachedTree = [];

    private (int IndexVersion, int CategoryVersion, ILibraryContext Context, LibraryTypeFilter Filter)? countsKey;
    private CategoryCounts cachedCounts = new();

    /// The rows for <paramref name="path"/> (the whole tree from Home while searching), laid out as in the
    /// main window: mod, then option group, then options, each showing only what can play here.
    public List<ModBlock> GetTree(IReadOnlyList<LibraryEntry> index, int indexVersion, int categoryVersion,
        ILibraryContext context, string search, LibraryTypeFilter filter, CategoryPath path)
    {
        var text = search.Trim().ToLowerInvariant();
        var key = (indexVersion, categoryVersion, context, text, filter, path);
        if (cachedKey == key) return cachedTree;

        cachedTree = index
            .Where(entry => EntryMatches(entry, text, filter) &&
                            (text.Length > 0 && path.Level == CategoryLevel.Home ? !entry.Hidden : InPath(entry, path)))
            .Select(entry => new Row(entry, entry.Option.Triggers.Where(t => context.Sections.Any(s => s.Includes(t))).ToList()))
            .Where(row => row.Triggers.Count > 0)
            .GroupBy(row => row.Entry.Mod)
            .OrderBy(mod => mod.Key.ModName, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new ModBlock(mod.Key, mod
                .GroupBy(row => row.Entry.Group)
                .Select(group => new Animation(group.Key, group.ToList()))
                .ToList()))
            .ToList();
        cachedKey = key;
        return cachedTree;
    }

    public CategoryCounts GetCounts(IReadOnlyList<LibraryEntry> index, int indexVersion, int categoryVersion,
        ILibraryContext context, LibraryTypeFilter filter)
    {
        var key = (indexVersion, categoryVersion, context, filter);
        if (countsKey == key) return cachedCounts;

        var counts = new CategoryCounts();
        // Playable animation keys per name; a name is listed (dimmed at zero) even with none playable.
        var rootKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var categoryKeys = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var otherKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var allKeys = new HashSet<string>();
        var hiddenKeys = new HashSet<string>();
        var allThemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allActs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in index)
        {
            allThemes.UnionWith(entry.Themes);
            allActs.UnionWith(entry.Acts);

            var playable = EntryMatches(entry, "", filter) &&
                           entry.Option.Triggers.Any(t => context.Sections.Any(s => s.Includes(t)));
            var animation = entry.AnimationKey;
            if (entry.Hidden)
            {
                counts.AnyHidden = true;
                if (playable) hiddenKeys.Add(animation);
                continue;
            }

            if (playable) allKeys.Add(animation);
            foreach (var root in entry.Themes.Where(AutoCategories.IsRoot))
            {
                Tally(rootKeys, root, animation, playable);
                if (!AutoCategories.PairingRoots.Contains(root)) continue;

                if (!categoryKeys.TryGetValue(root, out var categories))
                    categoryKeys[root] = categories = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var any = false;
                // A pairing root is browsed by act (Oral, Vaginal, …); styles like BDSM only pick roots.
                foreach (var category in entry.Acts)
                {
                    Tally(categories, category, animation, playable);
                    any = true;
                }
                if (!any) Tally(otherKeys, root, animation, playable);
            }
        }

        counts.All = allKeys.Count;
        counts.Hidden = hiddenKeys.Count;
        foreach (var root in CategoryResolver.Order(rootKeys.Keys, AutoCategories.ThemeOrder))
            counts.Roots.Add((root, rootKeys[root].Count));
        foreach (var (root, categories) in categoryKeys)
            counts.Categories[root] = CategoryResolver.Order(categories.Keys, AutoCategories.ActOrder)
                .Select(c => (c, categories[c].Count)).ToList();
        foreach (var (root, keys) in otherKeys)
            counts.Other[root] = keys.Count;
        // Automatic styles (BDSM, Romantic, …) only help pick a root and aren't browsed, so tagging
        // offers the roots plus the player's own names.
        counts.KnownThemes = CategoryResolver.Order(
            AutoCategories.Roots.Concat(allThemes.Where(t => !AutoCategories.ThemeOrder.Contains(t, StringComparer.OrdinalIgnoreCase))),
            AutoCategories.ThemeOrder);
        counts.KnownActs = CategoryResolver.Order(AutoCategories.ActOrder.Concat(allActs), AutoCategories.ActOrder);

        cachedCounts = counts;
        countsKey = key;
        return counts;
    }

    private static void Tally(Dictionary<string, HashSet<string>> keys, string name, string animation, bool playable)
    {
        if (!keys.TryGetValue(name, out var set)) keys[name] = set = [];
        if (playable) set.Add(animation);
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

    private static bool InPath(LibraryEntry entry, CategoryPath path) => path.Level switch
    {
        CategoryLevel.All => !entry.Hidden,
        CategoryLevel.Hidden => entry.Hidden,
        CategoryLevel.Root => !entry.Hidden && path.Root != null && entry.Themes.Contains(path.Root) && path.Category switch
        {
            null => true,
            OtherCategory => entry.Acts.Count == 0,
            var category => entry.Acts.Contains(category),
        },
        _ => false,
    };

    private static bool EntryMatches(LibraryEntry entry, string lowerSearch, LibraryTypeFilter filter)
    {
        if (filter == LibraryTypeFilter.Solo && entry.IsCouple) return false;
        if (filter == LibraryTypeFilter.Couple && !entry.IsCouple) return false;
        return lowerSearch.Length == 0 || entry.SearchText.Contains(lowerSearch, StringComparison.Ordinal);
    }
}
