using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PoseKit.Bones;
using PoseKit.Penumbra;

namespace PoseKit.Library;

/// <summary>
/// Decides each library option's pairing state and keeps it current. Names and descriptions are read
/// with the scan; everything keyed by animation follows once the files are hashed in the background,
/// so a first scan of a big pack never holds up the game.
/// </summary>
public sealed class PairingTagService : IDisposable
{
    private readonly PairingTagStore store;
    private readonly AlignmentMemory alignments;
    private readonly Func<string?> modRoot;
    private readonly Func<IEnumerable<string>?> installedMods;

    private CancellationTokenSource? hashing;
    private Task<HashResult>? hashTask;
    private IReadOnlyList<LibraryEntry> entries = [];

    // Animation identities of aligned options outside the library, so an alignment made under a mod
    // that isn't selected still counts for the same animation in one that is.
    private Dictionary<string, string> otherAlignedKeys = new(StringComparer.Ordinal);

    // What installed mods that aren't selected say about their animations, so that GoonersLife+ can use
    // the role codes Nightlife+ wrote for the same files without Nightlife+ being selected.
    private List<(string? Key, CouplePairing? Declared, WordReading Words)> otherModEvidence = [];

    private sealed record OtherModOption(string Id, CouplePairing? Declared, WordReading Words);

    private sealed record HashResult(Dictionary<string, string> Keys, Dictionary<string, CachedFileHash> Cache, List<OtherModOption> OtherOptions);

    private const string OtherModPrefix = "other:";

    /// Bumped whenever a state may have changed, for views that cache by it.
    public int Version { get; private set; }

    public bool LoadFailed => store.LoadFailed;

    public PairingTagService(string configDirectory, AlignmentMemory alignments, Func<string?> modRoot, Func<IEnumerable<string>?> installedMods)
    {
        this.installedMods = installedMods;
        store = new PairingTagStore(configDirectory);
        this.alignments = alignments;
        this.modRoot = modRoot;
        alignments.Changed += Apply;
    }

    public void Dispose()
    {
        alignments.Changed -= Apply;
        hashing?.Cancel();
    }

    /// Starts over for a new scan. Any run still in progress is for entries that no longer exist.
    public void Rebuild(IReadOnlyList<LibraryEntry> newEntries)
    {
        hashing?.Cancel();
        hashing = new CancellationTokenSource();
        entries = newEntries;
        otherAlignedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        otherModEvidence = [];
        Apply();

        var jobs = newEntries.Select(entry => new AnimationHashJob(entry.Id, entry.Mod.ModPath, entry.Option.AnimationFiles,
            AnimationHasher.NameKey(entry.Mod.ModDirectory, entry.Group.Name, entry.Option.Name))).ToList();
        var listed = newEntries.Select(AlignedKeyOf).ToHashSet(StringComparer.Ordinal);
        var aligned = alignments.Entries
            .Select(entry => (entry.ModDirectory, entry.Group, entry.Option))
            .Distinct()
            .Where(option => !listed.Contains(AlignedKey(option.ModDirectory, option.Group, option.Option)))
            .ToList();

        // Read now: Penumbra can only be asked from the framework thread.
        var root = modRoot();
        var selected = newEntries.Select(entry => entry.Mod.ModDirectory).ToHashSet(StringComparer.Ordinal);
        var otherMods = (installedMods() ?? []).Where(mod => !selected.Contains(mod)).ToList();
        var cache = new Dictionary<string, CachedFileHash>(store.HashCache, StringComparer.OrdinalIgnoreCase);
        var token = hashing.Token;
        hashTask = Task.Run(() =>
        {
            var otherOptions = new List<OtherModOption>();
            if (root != null)
            {
                jobs.AddRange(AlignedJobs(root, aligned));
                jobs.AddRange(OtherModJobs(root, otherMods, otherOptions, token));
            }
            return new HashResult(AnimationHasher.Compute(jobs, cache, token), cache, otherOptions);
        }, token);
    }

    private static IEnumerable<AnimationHashJob> AlignedJobs(string root, List<(string ModDirectory, string Group, string Option)> aligned)
    {
        foreach (var option in aligned)
        {
            var files = PenumbraPoseScanner.TryReadAnimationFiles(root, option.ModDirectory, option.Group, option.Option);
            if (files is { Count: > 0 })
                yield return new AnimationHashJob(AlignedKey(option.ModDirectory, option.Group, option.Option),
                    System.IO.Path.Combine(root, option.ModDirectory), files,
                    AnimationHasher.NameKey(option.ModDirectory, option.Group, option.Option));
        }
    }

    private static IEnumerable<AnimationHashJob> OtherModJobs(string root, List<string> mods, List<OtherModOption> options, CancellationToken token)
    {
        foreach (var mod in mods)
        {
            token.ThrowIfCancellationRequested();
            foreach (var group in PenumbraPoseScanner.TryReadAnimatedOptions(root, mod).GroupBy(option => option.Group))
            {
                var list = group.ToList();
                var words = NameGuess.UnifyGroup(group.Key, list.Select(option =>
                    NameGuess.Read(option.Option, option.Group, mod, CoupleNames.IsCouple(option.Group, option.Option))).ToList());
                for (var i = 0; i < list.Count; i++)
                {
                    var id = $"{OtherModPrefix}{mod}|{group.Key}|{list[i].Option}";
                    options.Add(new OtherModOption(id, DeclaredTags.FromDescription(list[i].Description), words[i]));
                    yield return new AnimationHashJob(id, System.IO.Path.Combine(root, mod), list[i].Files, AnimationHasher.NameKey(mod, group.Key, list[i].Option));
                }
            }
        }
    }

    /// Picks up a finished hashing run on the framework thread.
    public void Tick()
    {
        if (hashTask is not { IsCompleted: true } task) return;
        hashTask = null;

        if (task.IsFaulted)
        {
            Plugin.Log.Warning(task.Exception!, "[PoseKit] Couldn't read animation files; pairing tags use names and descriptions only.");
            return;
        }
        if (task.IsCanceled) return;

        var result = task.Result;
        var ids = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
            entry.AnimationKey = result.Keys.GetValueOrDefault(entry.Id);
        otherModEvidence = result.OtherOptions
            .Select(option => (result.Keys.GetValueOrDefault(option.Id), option.Declared, option.Words)).ToList();
        otherAlignedKeys = result.Keys.Where(pair => !ids.Contains(pair.Key) && !pair.Key.StartsWith(OtherModPrefix, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        Apply();
        store.ReplaceHashCache(result.Cache);
        Plugin.Log.Debug("[PoseKit] Pairing states: " + string.Join(", ",
            entries.GroupBy(e => e.Pairing).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
    }

    /// <param name="state">Null clears the player's tag.</param>
    public void SetPlayerTag(LibraryEntry entry, CouplePairing? state)
    {
        if (entry.AnimationKey is not { } key) return;
        store.Set(key, state, $"{entry.Mod.ModName} - {entry.Option.Name}");
        Apply();
    }

    private static string AlignedKey(string modDirectory, string group, string option) => $"{modDirectory}|{group}|{option}";

    private static string AlignedKeyOf(LibraryEntry entry) =>
        AlignedKey(entry.Mod.ModDirectory, entry.Group.IsImplicit ? "" : entry.Group.Name, entry.Option.Name);

    /// Every alignment by the option it was made on, and by animation identity where that's known,
    /// so identical animations share what was found on any of them.
    private (Dictionary<string, List<(BodyPart, BodyPart)>> ByOption, Dictionary<string, List<(BodyPart, BodyPart)>> ByAnimation) AlignedParts()
    {
        var byOption = new Dictionary<string, List<(BodyPart, BodyPart)>>(StringComparer.Ordinal);
        var byAnimation = new Dictionary<string, List<(BodyPart, BodyPart)>>(StringComparer.Ordinal);
        var keysByOption = entries.Where(entry => entry.AnimationKey != null)
            .GroupBy(AlignedKeyOf, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().AnimationKey!, StringComparer.Ordinal);

        static void Add(Dictionary<string, List<(BodyPart, BodyPart)>> map, string key, (BodyPart, BodyPart) parts)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(parts);
        }

        foreach (var alignment in alignments.Entries)
        {
            var optionKey = AlignedKey(alignment.ModDirectory, alignment.Group, alignment.Option);
            var parts = (alignment.Self, alignment.Partner);
            Add(byOption, optionKey, parts);
            if (keysByOption.TryGetValue(optionKey, out var animation) || otherAlignedKeys.TryGetValue(optionKey, out animation))
            {
                if (animation.StartsWith(AnimationHasher.FilesPrefix, StringComparison.Ordinal))
                    Add(byAnimation, animation, parts);
            }
        }
        return (byOption, byAnimation);
    }

    private void Apply()
    {
        var (byOption, byAnimation) = AlignedParts();
        var shared = DeclaredTags.SharedByFiles(entries.Select(entry => (entry.AnimationKey, entry.DeclaredState))
            .Concat(otherModEvidence.Select(other => (other.Key, other.Declared))));
        var sharedWords = NameGuess.SharedByFiles(entries.Select(entry => (entry.AnimationKey, entry.OwnWords))
            .Concat(otherModEvidence.Select(other => (other.Key, other.Words))));
        foreach (var entry in entries)
        {
            var key = entry.AnimationKey;
            var player = key != null ? store.TryGet(key) : null;
            var inherited = key != null && shared.TryGetValue(key, out var state) && state != CouplePairing.Unknown ? state : (CouplePairing?)null;

            var contact = key != null && byAnimation.TryGetValue(key, out var sharedParts)
                ? ContactEvidence.FromAlignments(sharedParts)
                : byOption.TryGetValue(AlignedKeyOf(entry), out var ownParts)
                    ? ContactEvidence.FromAlignments(ownParts)
                    : null;
            var words = key != null && sharedWords.TryGetValue(key, out var reading) ? reading : (WordReading?)null;
            entry.ApplySources(player, contact, inherited, words);
        }
        Version++;
    }
}
