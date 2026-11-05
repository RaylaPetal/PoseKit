using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PoseKit.Pairing;

namespace PoseKit.Library;

/// <summary>
/// What was last played with each partner, and alone. Remembered per partner because what suits one
/// couple says little about another. Stored in its own recents.json so a bad file can't break the
/// settings.
///
/// An unreadable file starts empty and is renamed to .bad on the next save. Saves go through a temp
/// file.
/// </summary>
public sealed class RecentPlays
{
    private const int MaxPerList = 12;
    private const string SoloList = "solo";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly IReadOnlyList<string> Empty = [];

    private readonly string path;
    private readonly Dictionary<string, List<string>> lists = new(StringComparer.Ordinal);
    private bool loadFailed;

    public RecentPlays(string configDirectory)
    {
        path = Path.Combine(configDirectory, "recents.json");
        Load();
    }

    /// <param name="partner">Null records and reads the solo list.</param>
    public IReadOnlyList<string> Get(PartnerIdentity? partner) =>
        lists.TryGetValue(ListKey(partner), out var list) ? list : Empty;

    /// <param name="entryId">The option's LibraryEntry.Id.</param>
    public void Record(PartnerIdentity? partner, string entryId)
    {
        var key = ListKey(partner);
        if (!lists.TryGetValue(key, out var list))
            lists[key] = list = [];
        else if (list.Count > 0 && list[0] == entryId)
            return;

        list.Remove(entryId);
        list.Insert(0, entryId);
        if (list.Count > MaxPerList)
            list.RemoveRange(MaxPerList, list.Count - MaxPerList);
        Save();
    }

    private static string ListKey(PartnerIdentity? partner) => partner?.TellAddress ?? SoloList;

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var (key, list) in JsonSerializer.Deserialize<Dictionary<string, List<string>>>(text, JsonOptions) ?? [])
                lists[key] = list;
        }
        catch (Exception ex)
        {
            loadFailed = true;
            lists.Clear();
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't read {path}; recent animations start empty and the file is kept until the next save.");
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
            File.WriteAllText(temp, JsonSerializer.Serialize(lists, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't save recent animations to {path}.");
        }
    }
}
