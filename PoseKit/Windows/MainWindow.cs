using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("PoseKit##MainWindow")
    {
        // The window itself stays fixed so the navigation remains visible. Each tab owns its own
        // scrolling child region below the tab bar instead.
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

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

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Priority = 0,
            ShowTooltip = () => ImGui.SetTooltip("PoseKit Settings"),
            Click = _ => plugin.ToggleConfigUi(),
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        using var theme = PoseKitUi.PushTheme();

        if (!ImGui.BeginTabBar("##PoseKitMainTabs", ImGuiTabBarFlags.None))
            return;

        if (ImGui.BeginTabItem("Animations"))
        {
            PenumbraPosePanel.DrawToolbar(plugin);
            if (ImGui.BeginChild("##PoseKitAnimationsScroll", Vector2.Zero, false, ImGuiWindowFlags.None))
                PenumbraPosePanel.Draw(plugin);
            ImGui.EndChild();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Offsets"))
        {
            if (ImGui.BeginChild("##PoseKitOffsetsScroll", Vector2.Zero, false, ImGuiWindowFlags.None))
            {
                PresetButtonsPanel.DrawOffsets(plugin);

                PoseKitUi.SectionHeader("Emote Sync");
                if (ImGui.Button("Resync nearby emotes"))
                    plugin.EmoteSync.Sync();
                ImGui.SameLine();
                ImGui.TextDisabled("Resets all nearby rendered players together on your client.");
            }
            ImGui.EndChild();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Presets"))
        {
            if (ImGui.BeginChild("##PoseKitPresetsScroll", Vector2.Zero, false, ImGuiWindowFlags.None))
                PresetButtonsPanel.DrawPresets(plugin);
            ImGui.EndChild();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }
}
