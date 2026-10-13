using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoseKit.Library;

/// <summary>How a placed play is fitted to the seat after being put on its position and facing.</summary>
public enum SnapMode
{
    /// Move the animation's hips onto the seat's recorded hip point.
    Hips,

    /// Seat position and facing only.
    PositionOnly,
}

/// <summary>
/// The snap mode the player last chose for each animation, per context kind ("seated", later
/// "bed"). Stored in its own seat-snap.json so a bad file can't break the settings.
///
/// An unreadable file starts empty and is renamed to .bad on the next save. Saves go through a temp
/// file.
/// </summary>
public sealed class SeatSnapMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;
    private readonly Dictionary<string, SnapMode> modes = new(StringComparer.Ordinal);
    private bool loadFailed;

    public SeatSnapMemory(string configDirectory)
    {
        path = Path.Combine(configDirectory, "seat-snap.json");
        Load();
    }

    public static string KeyFor(string modDirectory, string group, string option, string trigger, string contextKind) =>
        $"{modDirectory}|{group}|{option}|{trigger}|{contextKind}";

    public SnapMode? TryGet(string key) => modes.TryGetValue(key, out var mode) ? mode : null;

    public void Record(string key, SnapMode mode)
    {
        if (modes.TryGetValue(key, out var existing) && existing == mode) return;
        modes[key] = mode;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var (key, mode) in JsonSerializer.Deserialize<Dictionary<string, SnapMode>>(text, JsonOptions) ?? [])
                modes[key] = mode;
        }
        catch (Exception ex)
        {
            loadFailed = true;
            modes.Clear();
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't read {path}; seat snap memory starts empty and the file is kept until the next save.");
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
            File.WriteAllText(temp, JsonSerializer.Serialize(modes, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't save seat snap memory to {path}.");
        }
    }
}
