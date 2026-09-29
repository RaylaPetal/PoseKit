using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoseKit.Bones;

/// <summary>How Bone Align last succeeded on one animation.</summary>
public sealed class AlignmentEntry
{
    public string Key { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public string Group { get; set; } = "";
    public string Option { get; set; } = "";
    public string Trigger { get; set; } = "";
    public BodyPart Self { get; set; }
    public BodyPart Partner { get; set; }
    public float Gap { get; set; }
    public AlignFacing Facing { get; set; }
    public DateTime Updated { get; set; }
}

/// <summary>
/// Remembers each successful manual Bone Align per animation, so later plays can align themselves.
/// Stored in its own alignments.json so a bad file can't break the settings.
///
/// An unreadable file starts empty and is renamed to .bad on the next save. Saves go through a temp
/// file.
/// </summary>
public sealed class AlignmentMemory
{
    private const int FileVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class FileDto
    {
        public int Version { get; set; } = FileVersion;
        public List<AlignmentEntry> Entries { get; set; } = [];
    }

    private readonly string path;
    private readonly Dictionary<string, AlignmentEntry> entries = new(StringComparer.Ordinal);
    private bool loadFailed;

    public AlignmentMemory(string configDirectory)
    {
        path = Path.Combine(configDirectory, "alignments.json");
        Load();
    }

    public static string KeyFor(string modDirectory, string group, string option, string trigger) =>
        $"{modDirectory}|{group}|{option}|{trigger}";

    public AlignmentEntry? TryGet(string key) => entries.GetValueOrDefault(key);

    /// Adds or replaces the entry for its key and saves.
    public void Record(AlignmentEntry entry)
    {
        entries[entry.Key] = entry;
        Save();
    }

    /// Removes the entry for <paramref name="key"/>, if any, and saves.
    public void Forget(string key)
    {
        if (entries.Remove(key))
            Save();
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            var dto = JsonSerializer.Deserialize<FileDto>(text, JsonOptions);
            foreach (var entry in dto?.Entries ?? [])
            {
                if (entry.Key.Length > 0)
                    entries[entry.Key] = entry;
            }
        }
        catch (Exception ex)
        {
            loadFailed = true;
            entries.Clear();
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't read {path}; alignment memory starts empty and the file is kept until the next save.");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (loadFailed && File.Exists(path))
                File.Move(path, path + ".bad", overwrite: true);
            loadFailed = false;

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileDto { Entries = [.. entries.Values] }, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't save alignment memory to {path}.");
        }
    }
}
