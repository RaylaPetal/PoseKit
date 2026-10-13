using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PoseKit.Library;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Windows;

/// <summary>
/// The quick library's seat and bed controls: placement status, snap mode, and Save for this seat or
/// bed.
/// </summary>
public static class SeatPanel
{
    private static string saveName = "";
    private static string? saveNameSource;

    /// Wraps a placed play's beforePlay so placement starts right as the play runs, which may be
    /// later than the click when it's queued for a partner.
    public static Action PlacedBeforePlay(Plugin plugin, FurnitureContextBase context, SeatReference seat, LibraryEntry entry,
        PoseTriggerHint trigger, Guid? collectionId, Action? beforePlay)
    {
        var group = entry.Group.IsImplicit ? "" : entry.Group.Name;
        var triggerText = PlayDispatch.TriggerText(trigger);
        var key = SeatSnapMemory.KeyFor(entry.Mod.ModDirectory, group, entry.Option.Name, triggerText, context.MemoryKind);

        return () =>
        {
            beforePlay?.Invoke();
            var mode = plugin.SeatSnapMemory.TryGet(key) ?? context.DefaultSnapMode(trigger);
            plugin.SeatPlacement.Begin(seat, key, mode,
                () => PlayDispatch.PlayOptionTrigger(plugin, entry.Mod, entry.Group, entry.Option, trigger, collectionId),
                (entry.Mod.ModDirectory, entry.Mod.ModName, group, entry.Option.Name, triggerText, trigger.PoseIdentifier));
        };
    }

    public static void Draw(Plugin plugin, FurnitureContextBase context)
    {
        var placement = plugin.SeatPlacement;

        if (context.Reference == null)
            PoseKitUi.TextWrappedDisabled($"Measuring the {context.Surface}...");

        if (placement.Status.Length > 0)
            PoseKitUi.TextWrappedDisabled(placement.Status);

        if (placement.IsPlaced)
        {
            var hips = placement.Mode == SnapMode.Hips;
            if (ImGui.RadioButton($"Hips to {context.Surface}##PoseKitSnapHips", hips) && !hips)
                placement.SetMode(SnapMode.Hips);
            ImGui.SameLine();
            if (ImGui.RadioButton("Position only##PoseKitSnapPosition", !hips) && hips)
                placement.SetMode(SnapMode.PositionOnly);
        }

        if (context.Reference is { } seat && PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer) is { } pose)
            DrawSave(plugin, seat, pose);

        ImGui.Spacing();
    }

    private static void DrawSave(Plugin plugin, SeatReference seat, PoseIdentifier pose)
    {
        // A new play gets a fresh default name.
        var link = plugin.LastPlayedPenumbraContext;
        var playing = link is { ModName.Length: > 0 } ? (link.OptionName is "" or "Default" ? link.ModName : link.OptionName) : pose.DisplayName;
        var source = $"{pose}|{playing}";
        if (saveNameSource != source)
        {
            saveNameSource = source;
            saveName = $"{playing} @ {seat.Label}";
        }

        var saveLabel = $"Save for this {seat.Surface}";
        ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - PoseKitUi.ButtonWidth(saveLabel) - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##PoseKitSeatPresetName", "Preset name", ref saveName, 64);
        ImGui.SameLine();
        using (ImRaii.Disabled(saveName.Trim().Length == 0))
        {
            if (ImGui.Button($"{saveLabel}##PoseKitSaveSeat"))
                Save(plugin, seat, pose, saveName.Trim());
        }
        var getOn = seat.Kind == ReferenceKind.Bed ? "lie down on" : "sit on";
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(seat.Furniture != null
                ? $"Saves this as a preset for any {seat.Label}, listed under Here when you {getOn} one."
                : $"Saves this as a preset for this spot, listed under Here when you {getOn} it again.");
    }

    /// Anchored to the seat or bed itself rather than where the game stood the character up, so the saved
    /// offset is what's left after placement (the hips snap and any edits).
    private static void Save(Plugin plugin, SeatReference seat, PoseIdentifier pose, string name)
    {
        var anchor = seat.Furniture is { } furniture
            ? PresetAnchor.FromFurniture(FurnitureAnchor.Capture(seat.Position, seat.Rotation, furniture))
            : PresetAnchor.FromSpot(new LocationAnchor { TerritoryType = seat.TerritoryType, Position = seat.Position, Rotation = seat.Rotation });

        var offset = plugin.PoseTrigger.GetOffsetForNewPreset();
        if (plugin.SeatPlacement.IsPlaced)
        {
            offset = PoseTrigger.Subtract(offset, plugin.SeatPlacement.PlacementOffset);
        }

        var saved = plugin.PresetManager.Save(name, pose, offset, plugin.LastPlayedPenumbraContext, anchor);
        if (saved == null) return;
        plugin.LoadedPreset = saved;
        Plugin.ChatGui.Print($"[PoseKit] Saved \"{name}\" for {seat.Label}.");
    }
}
