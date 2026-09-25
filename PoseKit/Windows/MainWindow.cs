using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// Dashboard layout: a header (title, dependency/pairing status, global actions), then three cards —
/// a sidebar (page navigation, character tools and the live offset editor, all always available),
/// the selected page, and the Pairing panel — and a color legend footer. Below ThreeColumnMinWidth
/// the Pairing column folds into a sidebar page instead, so the window still works when shrunk.
/// </summary>
public class MainWindow : Window, IDisposable
{
    private enum Page { Animations, Presets, Pairing }

    private const float ThreeColumnMinWidth = 860f;
    private const float SidebarWidth = 250f;
    private const float PairingColumnWidth = 280f;

    private readonly Plugin plugin;
    private Page page = Page.Animations;

    public MainWindow(Plugin plugin)
        : base($"PoseKit v{Plugin.Version}###MainWindow")
    {
        // The window itself stays fixed; each card scrolls on its own.
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(1600, 1200)
        };

        Size = new Vector2(1000, 660);
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

        var avail = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var bodyHeight = Math.Max(120f, avail.Y - ImGui.GetFrameHeightWithSpacing());
        var threeColumn = avail.X >= ThreeColumnMinWidth;
        if (threeColumn && page == Page.Pairing)
            page = Page.Animations;

        if (PoseKitUi.BeginCard("##PoseKitSidebar", new Vector2(SidebarWidth, bodyHeight)))
            DrawSidebar(threeColumn);
        PoseKitUi.EndCard();

        ImGui.SameLine();
        var centerWidth = avail.X - SidebarWidth - spacing - (threeColumn ? PairingColumnWidth + spacing : 0f);
        if (PoseKitUi.BeginCard("##PoseKitContent", new Vector2(centerWidth, bodyHeight),
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            DrawPage();
        PoseKitUi.EndCard();

        if (threeColumn)
        {
            ImGui.SameLine();
            if (PoseKitUi.BeginCard("##PoseKitPairing", new Vector2(PairingColumnWidth, bodyHeight)))
            {
                PoseKitUi.CardTitle("Pairing");
                PoseKitUi.CardTitleRule();
                PairingPanel.Draw(plugin);
            }
            PoseKitUi.EndCard();
        }

        DrawLegend();
    }

    private void DrawHeader()
    {
        ImGui.SetWindowFontScale(1.3f);
        ImGui.TextColored(PoseKitUi.Accent, "P O S E K I T");
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine(0, 10);
        ImGui.TextColored(PoseKitUi.Muted, $"v{Plugin.Version}");

        // Rescan lives on the Animations page's title row, next to the mod count it refreshes.
        const string settingsLabel = "Settings##PoseKitHeaderSettings";
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - PoseKitUi.ButtonWidth(settingsLabel));
        if (ImGui.Button(settingsLabel))
            plugin.ToggleConfigUi();

        PoseKitUi.DrawDependencyStatus(plugin);
        ImGui.SameLine(0, 16);
        DrawPairingStatus();
        ImGui.Spacing();
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

    private void DrawSidebar(bool threeColumn)
    {
        PoseKitUi.CardTitle("Library");
        PoseKitUi.CardTitleRule();

        if (PoseKitUi.NavItem("Animations", page == Page.Animations, plugin.DiscoveredPoses.Count.ToString()))
            page = Page.Animations;
        if (PoseKitUi.NavItem("Presets", page == Page.Presets, plugin.PresetManager.Presets.Count.ToString()))
            page = Page.Presets;
        if (!threeColumn)
        {
            // The Pairing column is hidden at this width, so flag anything waiting on the player (an
            // incoming couple preset or pairing invite) right on its nav row instead.
            var needsAttention = plugin.CoupleRelayInbox.PresetName != null || plugin.PairingState.PendingInvite != null;
            var badge = needsAttention ? "!" : plugin.PairingState.Active ? "●" : null;
            if (PoseKitUi.NavItem("Pairing", page == Page.Pairing, badge, needsAttention ? PoseKitUi.Info : PoseKitUi.Good))
                page = Page.Pairing;
        }

        ImGui.Spacing();
        PoseKitUi.SectionHeader("Character Tools");

        if (PoseKitUi.WideButton("Resync nearby emotes"))
            plugin.EmoteSync.Sync();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Resets all nearby rendered players together on your client.");

        if (PoseKitUi.WideButton(plugin.FreeCam.Enabled ? "Disable freecam" : "Enable freecam"))
            plugin.FreeCam.Toggle();
        PoseKitUi.TextWrappedDisabled(plugin.FreeCam.Status);
        if (plugin.FreeCam.Enabled)
            PoseKitUi.TextWrappedDisabled("WASD move | E/Q up/down | right-drag look | /posekit tfc exits");

        ImGui.Spacing();
        PoseKitUi.SectionHeader("Live Offset");
        PresetButtonsPanel.DrawOffsets(plugin);
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

            case Page.Pairing:
                PoseKitUi.CardTitle("Pairing");
                PoseKitUi.CardTitleRule();
                DrawScrollRegion("##PoseKitPairingScroll", () => PairingPanel.Draw(plugin));
                break;
        }
    }

    private static void DrawScrollRegion(string id, Action draw)
    {
        if (ImGui.BeginChild(id, Vector2.Zero, false, ImGuiWindowFlags.None))
            draw();
        ImGui.EndChild();
    }

    private static void DrawLegend()
    {
        ImGui.TextColored(PoseKitUi.Muted, "What do the colors mean?");
        ImGui.SameLine(0, 14);
        PoseKitUi.StatusDot(PoseKitUi.Accent, "Your pick");
        ImGui.SameLine(0, 14);
        PoseKitUi.StatusDot(PoseKitUi.Info, "Partner's pick");
        ImGui.SameLine(0, 14);
        PoseKitUi.StatusDot(PoseKitUi.Good, "Both ready");
        ImGui.SameLine(0, 14);
        PoseKitUi.StatusDot(PoseKitUi.Bad, "Conflict");
    }
}
