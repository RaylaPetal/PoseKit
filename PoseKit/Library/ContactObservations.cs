using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoseKit.Bones;

namespace PoseKit.Library;

/// <summary>The closest contact measured on one animation, and the tag it suggests.</summary>
public sealed class ContactObservation
{
    public BodyPart A { get; set; }
    public BodyPart B { get; set; }
    public float Distance { get; set; }
    public CouplePairing Suggestion { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// Contact measured during paired plays, by animation identity. Stored in its own
/// contact-observations.json so a bad file can't break the settings; these stay suggestions until
/// the measurement has proved itself, so they never set a tag on their own.
///
/// An unreadable file starts empty and is renamed to .bad on the next save. Saves go through a temp
/// file.
/// </summary>
public sealed class ContactObservations
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;
    private readonly Dictionary<string, ContactObservation> observations = new(StringComparer.Ordinal);
    private bool loadFailed;

    public ContactObservations(string configDirectory)
    {
        path = Path.Combine(configDirectory, "contact-observations.json");
        Load();
    }

    public ContactObservation? TryGet(string animationKey) => observations.GetValueOrDefault(animationKey);

    /// Keeps the closest contact seen, since a later play may never line up as well.
    public void Record(string animationKey, ContactObservation observation)
    {
        if (observations.TryGetValue(animationKey, out var existing) && existing.Distance <= observation.Distance) return;
        observations[animationKey] = observation;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var (key, observation) in JsonSerializer.Deserialize<Dictionary<string, ContactObservation>>(text, JsonOptions) ?? [])
                observations[key] = observation;
        }
        catch (Exception ex)
        {
            loadFailed = true;
            observations.Clear();
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't read {path}; contact observations start empty and the file is kept until the next save.");
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
            File.WriteAllText(temp, JsonSerializer.Serialize(observations, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't save contact observations to {path}.");
        }
    }
}
