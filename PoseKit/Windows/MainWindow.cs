using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// Header with page tabs and tools, then the selected page beside a right rail with Pairing, Live
/// Offset and Bone Align.
/// </summary>
public class MainWindow : Window, IDisposable
{
    private enum Page { Animations, Presets }

    private const float RailWidth = 300f;
    private const float NarrowRailWidth = 260f;
    private const float NarrowWindowWidth = 820f;

    private readonly Plugin plugin;
    private Page page = Page.Animations;

    public MainWindow(Plugin plugin)
        : base($"PoseKit v{Plugin.Version}###MainWindow")
    {
        // The window itself stays fixed; each card scrolls on its own.
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 460),
            MaximumSize = new Vector2(1600, 1200)
        };

        Size = new Vector2(1000, 680);
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
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        DrawHeader();
        DrawStatusLine();
        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var railWidth = avail.X < NarrowWindowWidth ? NarrowRailWidth : RailWidth;

        if (PoseKitUi.BeginCard("##PoseKitContent", new Vector2(avail.X - railWidth - spacing, avail.Y),
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            DrawPage();
        PoseKitUi.EndCard();

        ImGui.SameLine();
        if (PoseKitUi.BeginCard("##PoseKitRail", new Vector2(railWidth, avail.Y)))
            DrawRail();
        PoseKitUi.EndCard();
    }

    private void DrawHeader()
    {
        ImGui.SetWindowFontScale(1.3f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Accent, "P O S E K I T");
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine(0, 8);
        ImGui.TextColored(PoseKitUi.Muted, $"v{Plugin.Version}");

        ImGui.SameLine(0, 24);
        if (PoseKitUi.PillTab("Animations", page == Page.Animations, plugin.DiscoveredPoses.Count.ToString()))
            page = Page.Animations;
        ImGui.SameLine(0, 4);
        if (PoseKitUi.PillTab("Presets", page == Page.Presets, plugin.PresetManager.Presets.Count.ToString()))
            page = Page.Presets;

        const string resyncLabel = "Resync##PoseKitHeaderResync";
        const string settingsLabel = "Settings##PoseKitHeaderSettings";
        var freecamLabel = plugin.FreeCam.Enabled ? "Freecam on##PoseKitHeaderFreecam" : "Freecam##PoseKitHeaderFreecam";
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        const float donateGap = 14f;
        var toolsWidth = PoseKitUi.ButtonWidth(resyncLabel) + PoseKitUi.ButtonWidth(freecamLabel) + PoseKitUi.ButtonWidth(settingsLabel) + spacing * 2
                         + donateGap + PoseKitUi.KofiButtonWidth();
        var toolsX = ImGui.GetWindowContentRegionMax().X - toolsWidth;
        PoseKitUi.SameLineIfFits(toolsWidth);
        if (ImGui.GetCursorPosX() < toolsX)
            ImGui.SetCursorPosX(toolsX);

        if (ImGui.Button(resyncLabel))
            plugin.EmoteSync.Sync();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restart every nearby player's emote together, on your screen.");
        ImGui.SameLine();
        if (PoseKitUi.ToggleButton(freecamLabel, plugin.FreeCam.Enabled))
            plugin.FreeCam.Toggle();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.FreeCam.Status);
        ImGui.SameLine();
        if (ImGui.Button(settingsLabel))
            plugin.ToggleConfigUi();

        ImGui.SameLine(0, donateGap);
        PoseKitUi.KofiButton();
    }

    /// While freecam is on, its controls replace the color legend.
    private void DrawStatusLine()
    {
        PoseKitUi.DrawDependencyStatus(plugin);
        ImGui.SameLine(0, 16);
        DrawPairingStatus();

        if (plugin.FreeCam.Enabled)
        {
            const string hint = "Freecam: WASD move · E/Q up/down · right-drag look";
            var hintWidth = ImGui.CalcTextSize(hint).X;
            PoseKitUi.SameLineIfFits(hintWidth + 16);
            RightAlign(hintWidth);
            ImGui.TextColored(PoseKitUi.Good, hint);
            return;
        }

        var legendWidth = LegendWidth();
        PoseKitUi.SameLineIfFits(legendWidth + 16);
        RightAlign(legendWidth);
        DrawLegend();
    }

    private static void RightAlign(float width)
    {
        var x = ImGui.GetWindowContentRegionMax().X - width;
        if (ImGui.GetCursorPosX() < x)
            ImGui.SetCursorPosX(x);
    }

    private void DrawPairingStatus()
    {
        var state = plugin.PairingState;
        if (state.Active && state.Peer is { } peer)
            PoseKitUi.StatusDot(PoseKitUi.Good, $"PAIRED WITH {peer.Name.ToUpperInvariant()}");
        else if (state.PendingInvite != null)
            PoseKitUi.StatusDot(PoseKitUi.Info, "PAIRING INVITE WAITING");
        else if (state.OutgoingInvite != null)
            PoseKitUi.StatusDot(PoseKitUi.Accent, "INVITE SENT");
        else
            PoseKitUi.StatusDot(PoseKitUi.Muted, "NOT PAIRED");
    }

    private void DrawRail()
    {
        PoseKitUi.CardTitle("Pairing");
        PoseKitUi.CardTitleRule();
        PairingPanel.Draw(plugin);

        ImGui.Spacing();
        PoseKitUi.SectionHeader("Live Offset");
        PresetButtonsPanel.DrawOffsets(plugin);

        ImGui.Spacing();
        PoseKitUi.SectionHeader("Bone Align");
        BoneAlignPanel.Draw(plugin);
    }

    private void DrawPage()
    {
        switch (page)
        {
            case Page.Animations:
                PenumbraPosePanel.DrawToolbar(plugin);
                DrawScrollRegion("##PoseKitAnimationsScroll", () => PenumbraPosePanel.Draw(plugin));
                break;

            case Page.Presets:
                PresetButtonsPanel.DrawToolbar(plugin);
                DrawScrollRegion("##PoseKitPresetsScroll", () => PresetButtonsPanel.DrawPresets(plugin));
                break;
        }
    }

    private static void DrawScrollRegion(string id, Action draw)
    {
        if (ImGui.BeginChild(id, Vector2.Zero, false, ImGuiWindowFlags.None))
            draw();
        ImGui.EndChild();
    }

    private static readonly (Vector4 Color, string Label)[] LegendEntries =
    [
        (PoseKitUi.Accent, "Your pick"), (PoseKitUi.Info, "Partner's pick"),
        (PoseKitUi.Good, "Both ready"), (PoseKitUi.Bad, "Conflict"),
    ];

    private const float LegendGap = 12f;

    private static float LegendWidth()
    {
        var width = 0f;
        foreach (var (_, label) in LegendEntries)
            width += ImGui.CalcTextSize("●").X + 5f + ImGui.CalcTextSize(label).X;
        return width + LegendGap * (LegendEntries.Length - 1);
    }

    private static void DrawLegend()
    {
        ImGui.BeginGroup();
        for (var i = 0; i < LegendEntries.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, LegendGap);
            PoseKitUi.StatusDot(LegendEntries[i].Color, LegendEntries[i].Label);
        }
        ImGui.EndGroup();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What the highlight colors on animations and presets mean while paired.");
    }
}
