using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace PoseKit.Windows;

/// <summary>Small shared UI helpers so section styling and labeled controls stay consistent across
/// windows instead of each panel reinventing its own spacing/labels.</summary>
internal static class PoseKitUi
{
    public static readonly Vector4 Accent = new(0.78f, 0.65f, 1f, 1f);
    public static readonly Vector4 AccentMuted = new(0.43f, 0.32f, 0.58f, 1f);
    private static readonly Vector4 AccentHovered = new(0.55f, 0.41f, 0.74f, 1f);
    private static readonly Vector4 AccentActive = new(0.66f, 0.50f, 0.88f, 1f);
    public static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Bad = new(0.9f, 0.4f, 0.4f, 1f);

    /// A distinct hue from the plugin's own lavender accent — used only for "this is the partner's
    /// pick, not yours" so the two are never confusable at a glance.
    public static readonly Vector4 Info = new(0.4f, 0.65f, 0.95f, 1f);
    private static readonly Vector4 InfoMuted = new(0.24f, 0.36f, 0.55f, 1f);
    private static readonly Vector4 GoodMuted = new(0.24f, 0.5f, 0.24f, 1f);

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

    /// Whether — and whose — pick a given queueable item (a preset name, or a Penumbra "ModName —
    /// OptionName" label) currently is, relative to the couple queue. Shared by the Presets and
    /// Animations tabs so both highlight consistently instead of each reinventing the comparison
    /// (the Presets tab previously only checked its own pick, missing the partner's entirely).
    public enum PickState { None, Own, Partner, Both }

    public static PickState GetPickState(Plugin plugin, string label)
    {
        var queue = plugin.CoupleQueueService;
        var isOwn = queue.QueuedSelectionName == label;
        var isPartner = queue.PartnerSelectionName == label;
        return isOwn && isPartner ? PickState.Both : isOwn ? PickState.Own : isPartner ? PickState.Partner : PickState.None;
    }

    /// Strong, unmistakable styling for a queued item's button — filled background, a colored border,
    /// and bright text, not just a small marker — so a pick reads clearly even at a glance. Own pick
    /// is the plugin's usual accent color; the partner's pick uses a distinct blue so the two are
    /// never confused; both-picked (about to fire) turns green. Dispose the returned scope (or just
    /// `using`) to restore normal styling regardless of which branch was taken — PickState.None
    /// pushes nothing and returns a no-op scope, so callers don't need to branch themselves.
    public static PickStyleScope PushPickButtonStyle(PickState state)
    {
        (Vector4 Fill, Vector4 Border)? colors = state switch
        {
            PickState.Own => (AccentMuted, Accent),
            PickState.Partner => (InfoMuted, Info),
            PickState.Both => (GoodMuted, Good),
            _ => null,
        };
        if (colors is not { } c) return new PickStyleScope(null, false);

        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f);
        var pushedColors = ImRaii.PushColor(ImGuiCol.Button, c.Fill)
            .Push(ImGuiCol.ButtonHovered, c.Fill)
            .Push(ImGuiCol.ButtonActive, c.Fill)
            .Push(ImGuiCol.Border, c.Border)
            .Push(ImGuiCol.Text, Vector4.One);
        return new PickStyleScope(pushedColors, true);
    }

    /// Inline "● you" / "● them" / "● ready!" tag matching PushPickButtonStyle's color choice — put
    /// next to a mod header or option label so the *whole path* to a pick reads clearly, not just its
    /// own Play button.
    public static void DrawPickBadge(PickState state)
    {
        (Vector4 Color, string Text)? badge = state switch
        {
            PickState.Own => (Accent, "● you"),
            PickState.Partner => (Info, "● them"),
            PickState.Both => (Good, "● ready!"),
            _ => null,
        };
        if (badge is not { } b) return;

        ImGui.SameLine();
        ImGui.TextColored(b.Color, b.Text);
    }

    public readonly struct PickStyleScope(ImRaii.ColorDisposable? colors, bool pushedBorderVar) : System.IDisposable
    {
        public void Dispose()
        {
            colors?.Dispose();
            if (pushedBorderVar) ImGui.PopStyleVar();
        }
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
