using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// What's new, shown once after an update that ships a new changelog, then not again until the next
/// one. Seen when closed; /posekit changelog reopens it.
/// </summary>
public sealed class ChangelogWindow : Window, IDisposable
{
    /// Bump with each changelog worth showing. The window opens whenever the player's last seen ID differs.
    public const string ChangelogId = "3.0.2.0";

    private static readonly (string Heading, string[] Items)[] Sections =
    [
        ("Movement lock", [
            "While a pose PoseKit played is going, your movement keys no longer move you, so one stray key can't end a pose you spent time placing and aligning.",
            "Press Unlock (in the main window header or the quick library) or type /posekit unlock to move again. The pose keeps playing until you actually move.",
            "The lock lets go by itself when the pose ends, when you type an emote of your own, in combat, on zone change and on logout. Emotes you type yourself are never locked.",
            "Don't want it? Turn off \"Lock movement while posing\" in Settings.",
        ]),
        ("Freecam", [
            "Freecam moves the moment you press a key, instead of a second or two later.",
            "Movement eases in and out smoothly instead of jumping, and it's a bit slower so placing the camera is easier.",
        ]),
        ("Library tags", [
            "Every library animation gets a colored FxF, MxF or M/F x F chip. Click a chip to correct it; your fix follows the animation's files.",
            "Tags come from your own corrections, the body parts Bone Align recorded, the animation's name and the mod author's codes. While paired, PoseKit also notices which parts touch and suggests a tag in the chip's menu.",
            "The library filter gains FxF and MxF, sorting animations into Fits and a collapsed Doesn't fit.",
        ]),
        ("Playing together", [
            "A Recent list per partner (or solo) sits above your mods in the quick library.",
            "Scenes whose names state both roles get Play together: your partner gets the other role with Accept, Swap and Decline.",
            "While paired, animations play on your partner's spot facing their way, since couple animations share one origin. Bone Align still refines it afterwards.",
            "If your partner walks off or leaves their pose, you stay put instead of being dragged along.",
        ]),
        ("Playing", [
            "Playing an animation now makes it the one that plays: PoseKit turns off other options in the same mod that use the same files, and outranks other mods only when they really claim them. Your saved Penumbra settings never change.",
            "After a play, PoseKit checks what Penumbra is serving and tells you in chat if another mod won.",
            "Standing idles can be played too: they show up as Idle triggers and keep their offset until you move.",
            "Hold Shift (or Ctrl or Alt, in Settings) to make the gizmo rotate while Move is selected.",
        ]),
    ];

    private readonly Configuration configuration;

    public ChangelogWindow(Configuration configuration) : base("What's new in PoseKit###PoseKitChangelog")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(700, 900),
        };
        Size = new Vector2(460, 520);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.configuration = configuration;
    }

    public void Dispose() { }

    /// Whether this changelog hasn't been seen yet.
    public bool IsUnseen => configuration.LastSeenChangelog != ChangelogId;

    public void MarkSeen()
    {
        if (!IsUnseen) return;
        configuration.LastSeenChangelog = ChangelogId;
        configuration.Save();
    }

    public override void OnClose() => MarkSeen();

    public override void Draw()
    {
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        var version = typeof(ChangelogWindow).Assembly.GetName().Version;
        PoseKitUi.TextWrappedDisabled($"PoseKit {version}. Reopen this anytime with /posekit changelog.");

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.BeginChild("##PoseKitChangelogScroll", new Vector2(0, -footer), false, ImGuiWindowFlags.None))
        {
            foreach (var (heading, items) in Sections)
            {
                PoseKitUi.SectionHeader(heading);
                foreach (var item in items)
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextUnformatted(item);
                    ImGui.PopTextWrapPos();
                }
                ImGui.Spacing();
            }
        }
        ImGui.EndChild();

        if (PoseKitUi.WideButton("Got it##PoseKitChangelogClose"))
            IsOpen = false;
    }
}
