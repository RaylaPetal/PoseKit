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

        ImGui.Spacing();
        PoseKitUi.SectionHeader("Bone Align");
        DrawBoneAlign();
    }

    /// Self/Partner body-part pickers and the Align button — see Bones.BoneAlignService.
    private void DrawBoneAlign()
    {
        var configuration = plugin.Configuration;
        var labelWidth = ImGui.CalcTextSize("Partner").X + ImGui.GetStyle().ItemSpacing.X;

        // A remembered animation: say so, with what's remembered, and offer to forget it. The
        // dropdowns below were already filled from it by AutoAlignCoordinator.
        if (plugin.AutoAlign.CurrentEntry is { } known)
            DrawKnownAnimation(known);

        var self = configuration.BoneAlignSelf;
        if (DrawBodyPartCombo("Self", "##PoseKitBoneAlignSelf", labelWidth, ref self))
        {
            configuration.BoneAlignSelf = self;
            configuration.Save();
        }

        var partner = configuration.BoneAlignPartner;
        if (DrawBodyPartCombo("Partner", "##PoseKitBoneAlignPartner", labelWidth, ref partner))
        {
            configuration.BoneAlignPartner = partner;
            configuration.Save();
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Muted, "Gap");
        ImGui.SameLine(labelWidth + ImGui.GetStyle().WindowPadding.X);
        ImGui.SetNextItemWidth(-1);
        var gap = configuration.BoneAlignGap;
        if (ImGui.SliderFloat("##PoseKitBoneAlignGap", ref gap, 0f, 0.10f, "%.2fy"))
        {
            configuration.BoneAlignGap = gap;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How much room to leave between the two parts, so they meet instead of fusing.");

        var matchFacing = configuration.BoneAlignMatchFacing;
        if (ImGui.Checkbox("Match facing##PoseKitBoneAlignFacing", ref matchFacing))
        {
            configuration.BoneAlignMatchFacing = matchFacing;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Also turns you to the animation's intended facing. For couple animations made to be played\n" +
                             "standing on the same spot, it snaps to the facing that puts you both on that spot.\n" +
                             "Otherwise it turns you so the two parts face each other (e.g. penis into vagina, face toward crotch).\n" +
                             "Turn only, no tilt. Also flips you 180 degrees when the other way round would put\n" +
                             "your bodies inside each other, e.g. an animation made for facing the opposite way.");

        var autoAlign = configuration.AutoAlignFromMemory;
        if (ImGui.Checkbox("Auto-align from memory##PoseKitAutoAlign", ref autoAlign))
        {
            configuration.AutoAlignFromMemory = autoAlign;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each successful Align is remembered for the animation you're playing. Next time you play it\n" +
                             "near your partner (or a targeted player), PoseKit aligns you by itself with the same parts.\n" +
                             "Turning this off stops auto-aligning; Aligns are still remembered.");

        var align = plugin.BoneAlign;
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(align.IsAligning))
        {
            if (PoseKitUi.WideButton(align.IsAligning ? "Aligning...##PoseKitBoneAlign" : "Align##PoseKitBoneAlign"))
                align.Start(Bones.AlignRequest.FromConfiguration(configuration));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Resyncs emotes, watches both parts for a moment, then moves you so they meet.");

        if (align.Status.Length > 0)
            PoseKitUi.TextWrappedDisabled(align.Status);
    }

    /// The remembered alignment for the animation playing, as a small green-tinted panel: a
    /// "● Known animation" header with Forget on the right, then the parts and the facing/gap on
    /// their own short lines, so it reads cleanly in the narrow sidebar.
    private void DrawKnownAnimation(Bones.AlignmentEntry known)
    {
        const float pad = 8f;
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        // Content on the top channel, the panel behind it on the bottom one, drawn once its height is known.
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - pad * 2);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Good, "●");
        ImGui.SameLine(0, 5);
        ImGui.TextColored(PoseKitUi.Good, "Known animation");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{known.ModName} — {known.Option} ({known.Trigger})\nRemembered from your last successful Align on this animation.");

        const string forgetLabel = "Forget##PoseKitBoneAlignForget";
        var forgetWidth = ImGui.CalcTextSize("Forget").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(width - pad * 2 - forgetWidth); // relative to the group, which starts pad in
        if (ImGui.SmallButton(forgetLabel))
            plugin.AlignmentMemory.Forget(known.Key);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stop auto-aligning this animation until you align it manually again.");

        ImGui.TextUnformatted(Bones.BodyParts.DisplayName(known.Self));
        ImGui.SameLine(0, 6);
        ImGui.TextColored(PoseKitUi.Muted, "to");
        ImGui.SameLine(0, 6);
        ImGui.TextUnformatted(Bones.BodyParts.DisplayName(known.Partner));

        var facing = Bones.BoneAlignService.FacingLabel(known.Facing);
        PoseKitUi.TextWrappedDisabled($"{char.ToUpperInvariant(facing[0])}{facing[1..]} · {known.Gap:0.00}y gap");
        if (plugin.AutoAlign.Waiting.Length > 0)
            PoseKitUi.TextWrappedDisabled(plugin.AutoAlign.Waiting);

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + pad);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(PoseKitUi.Good with { W = 0.10f }), 6f);
        drawList.AddRect(start, end, ImGui.GetColorU32(PoseKitUi.Good with { W = 0.35f }), 6f);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(width, 0));
        ImGui.Spacing();
    }

    private static bool DrawBodyPartCombo(string label, string id, float labelWidth, ref Bones.BodyPart value)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Muted, label);
        ImGui.SameLine(labelWidth + ImGui.GetStyle().WindowPadding.X);
        ImGui.SetNextItemWidth(-1);

        var changed = false;
        if (ImGui.BeginCombo(id, Bones.BodyParts.DisplayName(value)))
        {
            foreach (var part in Bones.BodyParts.All)
            {
                if (ImGui.Selectable(Bones.BodyParts.DisplayName(part), part == value))
                {
                    value = part;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
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
