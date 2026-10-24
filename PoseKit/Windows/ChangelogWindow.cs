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
    public const string ChangelogId = "3.0.1.0";

    private static readonly (string Heading, string[] Items)[] Sections =
    [
        ("Quick Library", [
            "A small on-screen chip opens a compact animation library. By default it's shown while you're paired; Settings can hide it when solo, or never show it at all (/posekit lib still opens the library).",
            "Laid out like the main window, just smaller: your mods (collapsed), then each animation, then its roles and variants with small play buttons. Search and the Solo/Couple filter open what matches.",
            "It only shows what you can play right now: ground sits, dozes and emotes while standing, sit poses while seated, doze poses while lying down. It opens by itself when you sit or lie down (you can turn that off in Settings).",
            "The Adjust section has the gizmo and Bone Align (parts, gap, auto-align, quick turns, Align and Remember), so you rarely need the main window.",
            "Rescan your mods right from the library header.",
        ]),
        ("Favorites", [
            "Click the star next to any animation you can play, in the main window or the library, to make it a favorite.",
            "Favorites get their own section above your mods, ready to play without digging through long lists, each showing which mod it comes from.",
        ]),
        ("Posing", [
            "Doze anywhere: doze poses play where you stand, and others see them. Beds and chairs still place you on them.",
            "Gizmo: drag handles on your character to move or turn your offset.",
            "Bone Align turns you to face your partner's direction before lining you up, since couple animations are made with both characters facing the same way. Turn off \"Face partner's direction\" to keep your own facing.",
            "Nudging a remembered animation by hand saves the new placement to Bone Align's memory.",
        ]),
        ("Pairing", [
            "Target a player and pair with them straight from the library or the main window, or right-click a player in the world, in chat, in your party or friend list and choose \"Pair with PoseKit\".",
            "Invites you receive pop up as a notification you can accept right there.",
            "PoseKit's pairing tells no longer clutter your chat: they're hidden, and you get a short [PoseKit] line for what matters (invites, pairing, accepts, declines).",
            "The library pairs like the main window: your pick is queued with a single tell, your partner picks theirs, and both play. With Override on (next to Gizmo in the library), your second pick plays both. When your partner picks, the library shows their pick so you can play your part.",
            "Tells are spaced out so the game no longer drops them, and the library has an Unpair button on the \"Paired with\" line.",
        ]),
        ("Fixes", [
            "Mods saved by newer Penumbra versions could show options without a play button; they play again.",
            "Dropdowns no longer snap back open when you collapse them.",
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
