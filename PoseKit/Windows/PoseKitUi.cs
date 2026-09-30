using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace PoseKit.Windows;

/// <summary>Shared theme and UI helpers.</summary>
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

    /// The partner's pick, distinct from the accent.
    public static readonly Vector4 Info = new(0.4f, 0.65f, 0.95f, 1f);
    private static readonly Vector4 InfoMuted = new(0.24f, 0.36f, 0.55f, 1f);
    private static readonly Vector4 GoodMuted = new(0.24f, 0.5f, 0.24f, 1f);

    public static readonly Vector4 Muted = new(0.56f, 0.52f, 0.64f, 1f);

    // Surfaces, darkest to lightest: window, card, input field.
    private static readonly Vector4 WindowBg = new(0.065f, 0.055f, 0.085f, 0.98f);
    private static readonly Vector4 CardBg = new(0.095f, 0.080f, 0.125f, 1f);
    private static readonly Vector4 CardBorder = new(0.43f, 0.32f, 0.58f, 0.40f);
    private static readonly Vector4 FieldBg = new(0.15f, 0.12f, 0.20f, 1f);
    private static readonly Vector4 FieldBgHovered = new(0.21f, 0.16f, 0.28f, 1f);
    private static readonly Vector4 FieldBgActive = new(0.27f, 0.20f, 0.36f, 1f);

    /// Paints the window body background. Pushing WindowBg in PreDraw/PostDraw isn't safe: they
    /// aren't guaranteed to run as a pair, and an unmatched pop crashed the game.
    public static void PaintWindowBackground()
    {
        var style = ImGui.GetStyle();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var titleBarHeight = ImGui.GetFontSize() + style.FramePadding.Y * 2f;
        // Leaves the resize-grip triangle unpainted so only ImGui's own grip shows.
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

    public static void StatusDot(Vector4 color, string label)
    {
        ImGui.BeginGroup();
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, 5);
        ImGui.TextColored(Muted, label);
        ImGui.EndGroup();
    }

    /// Muted text that wraps, unlike ImGui.TextDisabled.
    public static void TextWrappedDisabled(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// Adds every parent folder of a Penumbra sort path, since the filter matches nested paths.
    public static void AddSortFolders(string? sortPath, ISet<string> folders)
    {
        if (string.IsNullOrEmpty(sortPath)) return;
        var segments = sortPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length; i++)
            folders.Add(string.Join('/', segments[..i]));
    }

    /// Penumbra sort-folder picker bound to Configuration.PenumbraFolderFilter.
    public static void DrawFolderFilterCombo(string label, Configuration configuration, IEnumerable<string>? folders)
    {
        var folderFilter = configuration.PenumbraFolderFilter;
        var folderLabel = folderFilter.Length == 0 ? "(All mods)" : folderFilter;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(label, folderLabel)) return;

        if (ImGui.Selectable("(All mods)", folderFilter.Length == 0))
            SetFolderFilter(configuration, "");

        foreach (var folder in folders ?? [])
        {
            if (ImGui.Selectable(folder, folder == folderFilter))
                SetFolderFilter(configuration, folder);
        }

        ImGui.EndCombo();
    }

    private static void SetFolderFilter(Configuration configuration, string folder)
    {
        configuration.PenumbraFolderFilter = folder;
        configuration.Save();
    }

    /// A button that runs <paramref name="resolve"/>, or plain "(conflict)" text when it's null.
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

    public static void SectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(Accent, text.ToUpperInvariant());
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// Close the row with CardTitleRule.
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

    /// Always pair with EndCard, whatever this returns.
    public static bool BeginCard(string id, Vector2 size, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        var visible = ImGui.BeginChild(id, size, true, flags);
        ImGui.PopStyleVar();
        return visible;
    }

    public static void EndCard() => ImGui.EndChild();

    public static bool PillTab(string label, bool selected, string? count = null)
    {
        var text = count != null ? $"{label}  {count}" : label;
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 12f)
            .Push(ImGuiStyleVar.FramePadding, new Vector2(14f, 5f));
        using var colors = ImRaii.PushColor(ImGuiCol.Button, selected ? AccentMuted : new Vector4(0f, 0f, 0f, 0f))
            .Push(ImGuiCol.ButtonHovered, selected ? AccentMuted : FieldBgHovered)
            .Push(ImGuiCol.ButtonActive, AccentActive)
            .Push(ImGuiCol.Text, selected ? new Vector4(1f, 1f, 1f, 1f) : Muted);
        return ImGui.Button($"{text}##PoseKitTab{label}");
    }

    public static bool ToggleButton(string label, bool on)
    {
        if (!on) return ImGui.Button(label);
        using (ImRaii.PushColor(ImGuiCol.Button, GoodMuted)
                   .Push(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.60f, 0.30f, 1f))
                   .Push(ImGuiCol.ButtonActive, Good))
            return ImGui.Button(label);
    }

    public const string KofiUrl = "https://ko-fi.com/raylapetal";
    private const string KofiLabel = "Donate";
    private static readonly Vector4 KofiRed = new(1f, 0.369f, 0.357f, 1f);        // Ko-fi's brand #FF5E5B
    private static readonly Vector4 KofiRedHovered = new(1f, 0.47f, 0.46f, 1f);
    private static readonly Vector4 KofiRedActive = new(0.85f, 0.29f, 0.28f, 1f);

    public static float KofiButtonWidth() =>
        Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(Dalamud.Interface.FontAwesomeIcon.MugHot, KofiLabel);

    public static void KofiButton()
    {
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f)))
        {
            if (Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(Dalamud.Interface.FontAwesomeIcon.MugHot, KofiLabel,
                    KofiRed, KofiRedActive, KofiRedHovered))
                Dalamud.Utility.Util.OpenLink(KofiUrl);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support PoseKit on Ko-fi\nko-fi.com/raylapetal");
    }

    /// For destructive actions.
    public static bool DangerButton(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, BadMuted)
                   .Push(ImGuiCol.ButtonHovered, BadHovered)
                   .Push(ImGuiCol.ButtonActive, Bad))
            return ImGui.Button(label);
    }

    public static bool WideButton(string label) =>
        ImGui.Button(label, new Vector2(ImGui.GetContentRegionAvail().X, 0));

    public static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label.Split("##")[0]).X + ImGui.GetStyle().FramePadding.X * 2;

    /// SameLine only if the next item fits, so button rows wrap.
    public static void SameLineIfFits(float width)
    {
        var limit = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= limit)
            ImGui.SameLine();
    }

    public static bool AxisDragFloat(string id, string label, ref float value, float speed = 0.005f, float width = 90f)
    {
        ImGui.SetNextItemWidth(width);
        var changed = ImGui.DragFloat($"##PoseKit{id}", ref value, speed);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        return changed;
    }

    /// Whose pick a queueable item is.
    public enum PickState { None, Own, Partner, Both }

    public static PickState GetPickState(Plugin plugin, string label)
    {
        var queue = plugin.CoupleQueueService;
        var isOwn = queue.QueuedSelectionName == label;
        var isPartner = queue.PartnerSelectionName == label;
        return isOwn && isPartner ? PickState.Both : isOwn ? PickState.Own : isPartner ? PickState.Partner : PickState.None;
    }

    /// Own pick in the accent color, partner's in blue, both in green. None pushes nothing.
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

        SameLineIfFits(ImGui.CalcTextSize(b.Text).X);
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
