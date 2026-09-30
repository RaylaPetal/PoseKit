using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace PoseKit.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    /// Every installed mod, cached since building it costs one IPC call per mod.
    private List<(string Directory, string Name, string? SortPath, bool Enabled)>? allModsCache;
    private List<string>? availableFoldersCache;
    private string modSearch = "";

    public ConfigWindow(Plugin plugin) : base($"PoseKit Settings v{Plugin.Version}###PoseKitConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 250),
            MaximumSize = new Vector2(900, 900),
        };
        Size = new Vector2(450, 450);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    /// Penumbra may not be ready right after login, so rebuild the cache on every open.
    public override void OnOpen() => InvalidateModCache();

    /// Rebuilds the mod list on the next draw, e.g. after Penumbra deleted or moved a mod.
    public void InvalidateModCache()
    {
        allModsCache = null;
        availableFoldersCache = null;
    }

    public override void Draw()
    {
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        if (ImGui.BeginChild("##PoseKitSettingsScroll", Vector2.Zero, false, ImGuiWindowFlags.None))
        {
            PoseKitUi.DrawDependencyStatus(plugin);

            PoseKitUi.SectionHeader("Sync");
            var heelsLoaded = plugin.SimpleHeelsBridge.IsLoaded;
            var bridgeToHeels = configuration.BridgeOffsetToSimpleHeels;
            using (ImRaii.Disabled(!heelsLoaded))
            {
                if (ImGui.Checkbox("Bridge offset to SimpleHeels", ref bridgeToHeels))
                {
                    configuration.BridgeOffsetToSimpleHeels = bridgeToHeels;
                    configuration.Save();
                }
            }
            PoseKitUi.TextWrappedDisabled(heelsLoaded
                ? "SimpleHeels detected."
                : "SimpleHeels not detected — install and enable it to sync your pose offset to nearby players.");

            PoseKitUi.SectionHeader("Penumbra Mods to Scan for Poses");
            PoseKitUi.TextWrappedDisabled("Disabled mods are listed too — PoseKit enables one temporarily when you play a pose from it.");

            if (allModsCache == null)
                RefreshAllMods();

            var folderFilter = configuration.PenumbraFolderFilter;
            PoseKitUi.DrawFolderFilterCombo("Sort folder##PoseKitFolderFilter", configuration, availableFoldersCache);
            PoseKitUi.TextWrappedDisabled("Filters by Penumbra's own mod-organization folder (Mods tab), not the disk folder.");

            if (ImGui.Button("Refresh mods##PoseKitRefreshMods"))
                RefreshAllMods();

            if (allModsCache == null)
            {
                ImGui.TextDisabled("Penumbra not found.");
            }
            else
            {
                var filteredMods = allModsCache
                    .Where(mod => MatchesFolderFilter(mod.SortPath, folderFilter))
                    .ToList();

                ImGui.SameLine();
                var selectedCount = filteredMods.Count(mod => configuration.SelectedPenumbraMods.Contains(mod.Directory));
                ImGui.TextColored(PoseKitUi.Accent,
                    $"{selectedCount} selected / {filteredMods.Count} found");

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##PoseKitModSearch", "Search mods...", ref modSearch, 128);

                var visibleMods = filteredMods
                    .Where(mod => MatchesModSearch(mod, modSearch))
                    .OrderByDescending(mod => configuration.SelectedPenumbraMods.Contains(mod.Directory))
                    .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var changed = false;
                if (ImGui.SmallButton("Select visible##PoseKitSelectVisibleMods"))
                    foreach (var mod in visibleMods)
                        changed |= configuration.SelectedPenumbraMods.Add(mod.Directory);

                ImGui.SameLine();
                if (ImGui.SmallButton("Clear visible##PoseKitClearVisibleMods"))
                    foreach (var mod in visibleMods)
                        changed |= configuration.SelectedPenumbraMods.Remove(mod.Directory);

                ImGui.SameLine();
                ImGui.TextDisabled($"{visibleMods.Count} shown");

                if (ImGui.BeginChild("##PoseKitModPicker", new Vector2(0, 210), true, ImGuiWindowFlags.None))
                {
                    foreach (var (modDirectory, modName, _, modEnabled) in visibleMods)
                    {
                        var isSelected = configuration.SelectedPenumbraMods.Contains(modDirectory);
                        if (ImGui.Checkbox($"{modName}##PoseKitModPick{modDirectory.GetHashCode()}", ref isSelected))
                        {
                            if (isSelected) configuration.SelectedPenumbraMods.Add(modDirectory);
                            else configuration.SelectedPenumbraMods.Remove(modDirectory);
                            changed = true;
                        }

                        if (!modEnabled)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled("(disabled in Penumbra)");
                        }

                        if (!string.Equals(modName, modDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            ImGui.Indent();
                            ImGui.TextDisabled(modDirectory);
                            ImGui.Unindent();
                        }
                    }
                }
                ImGui.EndChild();

                changed |= DrawMissingMods();

                if (changed)
                {
                    configuration.Save();
                    plugin.RefreshPenumbraPoses();
                }
            }

            PoseKitUi.SectionHeader("Support");
            ImGui.TextUnformatted("Discord: raylapetal");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy##PoseKitCopyDiscord"))
                ImGui.SetClipboardText("raylapetal");

            if (ImGui.Button("Report an issue on GitHub##PoseKitGitHubIssues"))
                Util.OpenLink("https://github.com/RaylaPetal/PoseKit/issues");
            PoseKitUi.TextWrappedDisabled("Questions and bug reports are welcome.");
        }
        ImGui.EndChild();
    }

    /// Selected mods Penumbra no longer has. The picker can't show them, so they get their own list
    /// to remove them from. Only called with the mod list loaded, so nothing is flagged while
    /// Penumbra is unavailable. Returns whether the selection changed.
    private bool DrawMissingMods()
    {
        var known = new HashSet<string>(allModsCache!.Select(mod => mod.Directory));
        var missing = configuration.SelectedPenumbraMods
            .Where(directory => !known.Contains(directory))
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0) return false;

        if (!ImGui.CollapsingHeader($"Missing mods ({missing.Count})###PoseKitMissingMods")) return false;
        PoseKitUi.TextWrappedDisabled("Selected, but no longer in Penumbra (deleted or renamed).");

        var changed = false;
        if (ImGui.SmallButton("Remove all missing##PoseKitRemoveAllMissing"))
        {
            foreach (var directory in missing)
                changed |= configuration.SelectedPenumbraMods.Remove(directory);
            return changed;
        }

        foreach (var directory in missing)
        {
            if (ImGui.SmallButton($"Remove##PoseKitRemoveMissing{directory.GetHashCode()}"))
                changed |= configuration.SelectedPenumbraMods.Remove(directory);
            ImGui.SameLine();
            ImGui.TextColored(PoseKitUi.Bad, directory);
        }

        return changed;
    }

    private void RefreshAllMods()
    {
        var modList = plugin.PenumbraIpc.TryGetModList();
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        if (modList == null || collectionId is not { } cid)
        {
            allModsCache = null;
            availableFoldersCache = null;
            return;
        }

        var mods = new List<(string, string, string?, bool)>();
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, name) in modList)
        {
            var sortPath = plugin.PenumbraIpc.TryGetModPath(directory, name);
            PoseKitUi.AddSortFolders(sortPath, folders);

            var (enabled, _, _) = plugin.PenumbraIpc.TryGetCurrentSettings(cid, directory);
            mods.Add((directory, name, sortPath, enabled));
        }

        allModsCache = mods;
        availableFoldersCache = [.. folders];
    }

    private static bool MatchesFolderFilter(string? sortPath, string filter)
    {
        filter = filter.Trim().Trim('/');
        if (filter.Length == 0) return true;
        if (sortPath == null) return false;

        return sortPath.Equals(filter, StringComparison.OrdinalIgnoreCase)
            || sortPath.StartsWith(filter + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesModSearch((string Directory, string Name, string? SortPath, bool Enabled) mod, string search)
        => string.IsNullOrWhiteSpace(search)
           || mod.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
           || mod.Directory.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
