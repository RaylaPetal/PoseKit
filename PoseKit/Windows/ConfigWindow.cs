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
    private List<(string Directory, string Name, bool Enabled)>? availableModsCache;
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
        availableModsCache = null;
    }

    public override void Draw()
    {
        using var theme = PoseKitUi.PushTheme();

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

        var folderFilter = configuration.PenumbraFolderFilter;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("Sort folder##PoseKitFolderFilter", "e.g. Animations (blank = all mods)", ref folderFilter, 128))
        {
            configuration.PenumbraFolderFilter = folderFilter;
            configuration.Save();
            RefreshAvailableMods();
        }
        PoseKitUi.TextWrappedDisabled("Filters by Penumbra's own mod-organization folder (Mods tab), not the disk folder.");

        if (ImGui.Button("Refresh mods##PoseKitRefreshMods"))
            RefreshAvailableMods();

        availableModsCache ??= RefreshAvailableMods();

        if (availableModsCache == null)
        {
            ImGui.TextDisabled("Penumbra not found.");
        }
        else
        {
            ImGui.SameLine();
            var selectedCount = availableModsCache.Count(mod =>
                configuration.SelectedPenumbraMods.Contains(mod.Directory));
            ImGui.TextColored(PoseKitUi.Accent,
                $"{selectedCount} selected / {availableModsCache.Count} found");

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##PoseKitModSearch", "Search mods...", ref modSearch, 128);

            var visibleMods = availableModsCache
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
                foreach (var (modDirectory, modName, modEnabled) in visibleMods)
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

    /// Lists every mod in the folder filter regardless of enabled state (not just currently-enabled
    /// ones) — disabled mods can still be picked here, and PoseKit temporarily enables one through
    /// Penumbra the moment you play a pose from it, so you don't have to go flip it on there first.
    private List<(string, string, bool)>? RefreshAvailableMods()
    {
        var modList = plugin.PenumbraIpc.TryGetModList();
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        if (modList == null || collectionId is not { } cid)
        {
            availableModsCache = null;
            return null;
        }

        var list = new List<(string, string, bool)>();
        foreach (var (directory, name) in modList)
        {
            var sortPath = plugin.PenumbraIpc.TryGetModPath(directory, name);
            if (!MatchesFolderFilter(sortPath, configuration.PenumbraFolderFilter)) continue;

            var (enabled, _) = plugin.PenumbraIpc.TryGetCurrentSettings(cid, directory);
            list.Add((directory, name, enabled));
        }

        availableModsCache = list;
        return list;
    }

    private static bool MatchesFolderFilter(string? sortPath, string filter)
    {
        filter = filter.Trim().Trim('/');
        if (filter.Length == 0) return true;
        if (sortPath == null) return false;

        return sortPath.Equals(filter, StringComparison.OrdinalIgnoreCase)
            || sortPath.StartsWith(filter + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesModSearch((string Directory, string Name, bool Enabled) mod, string search)
        => string.IsNullOrWhiteSpace(search)
           || mod.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
           || mod.Directory.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
