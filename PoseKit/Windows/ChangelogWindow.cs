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
    public const string ChangelogId = "3.1.0.0";

    private static readonly (string Heading, string[] Items)[] Sections =
    [
        ("Pairing without tells", [
            "Pairing no longer sends any tells. Invites, picks, offers and answers travel through PoseKit's own relay, so they arrive instantly, nothing is dropped for being sent too fast, and Do Not Disturb or your /r target are no longer affected.",
            "Messages between you and your partner are end-to-end encrypted: the relay can't read what you pick or play. It only sees that a character is online with PoseKit and when messages are sent.",
            "Both of you need this version to pair. If a partner on an older PoseKit invites you, their invite is hidden and you see a line asking them to update.",
        ]),
        ("Pairing for the first time", [
            "Stand near your partner, target them and press Pair (or right-click them and choose \"Pair with PoseKit\"). Their PoseKit only trusts an invite from the character standing next to them and targeting them, so nobody can pretend to be you.",
            "Pressing Pair or Accept targets your partner for you.",
        ]),
        ("Pairing again", [
            "Partners you've paired with are remembered. Pair again from the pairing panel from anywhere in the game, no targeting needed; they still accept as usual.",
            "Forget a partner in the same list. Pairing with them again then needs them nearby.",
            "If your partner is offline when you pick something, you're told right away instead of it silently going nowhere.",
            "Each pick now says what's happening in chat: queued and waiting, your partner's pick, playing together, or expired. The quick library shows your queued pick too.",
        ]),
        ("Presets", [
            "Fixed: a preset's anchor (spot, furniture or partner) was lost the next time PoseKit loaded. Presets saved before this update lost theirs; save them again to anchor them.",
            "The main window groups presets by the mod they play from, and each one says whether it's solo or a couple, where it lands (hover for what that means) and what it plays.",
            "In the quick library, each preset gets its own row saying the same.",
            "The quick library opens with every section closed each time, even ones you opened earlier in the session; a search still opens what matches.",
        ]),
        ("Support and Discord", [
            "The main window's header now has Ko-fi, Patreon and Discord buttons, and this window has a \"Join the Discord\" button below, for updates and help.",
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
