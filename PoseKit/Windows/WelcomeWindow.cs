using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// First-run tutorial, shown once.
/// </summary>
public class WelcomeWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private List<string>? availableFoldersCache;

    public WelcomeWindow(Plugin plugin) : base("Welcome to PoseKit###PoseKitWelcome")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 420),
            MaximumSize = new Vector2(600, 800),
        };
        Size = new Vector2(460, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void OnOpen() => availableFoldersCache = null;

    public override void Draw()
    {
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        ImGui.TextWrapped("PoseKit offsets, saves, and replays poses, auto-discovers Penumbra animation " +
                           "mods, and can sync your pose to people you're paired with through SimpleHeels.");

        PoseKitUi.SectionHeader("Requirements");
        PoseKitUi.DrawDependencyStatus(plugin);
        PoseKitUi.TextWrappedDisabled("Penumbra is required for animation mod discovery. SimpleHeels is " +
                                       "optional — only needed if you want your pose offset to sync to paired viewers.");

        PoseKitUi.SectionHeader("Quick Start");
        ImGui.TextWrapped("1. Set a folder filter below, then pick which mods to scan in Settings.");
        ImGui.TextWrapped("2. Open the Animations tab at the top and hit Play on any pose.");
        ImGui.TextWrapped("3. Use Live Offset to drag your character into position, then save it as a named preset under Presets.");

        PoseKitUi.SectionHeader("Animation Mod Folder Filter");
        if (availableFoldersCache == null)
            RefreshFolders();
        PoseKitUi.DrawFolderFilterCombo("##PoseKitWelcomeFolderFilter", configuration, availableFoldersCache);
        PoseKitUi.TextWrappedDisabled("Restricts the mod picker to this Penumbra sort-folder — you can " +
                                       "change this anytime in Settings, along with which specific mods to scan.");

        ImGui.Spacing();
        ImGui.Spacing();
        if (ImGui.Button("Got it, let's go!##PoseKitWelcomeDone", new Vector2(ImGui.GetContentRegionAvail().X, 32)))
        {
            configuration.HasSeenWelcome = true;
            configuration.Save();
            IsOpen = false;
        }
    }

    /// Stays null while Penumbra is unavailable, so the next frame retries.
    private void RefreshFolders()
    {
        if (plugin.PenumbraIpc.TryGetModList() is not { } modList) return;

        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, name) in modList)
            PoseKitUi.AddSortFolders(plugin.PenumbraIpc.TryGetModPath(directory, name), folders);

        availableFoldersCache = [.. folders];
    }
}
