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
    public const string ChangelogId = "3.1.0.9";

    private static readonly (string Heading, string[] Items)[] Sections =
    [
        ("Never miss your partner", [
            "A short chime plays when someone invites you, accepts your invite, picks an animation, offers you a role or ends the pairing. Turn it off or pick another of the game's chat sounds in Settings > Pairing.",
            "The library button now says what's waiting: \"Pair request from ...\", \"... picked ...\", \"... wants to play ...\" or \"Waiting for ...\". Click it to answer.",
            "The library button also shows up for an incoming invite when you've hidden it while solo.",
        ]),
        ("Mod settings in the library", [
            "Options that play no animation, like an SFX toggle on a paddling or gagging animation, are now under \"Settings\" in each mod in the library, so you can switch sounds on without opening the main window.",
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
