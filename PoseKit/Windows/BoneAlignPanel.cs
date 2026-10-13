using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace PoseKit.Windows;

/// <summary>
/// Bone Align controls, shared by the main window and the quick library: the remembered-animation card,
/// body parts and gap, auto-align, quick turns, Align and Remember.
/// </summary>
public static class BoneAlignPanel
{
    /// <param name="compact">For the quick library: parts and gap share one row, and Align and Remember
    /// another, so every setting stays visible in a narrow window.</param>
    public static void Draw(Plugin plugin, bool compact = false)
    {
        var configuration = plugin.Configuration;

        if (compact)
        {
            DrawCompactPartsRow(configuration);
            DrawAutoAlign(configuration);
            ImGui.SameLine();
            DrawFaceSameWay(configuration);
            PresetButtonsPanel.DrawQuickTurns(plugin);

            var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            DrawAlignButton(plugin, half);
            ImGui.SameLine();
            DrawRememberButton(plugin, half);
        }
        else
        {
            if (plugin.AutoAlign.CurrentEntry is { } known)
                DrawKnownAnimation(plugin, known);

            var labelWidth = ImGui.CalcTextSize("Partner").X + ImGui.GetStyle().ItemSpacing.X;
            var self = configuration.BoneAlignSelf;
            if (DrawBodyPartCombo("Self", "##PoseKitBoneAlignSelf", labelWidth, ref self))
                SetSelf(configuration, self);
            var partner = configuration.BoneAlignPartner;
            if (DrawBodyPartCombo("Partner", "##PoseKitBoneAlignPartner", labelWidth, ref partner))
                SetPartner(configuration, partner);

            var rowStart = ImGui.GetCursorPosX();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(PoseKitUi.Muted, "Gap");
            ImGui.SameLine(rowStart + labelWidth);
            DrawGapSlider(configuration, -1);

            DrawAutoAlign(configuration);
            DrawFaceSameWay(configuration);
            PresetButtonsPanel.DrawQuickTurns(plugin);
            DrawAlignButton(plugin, ImGui.GetContentRegionAvail().X);
            DrawRememberButton(plugin, ImGui.GetContentRegionAvail().X);
        }

        if (plugin.BoneAlign.Status.Length > 0)
            PoseKitUi.TextWrappedDisabled(plugin.BoneAlign.Status);

        // Below the buttons in the library, so it doesn't push them down when it appears.
        if (compact && plugin.AutoAlign.CurrentEntry is { } remembered)
        {
            ImGui.Spacing();
            DrawKnownAnimation(plugin, remembered);
        }
    }

    /// Self, Partner and Gap as three equal columns, each label above its field, so the fields get the
    /// whole width.
    private static void DrawCompactPartsRow(Configuration configuration)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var left = ImGui.GetCursorPosX();
        var field = MathF.Max(40f, (ImGui.GetContentRegionAvail().X - spacing * 2) / 3);
        float Column(int i) => left + i * (field + spacing);

        ImGui.TextColored(PoseKitUi.Muted, "Self");
        ImGui.SameLine(Column(1));
        ImGui.TextColored(PoseKitUi.Muted, "Partner");
        ImGui.SameLine(Column(2));
        ImGui.TextColored(PoseKitUi.Muted, "Gap");

        var self = configuration.BoneAlignSelf;
        if (DrawPartCombo("##PoseKitBoneAlignSelf", field, ref self))
            SetSelf(configuration, self);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Your body part that should meet your partner's.");

        ImGui.SameLine(Column(1));
        var partner = configuration.BoneAlignPartner;
        if (DrawPartCombo("##PoseKitBoneAlignPartner", field, ref partner))
            SetPartner(configuration, partner);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Your partner's body part it should meet.");

        ImGui.SameLine(Column(2));
        DrawGapSlider(configuration, field);
    }

    private static void SetSelf(Configuration configuration, Bones.BodyPart part)
    {
        configuration.BoneAlignSelf = part;
        configuration.Save();
    }

    private static void SetPartner(Configuration configuration, Bones.BodyPart part)
    {
        configuration.BoneAlignPartner = part;
        configuration.Save();
    }

    private static void DrawGapSlider(Configuration configuration, float width)
    {
        ImGui.SetNextItemWidth(width);
        var gap = configuration.BoneAlignGap;
        if (ImGui.SliderFloat("##PoseKitBoneAlignGap", ref gap, 0f, 0.10f, "%.2fy"))
        {
            configuration.BoneAlignGap = gap;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How much room to leave between the two parts, so they meet instead of fusing.");
    }

    private static void DrawAutoAlign(Configuration configuration)
    {
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
                             "Nudging a remembered animation by hand (sliders or gizmo) saves the new placement too,\n" +
                             "a moment after you stop.\n" +
                             "Turning this off stops auto-aligning; Aligns are still remembered.");
    }

    private static void DrawAlignButton(Plugin plugin, float width)
    {
        var align = plugin.BoneAlign;
        using (ImRaii.Disabled(align.IsAligning))
        {
            if (ImGui.Button(align.IsAligning ? "Aligning...##PoseKitBoneAlign" : "Align##PoseKitBoneAlign", new Vector2(width, 0)))
                align.Start(Bones.AlignRequest.FromConfiguration(plugin.Configuration));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.Configuration.BoneAlignFaceSameWay
                ? "Turns you to face your partner's way, resyncs emotes, watches both parts for a moment,\n" +
                  "then moves you so they meet."
                : "Resyncs emotes, watches both parts for a moment, then moves you so they meet.\n" +
                  "Keeps your facing: turn first with the buttons above or the live offset.");
    }

    private static void DrawFaceSameWay(Configuration configuration)
    {
        var faceSameWay = configuration.BoneAlignFaceSameWay;
        if (ImGui.Checkbox("Face partner's direction##PoseKitBoneAlignFaceSameWay", ref faceSameWay))
        {
            configuration.BoneAlignFaceSameWay = faceSameWay;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Align first turns you to face the same way as your partner, since couple animations\n" +
                             "are made with both characters facing one direction. Turn it off to keep your facing.");
    }

    private static void DrawRememberButton(Plugin plugin, float width)
    {
        var align = plugin.BoneAlign;
        var hasKey = plugin.CurrentPlayContext != null;
        var rememberLabel = plugin.AutoAlign.CurrentEntry != null ? "Update memory##PoseKitBoneAlignRemember" : "Remember##PoseKitBoneAlignRemember";
        using (ImRaii.Disabled(align.IsAligning || !hasKey))
        {
            if (ImGui.Button(rememberLabel, new Vector2(width, 0)))
                align.Start(Bones.AlignRequest.ForMeasure(plugin.Configuration));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasKey
                ? "Saves how you're placed right now, without moving you: your facing relative to your partner and\n" +
                  "where the parts meet. Nudges to an animation that's already remembered are saved automatically."
                : "Play an animation from the Animations page (or one PoseKit can identify) to remember it.");
    }

    /// The remembered alignment for the playing animation, with a Forget button.
    private static void DrawKnownAnimation(Plugin plugin, Bones.AlignmentEntry known)
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

    /// Label then a full-width combo, lined up at labelWidth from where the row starts.
    private static bool DrawBodyPartCombo(string label, string id, float labelWidth, ref Bones.BodyPart value)
    {
        var rowStart = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Muted, label);
        ImGui.SameLine(rowStart + labelWidth);
        return DrawPartCombo(id, -1, ref value);
    }

    private static bool DrawPartCombo(string id, float width, ref Bones.BodyPart value)
    {
        ImGui.SetNextItemWidth(width);
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
}
