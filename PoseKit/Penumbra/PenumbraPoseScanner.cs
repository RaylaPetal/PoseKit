using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// The synthetic group for a mod's always-active files. Not a real Penumbra group, so it must
    /// never be sent to Penumbra.
    public bool IsImplicit { get; init; }
}

public sealed class PoseModInfo
{
    public required string ModDirectory { get; init; }
    public required string ModName { get; init; }
    public bool Enabled { get; set; }

    /// Passed back into TrySetTemporarySettings. See PenumbraIpc.TryGetCurrentSettings.
    public int Priority { get; set; }

    public required List<PoseModGroup> Groups { get; init; }
}

/// <summary>
/// Discovers poses in the mods picked in Settings by reading each mod's meta.json, which keeps each
/// option's identity (a raw .pap scan would merge options that replace the same poses).
/// </summary>
public sealed class PenumbraPoseScanner(PenumbraIpc ipc, Configuration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record ModMetaDto(DefaultDataDto? DefaultData, List<GroupFileDto>? Groups);

    private sealed record DefaultDataDto(Dictionary<string, string>? Files);

    private sealed record GroupFileDto(string Type, string Name, List<OptionFileDto> Options, List<ContainerDto>? Containers);

    private sealed record OptionFileDto(string Name, Dictionary<string, string>? Files);

    /// One per option combination in a Combining group; bit i of the index means option i is on.
    private sealed record ContainerDto(Dictionary<string, string>? Files);

    /// An option's game paths. Combining groups keep files in containers instead of on options,
    /// so an option gets every container its bit is set in.
    private static IEnumerable<string> OptionFileKeys(GroupFileDto group, int optionIndex)
    {
        if (group.Containers is not { } containers)
            return group.Options[optionIndex].Files?.Keys ?? Enumerable.Empty<string>();

        return containers
            .Where((_, index) => (index & (1 << optionIndex)) != 0)
            .SelectMany(container => container.Files?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// The game paths active for these selected options.
    private static IEnumerable<string> SelectedFileKeys(GroupFileDto group, IReadOnlyCollection<string> selected)
    {
        if (group.Containers is not { } containers)
        {
            return group.Options
                .Where(opt => selected.Contains(opt.Name))
                .SelectMany(opt => opt.Files?.Keys ?? Enumerable.Empty<string>());
        }

        var mask = 0;
        for (var i = 0; i < group.Options.Count; i++)
            if (selected.Contains(group.Options[i].Name)) mask |= 1 << i;
        return mask < containers.Count ? containers[mask].Files?.Keys ?? Enumerable.Empty<string>() : [];
    }

    public readonly record struct ModGroupInfo(string GroupName, List<string> OptionNames);

    /// A pose claimed by a mod that isn't selected for scanning, for conflict warnings.
    public readonly record struct ExternalPoseClaim(string ModDirectory, string ModName, string Label);

    /// Every group/option name in any mod, used to resolve a partner's hashes. Null if unreadable.
    public static List<ModGroupInfo>? TryReadGroups(string modRoot, string modDirectory)
    {
        var metaPath = Path.Combine(modRoot, modDirectory, "meta.json");
        if (!File.Exists(metaPath)) return null;

        ModMetaDto? meta;
        try { meta = JsonSerializer.Deserialize<ModMetaDto>(File.ReadAllText(metaPath), JsonOptions); }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Failed to parse {metaPath}.");
            return null;
        }

        return (meta?.Groups ?? [])
            .Where(dto => dto.Options != null)
            .Select(dto => new ModGroupInfo(dto.Name, dto.Options.Select(o => o.Name).ToList()))
            .ToList();
    }

    public List<PoseModInfo> Scan()
    {
        var results = new List<PoseModInfo>();
        if (configuration.SelectedPenumbraMods.Count == 0) return results;

        var modList = ipc.TryGetModList();
        var modRoot = ipc.TryGetModDirectory();
        var collectionId = ipc.TryGetLocalPlayerCollectionId();
        if (modList == null || modRoot == null || collectionId is not { } cid)
        {
            Plugin.Log.Warning($"[PoseKit] Scan aborted: modList={(modList == null ? "null" : $"{modList.Count} entries")}, modRoot={modRoot ?? "null"}, collectionId={collectionId?.ToString() ?? "null"}.");
            return results;
        }

        foreach (var modDirectory in configuration.SelectedPenumbraMods)
        {
            if (!modList.TryGetValue(modDirectory, out var modName))
            {
                Plugin.Log.Warning($"[PoseKit] Selected mod directory \"{modDirectory}\" not found in Penumbra's mod list ({modList.Count} known); skipping.");
                continue;
            }

            // Disabled mods are scanned too; playing one enables it temporarily.
            var (modEnabled, modPriority, currentSelections) = ipc.TryGetCurrentSettings(cid, modDirectory);

            var modPath = Path.Combine(modRoot, modDirectory);
            if (!Directory.Exists(modPath))
            {
                Plugin.Log.Warning($"[PoseKit] Mod path \"{modPath}\" (root \"{modRoot}\", directory \"{modDirectory}\") does not exist on disk; skipping.");
                continue;
            }

            var groups = new List<PoseModGroup>();

            var metaPath = Path.Combine(modPath, "meta.json");
            ModMetaDto? meta = null;
            if (File.Exists(metaPath))
            {
                try { meta = JsonSerializer.Deserialize<ModMetaDto>(File.ReadAllText(metaPath), JsonOptions); }
                catch (Exception ex) { Plugin.Log.Warning(ex, $"[PoseKit] Failed to parse {metaPath}, skipping mod."); }
            }
            else
            {
                Plugin.Log.Warning($"[PoseKit] {metaPath} not found; skipping mod.");
            }

            // DefaultData holds the always-active files; some mods have nothing else. Keys that
            // aren't real game paths (leftovers in some mods) are skipped so they don't show up as
            // bogus duplicate poses.
            var defaultFileKeys = (meta?.DefaultData?.Files?.Keys ?? Enumerable.Empty<string>())
                .Where(key => key.StartsWith("chara/", StringComparison.OrdinalIgnoreCase));

            var defaultTriggers = PoseNameHeuristics.Detect(modName, modName, defaultFileKeys);
            if (defaultTriggers.Count > 0)
            {
                groups.Add(new PoseModGroup
                {
                    Name = "Default",
                    MultiSelect = false,
                    IsImplicit = true,
                    Options = [new PoseModOption { Name = "Default", Triggers = defaultTriggers }],
                    Selected = new HashSet<string> { "Default" },
                });
            }

            foreach (var dto in meta?.Groups ?? [])
            {
                if (dto.Options == null) continue;

                var options = new List<PoseModOption>();
                for (var i = 0; i < dto.Options.Count; i++)
                {
                    var opt = dto.Options[i];
                    var triggers = PoseNameHeuristics.Detect(dto.Name, opt.Name, OptionFileKeys(dto, i));
                    options.Add(new PoseModOption { Name = opt.Name, Triggers = triggers });
                }

                groups.Add(new PoseModGroup
                {
                    Name = dto.Name,
                    // Combining groups toggle each option independently, like Multi.
                    MultiSelect = string.Equals(dto.Type, "Multi", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(dto.Type, "Combining", StringComparison.OrdinalIgnoreCase),
                    Options = options,
                    Selected = SelectionFor(currentSelections, dto.Name),
                });
            }

            if (groups.Count == 0)
            {
                Plugin.Log.Warning($"[PoseKit] Mod \"{modName}\" ({modDirectory}) has no default-data triggers and no group options with recognized options; skipping.");
                continue;
            }

            results.Add(new PoseModInfo
            {
                ModDirectory = modDirectory,
                ModName = modName,
                Enabled = modEnabled,
                Priority = modPriority,
                Groups = groups,
            });
        }

        return results;
    }

    /// A group's selection from Penumbra's settings; a group Penumbra didn't report has none.
    private static HashSet<string> SelectionFor(Dictionary<string, List<string>>? selections, string groupName) =>
        selections != null && selections.TryGetValue(groupName, out var selected) ? new HashSet<string>(selected) : [];

    /// Re-reads one scanned mod's current Penumbra settings into it, without re-reading its files.
    public void RefreshSettings(PoseModInfo mod)
    {
        if (ipc.TryGetLocalPlayerCollectionId() is not { } cid) return;

        var (enabled, priority, selections) = ipc.TryGetCurrentSettings(cid, mod.ModDirectory);
        mod.Enabled = enabled;
        mod.Priority = priority;
        foreach (var group in mod.Groups)
        {
            if (!group.IsImplicit)
                group.Selected = SelectionFor(selections, group.Name);
        }
    }

    /// Checks the selected options of every other enabled mod for poses that conflict with the
    /// scanned ones.
    public Dictionary<PoseIdentifier, List<ExternalPoseClaim>> ScanExternalConflicts()
    {
        var claims = new Dictionary<PoseIdentifier, List<ExternalPoseClaim>>();

        var modList = ipc.TryGetModList();
        var modRoot = ipc.TryGetModDirectory();
        var collectionId = ipc.TryGetLocalPlayerCollectionId();
        if (modList == null || modRoot == null || collectionId is not { } cid) return claims;

        var allSettings = ipc.TryGetAllSettings(cid);
        if (allSettings == null) return claims;

        void RecordClaim(string modDirectory, string modName, string groupName, string optionName, IEnumerable<string> fileKeys)
        {
            var label = groupName.Length > 0 && !string.Equals(groupName, optionName, StringComparison.Ordinal)
                ? $"{groupName}: {optionName}" : optionName;
            foreach (var trigger in PoseNameHeuristics.Detect(groupName, optionName, fileKeys))
            {
                if (trigger.PoseIdentifier is not { } pid) continue;
                if (!claims.TryGetValue(pid, out var list))
                    claims[pid] = list = [];
                list.Add(new ExternalPoseClaim(modDirectory, modName, label));
            }
        }

        foreach (var (modDirectory, modName) in modList)
        {
            if (configuration.SelectedPenumbraMods.Contains(modDirectory)) continue;
            if (!allSettings.TryGetValue(modDirectory, out var settings) || !settings.Enabled) continue;

            var metaPath = Path.Combine(modRoot, modDirectory, "meta.json");
            if (!File.Exists(metaPath)) continue;

            ModMetaDto? meta;
            try { meta = JsonSerializer.Deserialize<ModMetaDto>(File.ReadAllText(metaPath), JsonOptions); }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"[PoseKit] ScanExternalConflicts: failed to parse {metaPath}, skipping.");
                continue;
            }

            var defaultFileKeys = (meta?.DefaultData?.Files?.Keys ?? Enumerable.Empty<string>())
                .Where(key => key.StartsWith("chara/", StringComparison.OrdinalIgnoreCase));
            RecordClaim(modDirectory, modName, modName, modName, defaultFileKeys);

            foreach (var dto in meta?.Groups ?? [])
            {
                if (dto.Options == null) continue;
                if (!settings.Selections.TryGetValue(dto.Name, out var selectedOptions)) continue;

                // Limited to the active files, since a Combining option spans containers that
                // aren't all in use.
                var activeKeys = new HashSet<string>(SelectedFileKeys(dto, selectedOptions), StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < dto.Options.Count; i++)
                {
                    var opt = dto.Options[i];
                    if (!selectedOptions.Contains(opt.Name)) continue;
                    RecordClaim(modDirectory, modName, dto.Name, opt.Name, OptionFileKeys(dto, i).Where(activeKeys.Contains));
                }
            }
        }

        return claims;
    }
}
