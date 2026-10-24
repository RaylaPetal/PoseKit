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

    /// Shown while paired by default, where the library is how picks get queued; "never show" overrides that.
    public override bool DrawConditions() =>
        !plugin.Configuration.NeverShowLibraryChip &&
        (plugin.Configuration.ShowLibraryChip || plugin.PairingState.Active);

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

        DrawPairingDot();
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
