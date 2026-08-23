using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private List<(string Directory, string Name)>? enabledModsCache;

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
        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        PoseKitUi.SectionHeader("Penumbra Mods to Scan for Poses");
        ImGui.TextDisabled("Only currently-enabled mods are listed. Scanning is opt-in per mod.");

        if (ImGui.Button("Refresh mod list##PoseKitRefreshMods"))
            RefreshEnabledMods();

        enabledModsCache ??= RefreshEnabledMods();

        if (enabledModsCache == null)
        {
            ImGui.TextDisabled("Penumbra not found.");
            return;
        }

        var changed = false;
        foreach (var (modDirectory, modName) in enabledModsCache)
        {
            var isSelected = configuration.SelectedPenumbraMods.Contains(modDirectory);
            if (ImGui.Checkbox($"{modName}##PoseKitModPick{modDirectory.GetHashCode()}", ref isSelected))
            {
                if (isSelected) configuration.SelectedPenumbraMods.Add(modDirectory);
                else configuration.SelectedPenumbraMods.Remove(modDirectory);
                changed = true;
            }
        }

        if (changed)
        {
            configuration.Save();
            plugin.RefreshPenumbraPoses();
        }
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
            if (enabled) list.Add((directory, name));
        }

        enabledModsCache = list;
        return list;
    }
}
