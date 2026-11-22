using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;

namespace PoseKit.Windows;

/// <summary>
/// The small on-screen button that toggles the quick library, with a dot for the pairing state. Drag
/// it by its grip. Hidden in cutscenes and GPose like every PoseKit window.
/// </summary>
public sealed class LibraryChipWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Action toggleLibrary;

    public LibraryChipWindow(Plugin plugin, Action toggleLibrary)
        : base("PoseKit Library Chip###PoseKitLibraryChip",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin = plugin;
        this.toggleLibrary = toggleLibrary;

        IsOpen = true;
        // Only the settings toggle hides it (and only when solo); Escape and the close button would lose it.
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;

        Position = new Vector2(80, 320);
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    // Wide enough for "Pair request from" and a full name; longer animation names are cut.
    private const float StatusMaxWidth = 240f;

    /// Shown while paired by default, where the library is how picks get queued, and while an invite waits so
    /// it can be answered; "never show" overrides both.
    public override bool DrawConditions() =>
        !plugin.Configuration.NeverShowLibraryChip &&
        (plugin.Configuration.ShowLibraryChip || plugin.PairingState.Active || plugin.PairingState.PendingInvite != null);

    public override void Draw()
    {
        using var theme = PoseKitUi.PushTheme();

        // A plain glyph, so dragging it moves the window.
        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            ImGui.TextColored(PoseKitUi.Muted, FontAwesomeIcon.GripVertical.ToIconString());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drag to move");

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.BookOpen, "Library"))
            toggleLibrary();

        if (PendingStatus() is { } status)
            DrawStatus(status.Text, status.NeedsAnswer);
        else
            DrawPairingDot();
    }

    /// The most pressing pairing item, in words, so it's noticed without opening the library or reading chat.
    /// Only the first three wait on this player.
    private (string Text, bool NeedsAnswer)? PendingStatus()
    {
        var state = plugin.PairingState;
        if (state.PendingInvite is { } invite)
            return ($"Pair request from {invite.Sender.Name}", true);
        if (!state.Active || state.Peer is not { } peer)
            return state.OutgoingInvite is { } outgoing ? ($"Invited {outgoing.Target.Name}", false) : null;

        if (plugin.CoupleRelayInbox.Pending is { } relay)
            return ($"{relay.Sender.Name} wants to play {relay.Name}", true);
        var queue = plugin.CoupleQueueService;
        if (queue.PartnerSelectionName is { } theirs && queue.QueuedSelectionName == null)
            return ($"{peer.Name} picked {theirs}", true);
        if (queue.QueuedSelectionName != null)
            return ($"Waiting for {peer.Name}", false);
        return null;
    }

    /// Clickable like the button, since it's where the eye lands.
    private void DrawStatus(string text, bool needsAnswer)
    {
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        var shown = Shorten(text, StatusMaxWidth);
        ImGui.TextColored(needsAnswer ? PoseKitUi.Info : PoseKitUi.Muted, shown);
        if (ImGui.IsItemClicked())
            toggleLibrary();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(needsAnswer ? $"{text}\nClick to answer in the library." : text);
    }

    private static string Shorten(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        const string ellipsis = "…";
        var end = text.Length;
        while (end > 1 && ImGui.CalcTextSize(text[..end] + ellipsis).X > maxWidth) end--;
        return text[..end].TrimEnd() + ellipsis;
    }

    private void DrawPairingDot()
    {
        var state = plugin.PairingState;
        (Vector4 Color, string Tooltip)? dot =
            state.PendingInvite is { } pending ? (PoseKitUi.Info, $"{pending.Sender} wants to pair — open the library to answer.")
            : state.Active && state.Peer is { } peer ? (PoseKitUi.Good, $"Paired with {peer}")
            : state.OutgoingInvite is { } outgoing ? (PoseKitUi.Muted, $"Waiting for {outgoing.Target} to accept...")
            : null;
        if (dot is not { } d) return;

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(d.Color, "●");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(d.Tooltip);
    }
}
