using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PoseKit.Penumbra;

public sealed class PoseModOption
{
    public required string Name { get; init; }
    public required List<PoseTriggerHint> Triggers { get; init; }
}

public sealed class PoseModGroup
{
    public required string Name { get; init; }
    public required bool MultiSelect { get; init; }
    public required List<PoseModOption> Options { get; init; }
    public HashSet<string> Selected { get; set; } = new();
}

public sealed class PoseModInfo
{
    public required string ModDirectory { get; init; }
    public required string ModName { get; init; }
    public bool Enabled { get; set; }
    public required List<PoseModGroup> Groups { get; init; }
}

/// <summary>
/// Discovers poses from the mods the user has explicitly picked in Settings (Configuration.
/// SelectedPenumbraMods) — scanning every installed mod was slow and mostly irrelevant noise on a
/// large modlist, so this is opt-in per mod. Only currently-enabled mods are scanned even if selected.
///
/// Reads each mod's own on-disk group_*.json files directly — this is Penumbra's real settings-page
/// schema ({"Type": "Single"|"Multi", "Name": ..., "Options": [{"Name": ..., "Files":
/// {gamePath: redirect}}]}), confirmed against an actual installed mod
/// (~/Documents/Penumbra/GoonersLife+v3[Gooners.inc]/group_*.json) — rather than a raw recursive
/// .pap filesystem scan, which lost every option's identity by collapsing many distinct options
/// that happen to redirect the same handful of game pose slots into duplicate generic buttons.
/// </summary>
public sealed class PenumbraPoseScanner(PenumbraIpc ipc, Configuration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record GroupFileDto(string Type, string Name, List<OptionFileDto> Options);

    private sealed record OptionFileDto(string Name, Dictionary<string, string>? Files);

    public List<PoseModInfo> Scan()
    {
        var results = new List<PoseModInfo>();
        if (configuration.SelectedPenumbraMods.Count == 0) return results;

        var modList = ipc.TryGetModList();
        var modRoot = ipc.TryGetModDirectory();
        var collectionId = ipc.TryGetLocalPlayerCollectionId();
        if (modList == null || modRoot == null || collectionId is not { } cid) return results;

        foreach (var modDirectory in configuration.SelectedPenumbraMods)
        {
            if (!modList.TryGetValue(modDirectory, out var modName)) continue; // mod no longer installed

            var (modEnabled, currentSelections) = ipc.TryGetCurrentSettings(cid, modDirectory);
            if (!modEnabled) continue;

            var modPath = Path.Combine(modRoot, modDirectory);
            if (!Directory.Exists(modPath)) continue;

            var groupFiles = Directory.GetFiles(modPath, "group_*.json", SearchOption.TopDirectoryOnly);
            if (groupFiles.Length == 0) continue;

            var groups = new List<PoseModGroup>();
            foreach (var groupFile in groupFiles)
            {
                GroupFileDto? dto;
                try { dto = JsonSerializer.Deserialize<GroupFileDto>(File.ReadAllText(groupFile), JsonOptions); }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, $"[PoseKit] Failed to parse {groupFile}, skipping this group.");
                    continue;
                }
                if (dto?.Options == null) continue;

                var options = new List<PoseModOption>();
                foreach (var opt in dto.Options)
                {
                    var triggers = PoseNameHeuristics.Detect(dto.Name, opt.Name, (IEnumerable<string>?)opt.Files?.Keys ?? Array.Empty<string>());
                    options.Add(new PoseModOption { Name = opt.Name, Triggers = triggers });
                }

                var selected = currentSelections != null && currentSelections.TryGetValue(dto.Name, out var sel)
                    ? new HashSet<string>(sel)
                    : new HashSet<string>();

                groups.Add(new PoseModGroup
                {
                    Name = dto.Name,
                    MultiSelect = string.Equals(dto.Type, "Multi", StringComparison.OrdinalIgnoreCase),
                    Options = options,
                    Selected = selected,
                });
            }

            if (groups.Count == 0) continue;

            results.Add(new PoseModInfo
            {
                ModDirectory = modDirectory,
                ModName = modName,
                Enabled = modEnabled,
                Groups = groups,
            });
        }

        return results;
    }
}
