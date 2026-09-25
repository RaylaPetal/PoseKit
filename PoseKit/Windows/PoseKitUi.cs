using System;
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
    private static readonly Vector4 BadMuted = new(0.55f, 0.20f, 0.24f, 1f);
    private static readonly Vector4 BadHovered = new(0.72f, 0.28f, 0.32f, 1f);

    /// A distinct hue from the plugin's own lavender accent — used only for "this is the partner's
    /// pick, not yours" so the two are never confusable at a glance.
    public static readonly Vector4 Info = new(0.4f, 0.65f, 0.95f, 1f);
    private static readonly Vector4 InfoMuted = new(0.24f, 0.36f, 0.55f, 1f);
    private static readonly Vector4 GoodMuted = new(0.24f, 0.5f, 0.24f, 1f);

    /// Secondary text — a lavender-tinted grey rather than plain grey, so muted text still reads as
    /// part of the same purple palette.
    public static readonly Vector4 Muted = new(0.56f, 0.52f, 0.64f, 1f);

    // Surfaces, darkest to lightest: window, card, input field.
    private static readonly Vector4 WindowBg = new(0.065f, 0.055f, 0.085f, 0.98f);
    private static readonly Vector4 CardBg = new(0.095f, 0.080f, 0.125f, 1f);
    private static readonly Vector4 CardBorder = new(0.43f, 0.32f, 0.58f, 0.40f);
    private static readonly Vector4 FieldBg = new(0.15f, 0.12f, 0.20f, 1f);
    private static readonly Vector4 FieldBgHovered = new(0.21f, 0.16f, 0.28f, 1f);
    private static readonly Vector4 FieldBgActive = new(0.27f, 0.20f, 0.36f, 1f);

    /// Paints PoseKit's darker purple background over the current window's body (below the title
    /// bar), as the first thing in Draw(). Deliberately not done by pushing ImGuiCol.WindowBg from
    /// Window.PreDraw()/PostDraw(): Dalamud's WindowHost doesn't guarantee those run as a matched pair
    /// around the window, and an unmatched PopStyleColor there crashed the game inside cimgui. Drawing
    /// directly onto the window's draw list touches no style stack at all, so it can't unbalance one.
    public static void PaintWindowBackground()
    {
        var style = ImGui.GetStyle();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var titleBarHeight = ImGui.GetFontSize() + style.FramePadding.Y * 2f;
        // The bottom-right resize-grip triangle is deliberately left unpainted, so ImGui's own grip is
        // the only one ever visible — whether ImGui draws it before or after this runs. (Painting over
        // it and redrawing a replacement showed two grips in-game.) Three pieces cover everything else:
        //   top:    the body down to the grip square, rounded on no corners
        //   strip:  the bottom band up to the grip square, rounded bottom-left only
        //   corner: the upper-left half of the grip square, leaving the grip's own half untouched
        var gripSize = MathF.Max(ImGui.GetFontSize() * 1.35f, style.WindowRounding + 1f + ImGui.GetFontSize() * 0.2f);
        var bodyMin = new Vector2(pos.X, pos.Y + titleBarHeight);
        var max = pos + size;
        var gripTop = max.Y - gripSize;
        var gripLeft = max.X - gripSize;
        var color = ImGui.GetColorU32(WindowBg);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(bodyMin, new Vector2(max.X, gripTop), color);
        drawList.AddRectFilled(new Vector2(pos.X, gripTop), new Vector2(gripLeft, max.Y), color,
            style.WindowRounding, ImDrawFlags.RoundCornersBottomLeft);
        drawList.AddTriangleFilled(new Vector2(gripLeft, gripTop), new Vector2(max.X, gripTop), new Vector2(gripLeft, max.Y), color);
    }

    /// Applies PoseKit's shared lavender theme for the lifetime of the returned scope: controls,
    /// input fields, cards (child windows) and separators.
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
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, FieldBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FieldBgHovered);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, FieldBgActive);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.10f, 0.08f, 0.13f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Separator, CardBorder);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, AccentMuted);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, AccentHovered);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, AccentActive);

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        return new ThemeScope();
    }

    /// A red/green status line for the two things PoseKit's fuller feature set depends on — Penumbra
    /// (animation discovery) and SimpleHeels (optional offset sync) — so a user missing one notices
    /// immediately instead of quietly getting a degraded experience.
    public static void DrawDependencyStatus(Plugin plugin)
    {
        DrawStatus("Penumbra", plugin.PenumbraIpc.IsAvailable);
        ImGui.SameLine(0, 16);
        DrawStatus("SimpleHeels", plugin.SimpleHeelsBridge.IsLoaded);
    }

    private static void DrawStatus(string name, bool detected)
    {
        StatusDot(detected ? Good : Bad, name.ToUpperInvariant());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(detected ? $"{name} detected." : $"{name} not found.");
    }

    /// A colored dot followed by a muted label, as one hoverable unit.
    public static void StatusDot(Vector4 color, string label)
    {
        ImGui.BeginGroup();
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, 5);
        ImGui.TextColored(Muted, label);
        ImGui.EndGroup();
    }

    /// ImGui.TextDisabled doesn't wrap — fine for the short one-liners it's used for elsewhere, but a
    /// longer explanatory sentence just gets clipped at the window edge instead of flowing to a new
    /// line. This pushes the same muted color TextDisabled uses, but through TextWrapped.
    public static void TextWrappedDisabled(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// Inline conflict marker in warning red with a hover tooltip carrying the full explanation — used
    /// wherever a currently-selected option's gesture collides with another active option's. When the
    /// conflict can be fixed automatically (<paramref name="resolve"/> non-null), it's a red button
    /// that runs the fix on click; otherwise plain "(conflict)" text.
    public static void DrawConflictMarker(string id, string tooltip, Action? resolve)
    {
        ImGui.SameLine();
        if (resolve == null)
        {
            ImGui.TextColored(Bad, "(conflict)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
            return;
        }

        using (ImRaii.PushColor(ImGuiCol.Button, BadMuted)
                   .Push(ImGuiCol.ButtonHovered, BadHovered)
                   .Push(ImGuiCol.ButtonActive, Bad)
                   .Push(ImGuiCol.Text, Vector4.One))
        {
            if (ImGui.Button($"Conflict: keep this##{id}"))
                resolve();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    /// A sub-section title inside a card: small uppercase accent text with a faint rule under it.
    public static void SectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(Accent, text.ToUpperInvariant());
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// A card's own title row: uppercase accent title, an optional muted count/subtitle beside it.
    /// Callers can keep drawing on the same line (e.g. a right-aligned search box) before closing the
    /// row with CardTitleRule.
    public static void CardTitle(string title, string? subtitle = null)
    {
        ImGui.TextColored(Accent, title.ToUpperInvariant());
        if (subtitle != null)
        {
            ImGui.SameLine(0, 10);
            ImGui.TextColored(Muted, subtitle);
        }
    }

    public static void CardTitleRule()
    {
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// A bordered, rounded, padded panel — PoseKit's basic layout block. Always pair with EndCard,
    /// whatever BeginCard returns (same contract as ImGui.BeginChild).
    public static bool BeginCard(string id, Vector2 size, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        var visible = ImGui.BeginChild(id, size, true, flags);
        ImGui.PopStyleVar();
        return visible;
    }

    public static void EndCard() => ImGui.EndChild();

    /// A full-width sidebar navigation row: highlighted while selected, with an optional count (or
    /// short badge) right-aligned in muted text. True when clicked.
    public static bool NavItem(string label, bool selected, string? count = null, Vector4? countColor = null)
    {
        const float extraHeight = 8f;
        var rowY = ImGui.GetCursorPosY();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
        var clicked = ImGui.Selectable($"  {label}##PoseKitNav{label}", selected, ImGuiSelectableFlags.None,
            new Vector2(0, ImGui.GetTextLineHeight() + extraHeight));
        ImGui.PopStyleVar();
        if (count != null)
        {
            var width = ImGui.CalcTextSize(count).X;
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - width - 8f);
            ImGui.SetCursorPosY(rowY + extraHeight / 2f);
            ImGui.TextColored(countColor ?? Muted, count);
        }
        return clicked;
    }

    /// A normal-size button tinted in the muted warning red — for destructive actions like deleting a
    /// preset, so they read as different from the lavender buttons around them.
    public static bool DangerButton(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, BadMuted)
                   .Push(ImGuiCol.ButtonHovered, BadHovered)
                   .Push(ImGuiCol.ButtonActive, Bad))
            return ImGui.Button(label);
    }

    /// A button that fills the remaining width of the current line/card.
    public static bool WideButton(string label) =>
        ImGui.Button(label, new Vector2(ImGui.GetContentRegionAvail().X, 0));

    /// Width a normal (auto-sized) button with this label will take — for right-aligning a row.
    public static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label.Split("##")[0]).X + ImGui.GetStyle().FramePadding.X * 2;

    /// A single-axis drag float with its label always visible next to it, rather than an unlabeled
    /// X/Y/Z DragFloat3 that gives no indication which axis does what in-game.
    public static bool AxisDragFloat(string id, string label, ref float value, float speed = 0.005f, float width = 90f)
    {
        ImGui.SetNextItemWidth(width);
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
            ImGui.PopStyleVar(8);
            ImGui.PopStyleColor(24);
        }
    }
}
