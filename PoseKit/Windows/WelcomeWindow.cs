using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// First-run tutorial — opens automatically once (gated on Configuration.HasSeenWelcome) and never
/// again after being closed. Covers what PoseKit needs (Penumbra required, SimpleHeels optional),
/// the basic workflow, and lets the user set their animation-mod folder filter right away instead of
/// meeting an unfiltered wall of every installed mod the first time they open the mod picker.
/// </summary>
public class WelcomeWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

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

    public override void Draw()
    {
        using var theme = PoseKitUi.PushTheme();

        ImGui.TextWrapped("PoseKit offsets, saves, and replays poses, auto-discovers Penumbra animation " +
                           "mods, and can sync your pose to people you're paired with through SimpleHeels.");

        PoseKitUi.SectionHeader("Requirements");
        PoseKitUi.DrawDependencyStatus(plugin);
        PoseKitUi.TextWrappedDisabled("Penumbra is required for animation mod discovery. SimpleHeels is " +
                                       "optional — only needed if you want your pose offset to sync to paired viewers.");

        PoseKitUi.SectionHeader("Quick Start");
        ImGui.TextWrapped("1. Set a folder filter below, then pick which mods to scan in Settings.");
        ImGui.TextWrapped("2. Open the Animations tab and hit Play on any pose.");
        ImGui.TextWrapped("3. Use the Offsets tab to drag your character into position, then save it as a named preset.");

        PoseKitUi.SectionHeader("Animation Mod Folder Filter");
        var folderFilter = configuration.PenumbraFolderFilter;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##PoseKitWelcomeFolderFilter", "e.g. Animations (blank = all mods)", ref folderFilter, 128))
        {
            configuration.PenumbraFolderFilter = folderFilter;
            configuration.Save();
        }
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
}
