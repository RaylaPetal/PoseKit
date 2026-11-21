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
    public const string ChangelogId = "3.1.0.8";

    private static readonly (string Heading, string[] Items)[] Sections =
    [
        ("The right animation, every time", [
            "Sit, ground sit and doze animations now check what's actually playing and step to the right pose themselves. Some poses used to land one too early, so you saw the normal game animation and had to /cpose to the next one.",
            "Scenes with a role written without a dash, like \"(Dom-BedPose0/Sub BedPose1)\", are now recognized, so Play together offers your partner their role.",
        ]),
        ("Playing along with your partner", [
            "When your partner picks an animation, the \"picked\" section now shows the whole mod, not just the option they clicked. Scene mods that put each role in its own group (like a \"Bottom\" list and a \"Top\" toggle) offer your half first.",
            "In big animation packs, your partner's group comes first and the rest of the pack is under \"More from ...\", so nothing is out of reach.",
        ]),
        ("Sit anywhere", [
            "Chair-sit animations now play wherever you stand, the same way doze already does: no chair needed, and others see you sitting. Next to a chair you still sit on it.",
            "Sitting in the open puts you at chair height; use the gizmo or offsets to lower you onto a ledge, a rock or anything else.",
        ]),
        ("Quick library", [
            "While standing, the library lists everything you can play: ground sits, chair sits, dozes, idle stances and emotes. Chair-sit presets that aren't tied to furniture are listed too.",
            "While sitting or lying down, it still lists only sit poses or bed poses.",
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

        // Two button rows: Join the Discord, then Got it.
        var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y;
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

        if (PoseKitUi.WideButton("Join the Discord##PoseKitChangelogDiscord"))
            Dalamud.Utility.Util.OpenLink(SupportLinks.DiscordUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Updates and help\ndiscord.gg/87eMbV4FU9");
        if (PoseKitUi.WideButton("Got it##PoseKitChangelogClose"))
            IsOpen = false;
    }
}
