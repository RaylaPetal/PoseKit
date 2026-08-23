using System.Linq;
using Dalamud.Bindings.ImGui;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Windows;

/// <summary>Penumbra-discovered pose buttons, grouped by mod. Reuses the same PoseTrigger/OffsetEngine
/// replay path as saved presets (design doc §5 — "shared key across Features 2 and 3").</summary>
public static class PenumbraPosePanel
{
    public static void Draw(Plugin plugin)
    {
        if (!plugin.PenumbraIpc.IsAvailable)
        {
            ImGui.TextDisabled("Penumbra not found — pose discovery unavailable.");
            return;
        }

        if (ImGui.Button("Rescan##PoseKitPenumbraRescan"))
            plugin.RefreshPenumbraPoses();

        foreach (var group in plugin.DiscoveredPoses.GroupBy(p => p.ModName))
        {
            ImGui.TextDisabled(group.Key);
            foreach (var pose in group)
            {
                if (ImGui.Button($"{pose.Identifier.DisplayName}##PoseKitPenumbra{pose.FilePath.GetHashCode()}"))
                    plugin.PoseTrigger.Trigger(new NamedPose { Name = pose.Identifier.DisplayName, Pose = pose.Identifier, Offset = PoseOffset.Zero });
            }
        }
    }
}
