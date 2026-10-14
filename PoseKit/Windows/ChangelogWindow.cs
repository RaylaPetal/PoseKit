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
    public const string ChangelogId = "3.0.0.4";

    private static readonly (string Heading, string[] Items)[] Sections =
    [
        ("Quick Library", [
            "A small on-screen chip opens a compact animation library. It's always shown while you're paired; you can hide it when solo in Settings.",
            "Browse by Futa, Gay, Straight or Lesbian, then by act (Oral, Vaginal, Anal, Handjob & Fingering, Toys, Bondage…), or open Cuddles, Solo, Idles and Dancing. Cuddles and kisses that aren't sex scenes have their own Cuddles section.",
            "Animations are laid out like the main window: mod, then animation, then its roles and variants. Long names wrap, and counts are per animation.",
            "It only shows what you can play right now: ground sits, dozes and emotes while standing, sit poses while seated, doze poses while lying down. It opens by itself when you sit or lie down.",
            "Search stays inside the section you're in. Right-click an animation's title to change its acts and tags or hide it; its roles and variants follow along.",
            "Rescan and Reload categories buttons sit in the library header.",
            "The Adjust section has the gizmo and Bone Align (parts, gap, auto-align, quick turns, Align and Remember), so you rarely need the main window.",
            "Target a player and pair with them straight from the library.",
        ]),
        ("Categories", [
            "Animations are sorted automatically from their names and descriptions, each animation as a whole, so its roles and variants always stay together. Your own tags always win, and right-click > Hide removes toggles or animations you don't want.",
            "Anything that doesn't need a penis (bondage, devices, foot play, fingering, pussy play, anal toys) shows under both Straight and Lesbian.",
            "Every animation lands somewhere: nothing is left under Other. Styles like BDSM, Rough or Romantic are still searchable tags.",
            "Standalone Dom/Sub mods are recognised as couple animations.",
        ]),
        ("Posing", [
            "Doze anywhere: doze poses play where you stand, and others see them. Beds and chairs still place you on them.",
            "Gizmo: drag handles on your character to move or turn your offset.",
            "Bone Align turns you to face your partner's direction before lining you up, since couple animations are made with both characters facing the same way. Turn off \"Face partner's direction\" to keep your own facing.",
            "Nudging a remembered animation by hand saves the new placement to Bone Align's memory.",
        ]),
        ("Pairing", [
            "Right-click a player in the world, in chat, in your party or friend list and choose \"Pair with PoseKit\".",
            "When you pick one role of a couple animation, PoseKit can send your partner the other role (setting).",
            "PoseKit's pairing tells no longer clutter your chat: they're hidden, and you get a short [PoseKit] line for what matters (invites, offers, accepts, declines).",
            "Playing a couple animation from the library sends fewer tells, and with override on for both of you, both halves start right away.",
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
