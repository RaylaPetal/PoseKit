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

    /// Unfiltered snapshot of every installed mod (one Penumbra IPC round-trip per mod, the expensive
    /// part) — the folder dropdown and the mod picker both derive from this in-memory list, rather
    /// than re-scanning Penumbra every time the folder filter or search text changes.
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

    /// Penumbra's collection/mod-settings IPC isn't reliably ready right after login (or right after
    /// the game finishes loading), so a cache built on the window's first-ever draw can permanently
    /// miss mods that hadn't synced yet. Dropping the cache each time the window opens means it
    /// re-scans against Penumbra's current state instead of a stale snapshot, without needing a
    /// manual "Refresh mods" click.
    public override void OnOpen()
    {
        allModsCache = null;
        availableFoldersCache = null;
    }

    public override void Draw()
    {
        using var theme = PoseKitUi.PushTheme();

        // The window can be shrunk below what its content needs (down to the 400x250 minimum), so
        // everything below is wrapped in its own scroll region rather than relying on the outer
        // window to somehow fit it all — same pattern MainWindow uses per tab.
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
            var folderLabel = folderFilter.Length == 0 ? "(All mods)" : folderFilter;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("Sort folder##PoseKitFolderFilter", folderLabel))
            {
                if (ImGui.Selectable("(All mods)", folderFilter.Length == 0))
                    SetFolderFilter("");

                foreach (var folder in availableFoldersCache ?? [])
                {
                    if (ImGui.Selectable(folder, folder == folderFilter))
                        SetFolderFilter(folder);
                }

                ImGui.EndCombo();
            }
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

    /// Just saves the config — the mod picker below re-filters itself from allModsCache in memory
    /// every frame, and PenumbraPoseScanner scans by SelectedPenumbraMods, not this filter, so neither
    /// needs an explicit refresh here.
    private void SetFolderFilter(string folder)
    {
        configuration.PenumbraFolderFilter = folder;
        configuration.Save();
    }

    /// One IPC round-trip per installed mod to build an unfiltered snapshot of every mod's sort-folder
    /// path and enabled state, plus the set of distinct folders the dropdown offers — a mod filed
    /// under "Animations/Idles/Foo" contributes both "Animations" and "Animations/Idles" as selectable
    /// folders, since the folder filter matches anything nested under the selected path.
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
            if (!string.IsNullOrEmpty(sortPath))
            {
                var segments = sortPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 1; i < segments.Length; i++)
                    folders.Add(string.Join('/', segments[..i]));
            }

            var (enabled, _) = plugin.PenumbraIpc.TryGetCurrentSettings(cid, directory);
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
