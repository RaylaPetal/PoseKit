using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("PoseKit##MainWindow")
    {
        // Wide enough by default that pose labels ("Sit on Ground Pose 3" etc.) alongside their
        // checkboxes/trigger buttons don't get clipped at the window edge.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 250),
            MaximumSize = new Vector2(1200, 1200)
        };

        Size = new Vector2(600, 700);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.Button("Settings"))
        {
            plugin.ToggleConfigUi();
        }

        PresetButtonsPanel.Draw(plugin);

        PoseKitUi.SectionHeader("Penumbra Poses");
        PenumbraPosePanel.Draw(plugin);

        PoseKitUi.SectionHeader("Emote Sync");
        if (ImGui.Button("Resync my emote"))
        {
            plugin.EmoteSync.Sync();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Local only — no networking, no other player involved.");
    }
}
