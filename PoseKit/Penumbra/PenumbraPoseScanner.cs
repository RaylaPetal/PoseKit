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

    /// True for the synthetic group PenumbraPoseScanner builds from default_mod.json's always-active
    /// files — needed because a mod with no group_*.json at all (just one fixed set of redirects,
    /// nothing configurable) would otherwise never be scanned. Not a real Penumbra group: there's
    /// nothing to select (only one implicit option), and it must never be sent in a
    /// TrySetTemporarySettings payload — Penumbra has no group by this name.
    public bool IsImplicit { get; init; }
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
/// Reads each mod's own on-disk meta.json directly — Penumbra's mod-meta file format (schema version
/// 4): a single JSON file per mod holding "DefaultData": {"Files": {gamePath: redirect}} for the
/// mod's always-active files, plus "Groups": [{"Type": "Single"|"Multi", "Name": ..., "Options":
/// [{"Name": ..., "Files": {gamePath: redirect}}]}] for its option groups — confirmed against an
/// actual installed mod (~/Documents/Penumbra/GoonersLife+v3[Gooners.inc]/meta.json). Older Penumbra
/// versions split this across a separate default_mod.json plus one group_*.json per group; Penumbra
/// migrates existing mods to the single meta.json in place (renaming the old files to .bak), so only
/// the current format needs reading. Reading these files directly rather than a raw recursive .pap
/// filesystem scan preserves each option's identity — a scan would collapse many distinct options
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

    private sealed record ModMetaDto(DefaultDataDto? DefaultData, List<GroupFileDto>? Groups);

    private sealed record DefaultDataDto(Dictionary<string, string>? Files);

    private sealed record GroupFileDto(string Type, string Name, List<OptionFileDto> Options);

    private sealed record OptionFileDto(string Name, Dictionary<string, string>? Files);

    public readonly record struct ModGroupInfo(string GroupName, List<string> OptionNames);

    /// Reads one specific mod's meta.json directly and returns every real group/option name pair it
    /// defines — independent of Configuration.SelectedPenumbraMods, unlike Scan(), since this is used
    /// to resolve a partner-received group/option hash back to real text for a mod that isn't
    /// necessarily one the local user has opted to browse (see hash-couple-relay-group-option's
    /// design). Returns null if the mod's meta.json can't be found or parsed.
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

            // Disabled mods are scanned too (not skipped) — PoseKit.PlayTrigger enables one temporarily
            // through Penumbra the moment it's played, so users don't have to flip it on there first.
            var (modEnabled, currentSelections) = ipc.TryGetCurrentSettings(cid, modDirectory);

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

            // DefaultData holds the mod's always-active files — the ones outside any optional group. A
            // mod with no configurable options at all (just one fixed redirect set, like "[Mittens]
            // Fist Full of Dreams") has only these, and zero Groups, so it needs its own scan rather
            // than being skipped for lack of groups.
            //
            // Some mods (e.g. "Kissing While Standing [groundsit1] [Mittens]") carry leftover
            // DefaultData entries whose keys aren't real game paths at all — e.g. "I am kissing
            // partner number_/1/chara/human/.../j_pose01_loop.pap" instead of a real path starting
            // with "chara/". Penumbra itself would never match these at runtime, but the regex-based
            // detection below doesn't care where in the string it matches, so it'd otherwise pick up a
            // bogus duplicate of a pose the mod's *real* group already covers properly — showing as a
            // false self-conflict and a "Default" button that only enables the mod without ever
            // selecting the real option.
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
                foreach (var opt in dto.Options)
                {
                    var fileKeys = (IEnumerable<string>?)opt.Files?.Keys ?? Array.Empty<string>();
                    var triggers = PoseNameHeuristics.Detect(dto.Name, opt.Name, fileKeys);
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
                Groups = groups,
            });
        }

        return results;
    }
}
