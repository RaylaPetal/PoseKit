using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PoseKit.Presets;

namespace PoseKit.Windows;

/// <summary>Live-offset drag fields + save button, and saved-preset buttons grouped by PoseIdentifier.</summary>
public static class PresetButtonsPanel
{
    private static string newPresetName = "";

    public static void Draw(Plugin plugin)
    {
        ImGui.TextUnformatted("Live Offset");

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var currentPose = PoseIdentifier.FromCharacter(localPlayer);
        if (currentPose is null)
        {
            ImGui.TextDisabled("Not currently in a pose/emote loop.");
        }
        else
        {
            ImGui.TextDisabled(currentPose.Value.DisplayName);

            var position = plugin.TempOffset.Offset.Position;
            if (ImGui.DragFloat3("Position##PoseKitOffset", ref position, 0.005f))
            {
                plugin.TempOffset.Active = true;
                plugin.TempOffset.Offset = new PoseOffset { Position = position };
                plugin.OffsetEngine.DesiredOffset = plugin.TempOffset.Offset;
                plugin.OffsetEngine.Active = true;
            }

            if (ImGui.Button("Reset##PoseKitOffsetReset"))
            {
                plugin.TempOffset.Reset();
                plugin.OffsetEngine.Active = false;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            ImGui.InputTextWithHint("##PoseKitPresetName", "Preset name", ref newPresetName, 64);
            ImGui.SameLine();
            var canSave = plugin.TempOffset.Active && newPresetName.Trim().Length > 0;
            using (ImRaii.Disabled(!canSave))
            {
                if (ImGui.Button("Save as preset##PoseKitSavePreset"))
                {
                    plugin.PresetManager.Save(newPresetName.Trim(), currentPose.Value, plugin.TempOffset.Offset);
                    newPresetName = "";
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Saved Presets");

        var groups = plugin.PresetManager.Presets.GroupBy(p => p.Pose);
        foreach (var group in groups)
        {
            ImGui.TextDisabled(group.Key.DisplayName);
            foreach (var namedPose in group)
            {
                if (ImGui.Button($"{namedPose.Name}##PoseKitPreset{namedPose.GetHashCode()}"))
                    plugin.PoseTrigger.Trigger(namedPose);

                ImGui.SameLine();
                if (ImGui.SmallButton($"x##PoseKitDeletePreset{namedPose.GetHashCode()}"))
                    plugin.PresetManager.Delete(namedPose);
            }
        }
    }
}
