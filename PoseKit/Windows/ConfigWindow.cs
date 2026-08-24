using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace PoseKit.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private List<(string Directory, string Name)>? enabledModsCache;
    private string modSearch = "";

    public ConfigWindow(Plugin plugin) : base("PoseKit Settings###PoseKitConfig")
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

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        using var theme = PoseKitUi.PushTheme();

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        PoseKitUi.SectionHeader("Penumbra Mods to Scan for Poses");
        ImGui.TextDisabled("Only currently-enabled mods are listed. Scanning is opt-in per mod.");

        var folderFilter = configuration.PenumbraFolderFilter;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("Sort folder##PoseKitFolderFilter", "e.g. Animations (blank = all mods)", ref folderFilter, 128))
        {
            configuration.PenumbraFolderFilter = folderFilter;
            configuration.Save();
            RefreshEnabledMods();
        }
        ImGui.TextDisabled("Filters by Penumbra's own mod-organization folder (Mods tab), not the disk folder.");

        if (ImGui.Button("Refresh enabled mods##PoseKitRefreshMods"))
            RefreshEnabledMods();

        enabledModsCache ??= RefreshEnabledMods();

        if (enabledModsCache == null)
        {
            ImGui.TextDisabled("Penumbra not found.");
        }
        else
        {
            ImGui.SameLine();
            var selectedEnabledCount = enabledModsCache.Count(mod =>
                configuration.SelectedPenumbraMods.Contains(mod.Directory));
            ImGui.TextColored(PoseKitUi.Accent,
                $"{selectedEnabledCount} selected / {enabledModsCache.Count} enabled");

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##PoseKitModSearch", "Search enabled mods...", ref modSearch, 128);

            var visibleMods = enabledModsCache
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
                foreach (var (modDirectory, modName) in visibleMods)
                {
                    var isSelected = configuration.SelectedPenumbraMods.Contains(modDirectory);
                    if (ImGui.Checkbox($"{modName}##PoseKitModPick{modDirectory.GetHashCode()}", ref isSelected))
                    {
                        if (isSelected) configuration.SelectedPenumbraMods.Add(modDirectory);
                        else configuration.SelectedPenumbraMods.Remove(modDirectory);
                        changed = true;
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
        ImGui.TextDisabled("Questions and bug reports are welcome.");
    }

    private List<(string, string)>? RefreshEnabledMods()
    {
        var modList = plugin.PenumbraIpc.TryGetModList();
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        if (modList == null || collectionId is not { } cid)
        {
            enabledModsCache = null;
            return null;
        }

        var list = new List<(string, string)>();
        foreach (var (directory, name) in modList)
        {
            var (enabled, _) = plugin.PenumbraIpc.TryGetCurrentSettings(cid, directory);
            if (!enabled) continue;

            var sortPath = plugin.PenumbraIpc.TryGetModPath(directory, name);
            if (MatchesFolderFilter(sortPath, configuration.PenumbraFolderFilter))
                list.Add((directory, name));
        }

        enabledModsCache = list;
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

    private static bool MatchesModSearch((string Directory, string Name) mod, string search)
        => string.IsNullOrWhiteSpace(search)
           || mod.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
           || mod.Directory.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
