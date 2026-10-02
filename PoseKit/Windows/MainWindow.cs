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
        DrawBoneAlign();
    }

    private void DrawBoneAlign()
    {
        var configuration = plugin.Configuration;
        var labelWidth = ImGui.CalcTextSize("Partner").X + ImGui.GetStyle().ItemSpacing.X;

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

        var autoAlign = configuration.AutoAlignFromMemory;
        if (ImGui.Checkbox("Auto-align from memory##PoseKitAutoAlign", ref autoAlign))
        {
            configuration.AutoAlignFromMemory = autoAlign;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each successful Align is remembered for the animation you're playing: your facing relative to\n" +
                             "your partner and where the parts meet. Next time you play it near your partner (or a targeted\n" +
                             "player), PoseKit turns and aligns you by itself.\n" +
                             "Turning this off stops auto-aligning; Aligns are still remembered.");

        var align = plugin.BoneAlign;
        DrawQuickTurns(align);

        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(align.IsAligning))
        {
            if (PoseKitUi.WideButton(align.IsAligning ? "Aligning...##PoseKitBoneAlign" : "Align##PoseKitBoneAlign"))
                align.Start(Bones.AlignRequest.FromConfiguration(configuration));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Resyncs emotes, watches both parts for a moment, then moves you so they meet.\n" +
                             "Keeps your facing: turn first with the buttons above or the live offset.");

        var hasKey = plugin.CurrentPlayContext != null;
        var rememberLabel = plugin.AutoAlign.CurrentEntry != null ? "Update memory##PoseKitBoneAlignRemember" : "Remember##PoseKitBoneAlignRemember";
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(align.IsAligning || !hasKey))
        {
            if (PoseKitUi.WideButton(rememberLabel))
                align.Start(Bones.AlignRequest.ForMeasure(configuration));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasKey
                ? "Saves how you're placed right now, without moving you: your facing relative to your partner and\n" +
                  "where the parts meet. Use it after nudging the live offset by hand."
                : "Play an animation from the Animations page (or one PoseKit can identify) to remember it.");

        if (align.Status.Length > 0)
            PoseKitUi.TextWrappedDisabled(align.Status);
    }

    private static readonly float[] QuickTurnDegrees = [-90f, 180f, 90f];

    /// -90 / 180 / +90 turns around the Self part, added to the bone-align correction.
    private void DrawQuickTurns(Bones.BoneAlignService align)
    {
        var posing = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer) != null;
        var rotationAvailable = plugin.OffsetEngine.RotationHookResolved;
        var why = !rotationAvailable ? "Rotation offset unavailable — hook didn't resolve this game version."
            : !posing ? "Start a pose or emote first."
            : align.IsAligning ? "Wait for the current alignment to finish."
            : "";

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = (ImGui.GetContentRegionAvail().X - spacing * (QuickTurnDegrees.Length - 1)) / QuickTurnDegrees.Length;
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(why.Length > 0))
        {
            for (var i = 0; i < QuickTurnDegrees.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                var degrees = QuickTurnDegrees[i];
                var label = MathF.Abs(degrees) >= 180f ? $"{MathF.Abs(degrees):0}°" : $"{degrees:+0;-0}°";
                if (ImGui.Button($"{label}##PoseKitQuickTurn{i}", new Vector2(buttonWidth, 0)))
                    align.QuickTurn(degrees);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(why.Length > 0 ? why : $"Turn {MathF.Abs(degrees):0} degrees around your {Bones.BodyParts.DisplayName(plugin.Configuration.BoneAlignSelf).ToLowerInvariant()}.");
            }
        }
        if (!rotationAvailable)
            PoseKitUi.TextWrappedDisabled(why);
    }

    /// The remembered alignment for the playing animation, with a Forget button.
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
            ImGui.SetTooltip($"{known.ModName} — {known.Option} ({known.Trigger})\nRemembered from your last successful Align or Remember on this animation.");

        const string forgetLabel = "Forget##PoseKitBoneAlignForget";
        var forgetWidth = ImGui.CalcTextSize("Forget").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(width - pad * 2 - forgetWidth); // relative to the group, which starts pad in
        if (ImGui.SmallButton(forgetLabel))
            plugin.AlignmentMemory.Forget(known.Key);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stop auto-aligning this animation until you align or remember it again.");

        ImGui.TextUnformatted(Bones.BodyParts.DisplayName(known.Self));
        ImGui.SameLine(0, 6);
        ImGui.TextColored(PoseKitUi.Muted, "to");
        ImGui.SameLine(0, 6);
        ImGui.TextUnformatted(Bones.BodyParts.DisplayName(known.Partner));

        var facing = known.RelativeYaw is { } yaw ? $"Facing {Bones.BoneAlignService.FacingText(yaw)}" : "Facing not saved";
        var contact = known.ContactOffset is { } offset ? $"{offset.ToVector3().Length():0.00}y contact" : $"{known.Gap:0.00}y gap";
        PoseKitUi.TextWrappedDisabled($"{facing} · {contact}");
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
