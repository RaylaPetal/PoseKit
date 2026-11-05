using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PoseKit.Library;

/// <summary>A file hash, reused while the file's size and modified time don't change.</summary>
public sealed class CachedFileHash
{
    public long Size { get; set; }
    public long Modified { get; set; }
    public string Hash { get; set; } = "";
}

/// <param name="Id">The caller's name for this option.</param>
/// <param name="Files">The option's animation files, relative to the mod folder as meta.json has them.</param>
/// <param name="NameKey">The identity to use when no animation file can be read.</param>
public sealed record AnimationHashJob(string Id, string ModRoot, IReadOnlyList<string> Files, string NameKey);

/// <summary>
/// Names each option's animation by its file contents, so a player's tag survives renumbering and
/// mod updates, and carries to the same animation in another pack. Runs off the framework thread;
/// nothing here may touch game state.
/// </summary>
public static class AnimationHasher
{
    public const string FilesPrefix = "files:";

    public static string NameKey(string modDirectory, string group, string option) => $"name:{modDirectory}|{group}|{option}";

    /// <param name="cache">Read for reuse and replaced with the entries this run used.</param>
    /// <returns>Each job's identity, by id.</returns>
    public static Dictionary<string, string> Compute(IReadOnlyList<AnimationHashJob> jobs, Dictionary<string, CachedFileHash> cache,
        CancellationToken cancellation = default)
    {
        var keys = new Dictionary<string, string>(jobs.Count, StringComparer.Ordinal);
        var used = new Dictionary<string, CachedFileHash>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in jobs)
        {
            cancellation.ThrowIfCancellationRequested();

            if (!folders.TryGetValue(job.ModRoot, out var folder))
                folders[job.ModRoot] = folder = IndexFolder(job.ModRoot);

            var hashes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in job.Files)
            {
                if (folder.TryGetValue(Normalize(file), out var fullPath) && HashFile(fullPath, cache, used) is { } hash)
                    hashes.Add(hash);
            }

            keys[job.Id] = hashes.Count == 0
                ? job.NameKey
                : FilesPrefix + Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(string.Join('\n', hashes))));
        }

        cache.Clear();
        foreach (var (path, entry) in used)
            cache[path] = entry;
        return keys;
    }

    /// Mods are authored on Windows, so meta.json's paths can differ in case from the files on disk.
    /// Looking files up in a listing also keeps a path in meta.json from leaving the mod's folder.
    private static Dictionary<string, string> IndexFolder(string root)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return index;

        foreach (var path in Directory.EnumerateFiles(root, "*.pap", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }))
            index[Normalize(Path.GetRelativePath(root, path))] = path;
        return index;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private static string? HashFile(string path, Dictionary<string, CachedFileHash> cache, Dictionary<string, CachedFileHash> used)
    {
        if (used.TryGetValue(path, out var seen)) return seen.Hash;

        try
        {
            var info = new FileInfo(path);
            var modified = info.LastWriteTimeUtc.Ticks;
            if (!cache.TryGetValue(path, out var entry) || entry.Size != info.Length || entry.Modified != modified)
            {
                using var stream = File.OpenRead(path);
                entry = new CachedFileHash { Size = info.Length, Modified = modified, Hash = Convert.ToHexString(SHA1.HashData(stream)) };
            }
            used[path] = entry;
            return entry.Hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
