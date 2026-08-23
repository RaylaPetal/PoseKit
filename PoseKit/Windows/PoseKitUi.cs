using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PoseKit.Windows;

/// <summary>Small shared UI helpers so section styling and labeled controls stay consistent across
/// windows instead of each panel reinventing its own spacing/labels.</summary>
internal static class PoseKitUi
{
    private static readonly Vector4 HeaderColor = new(0.78f, 0.65f, 1f, 1f);

    public static void SectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(HeaderColor, text);
    }

    /// A single-axis drag float with its label always visible next to it, rather than an unlabeled
    /// X/Y/Z DragFloat3 that gives no indication which axis does what in-game.
    public static bool AxisDragFloat(string id, string label, ref float value, float speed = 0.005f)
    {
        ImGui.SetNextItemWidth(90);
        var changed = ImGui.DragFloat($"##PoseKit{id}", ref value, speed);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        return changed;
    }
}
