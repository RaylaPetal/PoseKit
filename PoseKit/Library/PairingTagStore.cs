using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoseKit.Library;

/// <summary>A pairing the player set by hand; Label only makes the file readable.</summary>
public sealed class PlayerPairingTag
{
    public CouplePairing State { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// The player's pairing tags, by animation identity (see AnimationHasher), and the file hash cache.
/// Stored in its own pairing-tags.json so a bad file can't break the settings.
///
/// An unreadable file starts empty and is renamed to .bad on the next save. Saves go through a temp
/// file.
/// </summary>
public sealed class PairingTagStore
{
    private sealed class FileDto
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, PlayerPairingTag> Tags { get; set; } = new();
        public Dictionary<string, CachedFileHash> HashCache { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;
    private Dictionary<string, PlayerPairingTag> tags = new(StringComparer.Ordinal);

    public Dictionary<string, CachedFileHash> HashCache { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool LoadFailed { get; private set; }

    public PairingTagStore(string configDirectory)
    {
        path = Path.Combine(configDirectory, "pairing-tags.json");
        Load();
    }

    public CouplePairing? TryGet(string key) => tags.TryGetValue(key, out var tag) ? tag.State : null;

    /// <param name="state">Null clears the player's tag.</param>
    public void Set(string key, CouplePairing? state, string label)
    {
        if (state is { } value)
            tags[key] = new PlayerPairingTag { State = value, Label = label };
        else
            tags.Remove(key);
        Save();
    }

    public void ReplaceHashCache(Dictionary<string, CachedFileHash> cache)
    {
        HashCache = cache;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;

            var dto = JsonSerializer.Deserialize<FileDto>(text, JsonOptions) ?? new FileDto();
            tags = new Dictionary<string, PlayerPairingTag>(dto.Tags, StringComparer.Ordinal);
            HashCache = new Dictionary<string, CachedFileHash>(dto.HashCache, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            tags = new Dictionary<string, PlayerPairingTag>(StringComparer.Ordinal);
            HashCache = new Dictionary<string, CachedFileHash>(StringComparer.OrdinalIgnoreCase);
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't read {path}; pairing tags start empty and the file is kept until the next save.");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (LoadFailed && File.Exists(path))
                File.Move(path, path + ".bad", overwrite: true);
            LoadFailed = false;

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileDto { Tags = tags, HashCache = HashCache }, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't save pairing tags to {path}.");
        }
    }
}
