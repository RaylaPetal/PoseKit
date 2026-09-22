using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using PoseKit.Movement;

namespace PoseKit.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base($"PoseKit v{Plugin.Version}###MainWindow")
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

        PoseKitUi.DrawDependencyStatus(plugin);

        if (!ImGui.BeginTabBar("##PoseKitMainTabs", ImGuiTabBarFlags.None))
            return;

        if (ImGui.BeginTabItem("Animations"))
        {
            PairingPanel.Draw(plugin);
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

                // One shared header for all three utility rows below, instead of one per row — each
                // row is already a single button+status line (DrawAlignSection's own shape, mirrored
                // here for the other two), so the only real bulk was three separate headers' worth of
                // spacing/separator/colored-text overhead. Freecam's conditional hint line is the one
                // allowed second line — collapsing it into the row would make an already-dense line
                // unreadable, and it isn't shown most of the time anyway. See design.md Decision 4.
                PoseKitUi.SectionHeader("Utilities");

                if (ImGui.Button("Resync nearby emotes"))
                    plugin.EmoteSync.Sync();
                ImGui.SameLine();
                PoseKitUi.TextWrappedDisabled("Resets all nearby rendered players together on your client.");

                if (ImGui.Button(plugin.FreeCam.Enabled ? "Disable freecam" : "Enable freecam"))
                    plugin.FreeCam.Toggle();
                ImGui.SameLine();
                PoseKitUi.TextWrappedDisabled(plugin.FreeCam.Status);
                if (plugin.FreeCam.Enabled)
                    PoseKitUi.TextWrappedDisabled("WASD: move | E/Q: up/down | Right drag: look | /posekit tfc: exit");

                DrawAlignSection();
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

    private void DrawAlignSection()
    {
        var state = plugin.AlignService.GetAlignState();
        var canAlign = state.hasTarget && state.inRange && state.mode == CharacterModes.Normal && !state.isWalking;

        using (ImRaii.Disabled(!canAlign))
        {
            if (ImGui.Button(state.isWalking ? "Aligning..." : "Align to Target"))
                plugin.AlignService.AlignToTarget();
        }
        ImGui.SameLine();

        var status = state.isWalking ? "Walking to target..."
            : !state.hasTarget ? "No target selected."
            : state.mode != CharacterModes.Normal ? AlignService.BlockedReason(state.mode)
            : state.inRange ? $"Ready — target: {state.targetName} ({state.distance:F1}y)"
            : $"Too far: {state.targetName} ({state.distance:F1}y) — stand within {AlignService.MaxAlignDistance:F0}y first.";
        PoseKitUi.TextWrappedDisabled(status);
    }
}
