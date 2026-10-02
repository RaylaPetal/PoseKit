using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
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

    /// The gap slider when recorded. Only used to place entries without a ContactOffset.
    public float Gap { get; set; }

    /// This player's drawn heading minus the partner's, in radians. Null when not known (an entry
    /// migrated from a version 1 part-direction or unchanged facing).
    public float? RelativeYaw { get; set; }

    /// The Self part minus the Partner part, in the partner's drawn frame. Null for version 1 entries.
    public StoredVector? ContactOffset { get; set; }

    /// Version 1 only; read for migration, never written.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlignFacing? Facing { get; set; }

    public DateTime Updated { get; set; }
}

/// <summary>A Vector3 as JSON properties; System.Numerics.Vector3 only has fields.</summary>
public sealed class StoredVector
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3 ToVector3() => new(X, Y, Z);

    public static StoredVector From(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}

/// <summary>How version 1 entries stored their facing. Only read for migration, so don't rename members.</summary>
public enum AlignFacing
{
    Auto,
    SameWay,
    Facing,
    QuarterLeft,
    QuarterRight,
    PartDirection,
    Unchanged,
}

/// <summary>
/// Remembers each successful manual Bone Align per animation, so later plays can align themselves.
/// Stored in its own alignments.json so a bad file can't break the settings.
///
/// An unreadable file starts empty and is renamed to .bad on the next save. Saves go through a temp
/// file. Version 1 entries stored a facing label; they're converted to a relative yaw on load and
/// written back as version 2 on the next save.
/// </summary>
public sealed class AlignmentMemory
{
    private const int FileVersion = 2;

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
                if (entry.Key.Length == 0) continue;
                Migrate(entry);
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

    /// Turns a version 1 facing label into a relative yaw. Labels with no fixed angle become null.
    private static void Migrate(AlignmentEntry entry)
    {
        if (entry.Facing is not { } facing) return;
        entry.RelativeYaw ??= facing switch
        {
            AlignFacing.SameWay => 0f,
            AlignFacing.Facing => MathF.PI,
            AlignFacing.QuarterLeft => MathF.PI / 2,
            AlignFacing.QuarterRight => -MathF.PI / 2,
            _ => null,
        };
        entry.Facing = null;
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
