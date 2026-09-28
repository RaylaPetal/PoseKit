using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// Two-part layout. A header carries the title, the Animations/Presets page tabs (the only thing that
/// switches content) and the global tools (resync, freecam, settings), with a status line under it
/// (dependencies, pairing, color legend). Below it are two cards: the selected page, taking all
/// the width it can, and a fixed right rail that always shows Pairing, Live Offset and Bone Align
/// top to bottom, in the order a couple session uses them. The rail scrolls on its own; nothing
/// collapses or hides.
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

    /// Title and version, the page tabs beside them, and the global tools right-aligned — freecam
    /// tinted green while it's on, so its state is visible at a glance.
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
        var toolsWidth = PoseKitUi.ButtonWidth(resyncLabel) + PoseKitUi.ButtonWidth(freecamLabel) + PoseKitUi.ButtonWidth(settingsLabel) + spacing * 2;
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
    }

    /// Dependencies and pairing on the left, the pick-color legend on the right (wrapping under when
    /// the window is narrow). While freecam is on, its controls replace the legend, since they're what
    /// the player needs right then.
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

    /// Everything for the session, always visible, top to bottom: who you're paired with and what's
    /// picked, then fine-tuning your placement, then lining up body parts.
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
    /// their own short lines, so it reads cleanly in the narrow right rail.
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
        }
    }

    private static void DrawScrollRegion(string id, Action draw)
    {
        if (ImGui.BeginChild(id, Vector2.Zero, false, ImGuiWindowFlags.None))
            draw();
        ImGui.EndChild();
    }

    // The pick colors, in the order a couple round goes: yours, theirs, both, and a conflict.
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
