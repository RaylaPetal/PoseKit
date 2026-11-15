using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace PoseKit.Windows;

/// A "Support me" label with small Ko-fi, Patreon and Discord icon buttons on one line, matching
/// Oathbound so both plugins present support the same way.
public static class SupportLinks
{
    private const string KofiUrl = "https://ko-fi.com/raylapetal";
    private const string PatreonUrl = "https://www.patreon.com/RaylaPetal";

    /// Shared with Oathbound, so the text never names either plugin as its owner.
    public const string DiscordUrl = "https://discord.gg/87eMbV4FU9";

    private const string Label = "Support me";

    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 KofiBlue = new(0.161f, 0.671f, 0.878f, 1f);
    private static readonly Vector4 KofiBlueHovered = new(0.29f, 0.75f, 0.93f, 1f);
    private static readonly Vector4 PatreonCoral = new(1f, 0.259f, 0.302f, 1f);
    private static readonly Vector4 PatreonCoralHovered = new(1f, 0.4f, 0.44f, 1f);
    private static readonly Vector4 DiscordBlurple = new(0.345f, 0.396f, 0.949f, 1f);
    private static readonly Vector4 DiscordBlurpleHovered = new(0.45f, 0.49f, 0.97f, 1f);

    private static float ButtonSize => ImGui.GetFrameHeight();

    /// Exact, so the main window header can right-align and wrap its tools.
    public static float Width =>
        ImGui.CalcTextSize(Label).X + ImGui.GetStyle().ItemSpacing.X * 3 + ButtonSize * 3;

    /// Drawn at the cursor: the label, then the three buttons.
    public static void Draw()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Label);

        ImGui.SameLine();
        if (IconButton("kofi", FontAwesomeIcon.MugHot, KofiBlue, KofiBlueHovered))
            Dalamud.Utility.Util.OpenLink(KofiUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support PoseKit on Ko-fi\nko-fi.com/raylapetal");

        ImGui.SameLine();
        if (IconButton("patreon", FontAwesomeIcon.HandHoldingHeart, PatreonCoral, PatreonCoralHovered))
            Dalamud.Utility.Util.OpenLink(PatreonUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support PoseKit on Patreon\npatreon.com/RaylaPetal");

        ImGui.SameLine();
        // Dalamud's FontAwesome set has no brand icons, so Discord gets a chat bubble in its own color.
        if (IconButton("discord", FontAwesomeIcon.Comments, DiscordBlurple, DiscordBlurpleHovered))
            Dalamud.Utility.Util.OpenLink(DiscordUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Join the Discord: updates and help\ndiscord.gg/87eMbV4FU9");
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, Vector4 color, Vector4 hovered)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, White))
            return ImGuiComponents.IconButton($"PoseKitSupport{id}", icon, color, hovered, hovered, new Vector2(ButtonSize));
    }
}
