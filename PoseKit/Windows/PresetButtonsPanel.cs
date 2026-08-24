using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PoseKit.Presets;

namespace PoseKit.Windows;

/// <summary>Live-offset and saved-preset views. They share state but are drawn in separate main
/// tabs so editing a pose and browsing the preset library each have room to breathe.</summary>
public static class PresetButtonsPanel
{
    private static string newPresetName = "";

    public static void DrawOffsets(Plugin plugin)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var currentPose = PoseIdentifier.FromCharacter(localPlayer);
        PoseKitUi.SectionHeader("Live Offset");
        ImGui.TextDisabled(currentPose?.DisplayName ?? "Not currently in a pose/emote loop.");

        using (ImRaii.Disabled(currentPose is null))
        {
            var offset = plugin.OffsetEngine.DesiredOffset;
            var changed = false;

            changed |= PoseKitUi.AxisDragFloat("OffsetX", "Left / Right", ref offset.Position.X);
            changed |= PoseKitUi.AxisDragFloat("OffsetY", "Height", ref offset.Position.Y);
            changed |= PoseKitUi.AxisDragFloat("OffsetZ", "Forward / Backward", ref offset.Position.Z);

            if (plugin.OffsetEngine.RotationHookResolved)
            {
                var degrees = offset.Rotation * (180f / MathF.PI);
                if (PoseKitUi.AxisDragFloat("Rotation", "Rotation (degrees)", ref degrees, 1f))
                {
                    degrees %= 360f;
                    if (degrees < 0) degrees += 360f;
                    offset.Rotation = degrees * (MathF.PI / 180f);
                    changed = true;
                }
            }
            else
            {
                ImGui.TextDisabled("Rotation offset unavailable — hook didn't resolve this game version.");
            }

            if (changed)
            {
                plugin.OffsetEngine.DesiredOffset = offset;
                plugin.OffsetEngine.Active = true;
            }
        }

        // Always available, regardless of current pose — this is the manual escape hatch for a
        // stuck offset, so it can't be hidden behind the very state that made it hard to fix.
        if (ImGui.Button("Reset##PoseKitOffsetReset"))
        {
            plugin.OffsetEngine.Reset(localPlayer);
            plugin.LoadedPreset = null;
            plugin.LastPlayedPenumbraContext = null;
        }

        if (currentPose is { } pose && plugin.LoadedPreset is { } loaded && loaded.Pose == pose)
        {
            ImGui.SameLine();
            if (ImGui.Button("Update preset##PoseKitUpdatePreset"))
                plugin.PresetManager.Update(loaded, plugin.OffsetEngine.DesiredOffset);
        }
    }

    public static void DrawPresets(Plugin plugin)
    {
        var currentPose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);

        PoseKitUi.SectionHeader("Save Current Offset");
        ImGui.TextDisabled(currentPose?.DisplayName ?? "Start a pose or animation before saving a preset.");

        if (currentPose is { } pose)
        {
            ImGui.SetNextItemWidth(150);
            ImGui.InputTextWithHint("##PoseKitPresetName", "Preset name", ref newPresetName, 64);
            ImGui.SameLine();
            var canSave = plugin.OffsetEngine.Active && newPresetName.Trim().Length > 0;
            using (ImRaii.Disabled(!canSave))
            {
                if (ImGui.Button("Save as preset##PoseKitSavePreset"))
                {
                    var saved = plugin.PresetManager.Save(newPresetName.Trim(), pose, plugin.OffsetEngine.DesiredOffset,
                        plugin.LastPlayedPenumbraContext);
                    plugin.LoadedPreset = saved;
                    newPresetName = "";
                }
            }
        }

        PoseKitUi.SectionHeader("Preset Library");

        var groups = plugin.PresetManager.Presets.GroupBy(p => p.Pose);
        if (!groups.Any())
        {
            ImGui.TextDisabled("No saved presets yet.");
            return;
        }

        foreach (var group in groups)
        {
            if (!ImGui.CollapsingHeader($"{group.Key.DisplayName}##PoseKitPresetGroup{group.Key.GetHashCode()}",
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.Indent();
            foreach (var namedPose in group.ToList())
            {
                if (ImGui.Button($"{namedPose.Name}##PoseKitPreset{namedPose.GetHashCode()}"))
                    plugin.PlayPreset(namedPose);

                ImGui.SameLine();
                if (ImGui.SmallButton($"x##PoseKitDeletePreset{namedPose.GetHashCode()}"))
                    plugin.PresetManager.Delete(namedPose);
            }
            ImGui.Unindent();
        }
    }
}
