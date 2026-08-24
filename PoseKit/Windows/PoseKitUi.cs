using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PoseKit.Windows;

/// <summary>Small shared UI helpers so section styling and labeled controls stay consistent across
/// windows instead of each panel reinventing its own spacing/labels.</summary>
internal static class PoseKitUi
{
    public static readonly Vector4 Accent = new(0.78f, 0.65f, 1f, 1f);
    public static readonly Vector4 AccentMuted = new(0.43f, 0.32f, 0.58f, 1f);
    private static readonly Vector4 AccentHovered = new(0.55f, 0.41f, 0.74f, 1f);
    private static readonly Vector4 AccentActive = new(0.66f, 0.50f, 0.88f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Bad = new(0.9f, 0.4f, 0.4f, 1f);

    /// Applies PoseKit's shared lavender control theme for the lifetime of the returned scope.
    /// Window backgrounds remain under Dalamud's global theme so the plugin still feels native.
    public static ThemeScope PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, AccentMuted);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.32f, 0.24f, 0.43f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, AccentHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.20f, 0.16f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, AccentHovered);
        ImGui.PushStyleColor(ImGuiCol.TabActive, AccentMuted);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, new Vector4(0.55f, 0.41f, 0.74f, 0.45f));

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f));
        return new ThemeScope();
    }

    /// A red/green status line for the two things PoseKit's fuller feature set depends on — Penumbra
    /// (animation discovery) and SimpleHeels (optional offset sync) — so a user missing one notices
    /// immediately instead of quietly getting a degraded experience.
    public static void DrawDependencyStatus(Plugin plugin)
    {
        DrawStatus("Penumbra", plugin.PenumbraIpc.IsAvailable);
        ImGui.SameLine();
        DrawStatus("SimpleHeels", plugin.SimpleHeelsBridge.IsLoaded);
    }

    private static void DrawStatus(string name, bool detected)
        => ImGui.TextColored(detected ? Good : Bad, $"{name}: {(detected ? "Detected" : "Not Found")}");

    /// ImGui.TextDisabled doesn't wrap — fine for the short one-liners it's used for elsewhere, but a
    /// longer explanatory sentence just gets clipped at the window edge instead of flowing to a new
    /// line. This pushes the same muted color TextDisabled uses, but through TextWrapped.
    public static void TextWrappedDisabled(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// Inline "(conflict)" marker in warning red with a hover tooltip carrying the full explanation —
    /// used wherever a currently-selected option's gesture collides with another selected option's.
    public static void DrawConflictMarker(string tooltip)
    {
        ImGui.SameLine();
        ImGui.TextColored(Bad, "(conflict)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    public static void SectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Separator, AccentMuted);
        ImGui.Separator();
        ImGui.PopStyleColor();
        ImGui.TextColored(Accent, text);
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

    public readonly struct ThemeScope : System.IDisposable
    {
        public void Dispose()
        {
            ImGui.PopStyleVar(5);
            ImGui.PopStyleColor(13);
        }
    }
}
