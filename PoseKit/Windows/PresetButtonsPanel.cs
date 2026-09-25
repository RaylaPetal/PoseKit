using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility.Raii;
using PoseKit.Furniture;
using PoseKit.Presets;

namespace PoseKit.Windows;

/// <summary>Live-offset and saved-preset views. They share state but are drawn in separate main
/// tabs so editing a pose and browsing the preset library each have room to breathe. Pairing itself
/// lives in PairingPanel (Animations tab) — presets don't name a partner; see DrawPresetEntry.</summary>
public static class PresetButtonsPanel
{
    private enum AnchorMode { None, Spot, Furniture }

    private static readonly FurnitureScanner furnitureScanner = new();

    private static string newPresetName = "";
    private static AnchorMode anchorMode = AnchorMode.None;
    private static int selectedFurnitureIndex = -1;
    private static bool includePartner;

    public static void DrawOffsets(Plugin plugin)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var currentPose = PoseIdentifier.FromCharacter(localPlayer);
        PoseKitUi.SectionHeader("Live Offset");
        PoseKitUi.TextWrappedDisabled(currentPose?.DisplayName ?? "Not currently in a pose/emote loop.");

        using (ImRaii.Disabled(currentPose is null))
        {
            var offset = plugin.OffsetEngine.DesiredOffset;
            var changed = false;

            changed |= PoseKitUi.AxisDragFloat("OffsetX", "Left / Right", ref offset.Position.X);
            changed |= PoseKitUi.AxisDragFloat("OffsetY", "Height", ref offset.Position.Y);
            changed |= PoseKitUi.AxisDragFloat("OffsetZ", "Forward / Backward", ref offset.Position.Z);

            if (plugin.OffsetEngine.RotationHookResolved)
            {
                var degrees = offset.Rotation * (180f / MathF.PI);
                if (PoseKitUi.AxisDragFloat("Rotation", "Rotation (degrees)", ref degrees, 1f))
                {
                    degrees %= 360f;
                    if (degrees < 0) degrees += 360f;
                    offset.Rotation = degrees * (MathF.PI / 180f);
                    changed = true;
                }
            }
            else
            {
                PoseKitUi.TextWrappedDisabled("Rotation offset unavailable — hook didn't resolve this game version.");
            }

            if (changed)
                plugin.PoseTrigger.ApplyManualOffset(offset);
        }

        // Always available, regardless of current pose — this is the manual escape hatch for a
        // stuck offset, so it can't be hidden behind the very state that made it hard to fix.
        if (ImGui.Button("Reset##PoseKitOffsetReset"))
        {
            plugin.PoseTrigger.ClearOffset(localPlayer);
            plugin.LoadedPreset = null;
            plugin.LastPlayedPenumbraContext = null;
        }

        if (currentPose is { } pose && plugin.LoadedPreset is { } loaded && loaded.Pose == pose)
        {
            ImGui.SameLine();
            if (ImGui.Button("Update preset##PoseKitUpdatePreset"))
            {
                // A partner-anchored preset's live offset has the partner correction folded in —
                // saving that back would double-apply it on the next replay, so save the tracked base
                // (the preset's own offset plus any manual nudge) instead. See partner-anchor's spec.
                var offset = loaded.Anchor?.Partner != null && plugin.PoseTrigger.TryGetTrackedBaseOffset(out var trackedBase)
                    ? trackedBase
                    : plugin.OffsetEngine.DesiredOffset;
                plugin.PresetManager.Update(loaded, offset);
            }
        }
    }

    public static void DrawPresets(Plugin plugin)
    {
        var currentPose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);

        PoseKitUi.SectionHeader("Save Current Offset");
        PoseKitUi.TextWrappedDisabled(currentPose?.DisplayName ?? "Start a pose or animation before saving a preset.");

        if (currentPose is { } pose)
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            // A couple save is positioned relative to the partner instead of a spot/furniture — the
            // partner is the root on replay, see PartnerAnchor — so the spot/furniture choice doesn't
            // apply to it at all (and isn't offered below).
            var savingCouple = includePartner && plugin.PairingState.Active;
            var effectiveAnchorMode = savingCouple ? AnchorMode.None : anchorMode;
            var nearbyFurniture = effectiveAnchorMode == AnchorMode.Furniture ? furnitureScanner.ScanNearby(localPlayer) : null;

            ImGui.SetNextItemWidth(150);
            ImGui.InputTextWithHint("##PoseKitPresetName", "Preset name", ref newPresetName, 64);
            ImGui.SameLine();

            var furnitureValid = effectiveAnchorMode != AnchorMode.Furniture ||
                                  (nearbyFurniture is { Count: > 0 } && selectedFurnitureIndex >= 0 && selectedFurnitureIndex < nearbyFurniture.Count);
            // Being in a pose (currentPose above) is already the real precondition for "there's
            // something to save" — gating on HasAppliedOffset too meant a legitimate all-zero offset
            // (the default, or right after clicking Reset) made Save unavailable, which broke
            // anchor-only presets: the whole point of those is the anchor correction with no extra
            // nudge on top.
            var canSave = newPresetName.Trim().Length > 0 && furnitureValid;

            using (ImRaii.Disabled(!canSave))
            {
                if (ImGui.Button("Save as preset##PoseKitSavePreset"))
                {
                    PresetAnchor? anchor = effectiveAnchorMode switch
                    {
                        AnchorMode.Spot when localPlayer != null =>
                            PresetAnchor.FromSpot(LocationAnchor.Capture(localPlayer, Plugin.ClientState.TerritoryType)),
                        AnchorMode.Furniture when localPlayer != null && nearbyFurniture is { } list && selectedFurnitureIndex < list.Count =>
                            PresetAnchor.FromFurniture(FurnitureAnchor.Capture(localPlayer, list[selectedFurnitureIndex])),
                        _ => null,
                    };

                    var name = newPresetName.Trim();
                    if (savingCouple)
                    {
                        // Captured here, at click time, from both characters' actual transforms — the
                        // same instant this side's own offset is captured — rather than when the
                        // partner's reply arrives. No anchor hint goes to the partner (anchor stays
                        // partner-kind, which the capture request encodes as "none"), so their half
                        // is stored unanchored: they're the root.
                        if (plugin.PairingState.Peer is { } peer && localPlayer != null &&
                            PartnerAnchor.TryFindLive(peer) is { } partnerCharacter)
                        {
                            anchor = PresetAnchor.FromPartner(PartnerAnchor.Capture(localPlayer, partnerCharacter, peer));
                        }
                        else
                        {
                            Plugin.ChatGui.PrintError("[PoseKit] Couldn't find your partner nearby — saved without positioning " +
                                                      "this preset relative to them.");
                        }

                        // Asynchronous: captures the partner's own current state over a /tell
                        // request/reply and completes the save once it arrives (or times out) — see
                        // CouplePresetCaptureService. LoadedPreset is picked up via its Saved event
                        // rather than set here, since the save doesn't happen synchronously.
                        plugin.CouplePresetCaptureService.RequestAndSave(name, pose, plugin.OffsetEngine.DesiredOffset,
                            plugin.LastPlayedPenumbraContext, anchor);
                    }
                    else
                    {
                        var saved = plugin.PresetManager.Save(name, pose, plugin.OffsetEngine.DesiredOffset,
                            plugin.LastPlayedPenumbraContext, anchor);
                        plugin.LoadedPreset = saved;
                    }

                    newPresetName = "";
                }
            }

            if (plugin.PairingState.Active)
            {
                ImGui.Checkbox("Include partner##PoseKitIncludePartner", ref includePartner);
                PoseKitUi.TextWrappedDisabled("Captures your partner's current pose/offset/mod too, saved only in your own " +
                                               "library, and remembers where you're standing relative to them. Playing it " +
                                               "later relays their half to them for accept/deny: they stay put, and you're " +
                                               "positioned around them wherever you both are.");
            }

            if (savingCouple)
            {
                var partnerName = plugin.PairingState.Peer?.Name ?? "your partner";
                PoseKitUi.TextWrappedDisabled($"Positioned relative to {partnerName}.");
            }
            else
                DrawAnchorChoice(localPlayer, nearbyFurniture);
        }

        PoseKitUi.SectionHeader("Preset Library");

        var groups = plugin.PresetManager.Presets.GroupBy(p => p.Pose);
        if (!groups.Any())
        {
            ImGui.TextDisabled("No saved presets yet.");
            return;
        }

        foreach (var group in groups)
        {
            if (!ImGui.CollapsingHeader($"{group.Key.DisplayName}##PoseKitPresetGroup{group.Key.GetHashCode()}",
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.Indent();
            foreach (var namedPose in group.ToList())
                DrawPresetEntry(plugin, namedPose);
            ImGui.Unindent();
        }
    }

    /// The solo save's spot/furniture anchor picker — not shown for a couple save, which is always
    /// positioned relative to the partner instead (see DrawPresets).
    private static void DrawAnchorChoice(IPlayerCharacter? localPlayer, List<NearbyFurniture>? nearbyFurniture)
    {
        var mode = (int)anchorMode;
        ImGui.RadioButton("No anchor##PoseKitAnchorNone", ref mode, (int)AnchorMode.None);
        ImGui.SameLine();
        ImGui.RadioButton("Anchor to current spot##PoseKitAnchorSpot", ref mode, (int)AnchorMode.Spot);
        ImGui.SameLine();
        ImGui.RadioButton("Anchor to furniture##PoseKitAnchorFurniture", ref mode, (int)AnchorMode.Furniture);
        anchorMode = (AnchorMode)mode;

        if (anchorMode == AnchorMode.Spot)
        {
            PoseKitUi.TextWrappedDisabled("Corrects the offset on replay so the character lands back in this exact " +
                                           "world spot and facing, not just the same pose relative to wherever you are then.");
        }
        else if (anchorMode == AnchorMode.Furniture)
        {
            if (nearbyFurniture is { Count: > 0 } unsorted && localPlayer != null)
            {
                // Nearest first — ScanNearby already limits to nearby items, this just orders
                // them by distance so the closest (most likely "the one you're on") sorts to the
                // top of the dropdown instead of object-array order.
                var list = unsorted.OrderBy(f => Vector3.Distance(f.Position, localPlayer.Position)).ToList();
                if (selectedFurnitureIndex < 0 || selectedFurnitureIndex >= list.Count) selectedFurnitureIndex = 0;

                string LabelFor(NearbyFurniture f) => $"{f.Name} ({Vector3.Distance(f.Position, localPlayer.Position):0.0}y)";

                ImGui.SetNextItemWidth(220);
                var preview = LabelFor(list[selectedFurnitureIndex]);
                if (ImGui.BeginCombo("##PoseKitFurniturePicker", preview))
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var isSelected = i == selectedFurnitureIndex;
                        if (ImGui.Selectable($"{LabelFor(list[i])}##PoseKitFurniture{i}", isSelected))
                            selectedFurnitureIndex = i;
                    }
                    ImGui.EndCombo();
                }
                PoseKitUi.TextWrappedDisabled("Corrects the offset on replay against this furniture item's current " +
                                               "position, so it still lands right even in a differently laid-out room.");
            }
            else
            {
                PoseKitUi.TextWrappedDisabled("No furniture nearby to anchor to.");
                if (furnitureScanner.LastDiagnostic is { } diagnostic)
                    PoseKitUi.TextWrappedDisabled($"Debug: {diagnostic}");
            }
        }
    }

    /// Public so PairingPanel can offer the exact same clickable entry (click dispatch, pick
    /// highlighting, anchor/animation info) for whatever a paired partner just picked, without the
    /// user having to scroll down to find it in the Preset Library themselves.
    public static void DrawPresetEntry(Plugin plugin, NamedPose namedPose)
    {
        var anchorSuffix = namedPose.Anchor?.Spot != null ? " (anchored)"
            : namedPose.Anchor?.Furniture is { } furnitureAnchor ? $" (anchored: {furnitureAnchor.FurnitureName})"
            : namedPose.Anchor?.Partner is { } partnerAnchor ? $" (anchored: {partnerAnchor.Partner.Name})"
            : "";
        var coupleSuffix = namedPose.PartnerHalf != null ? " (couple)" : "";
        var label = $"{namedPose.Name}{anchorSuffix}{coupleSuffix}";

        var pick = PoseKitUi.GetPickState(plugin, namedPose.Name);
        using (PoseKitUi.PushPickButtonStyle(pick))
        {
            if (ImGui.Button($"{label}##PoseKitPreset{namedPose.GetHashCode()}"))
            {
                // Solo play bypass: play this side's own half only, exactly as if unpaired — even for
                // a couple preset, where that means calling PlayPreset (not PlayCouplePreset) so
                // nothing is relayed either. Checked first so it always wins regardless of mutual
                // override or plain pairing state. See couple-pairing's Solo Play Bypass requirement.
                if (plugin.PairingState.SoloPlayEnabled)
                    plugin.PlayPreset(namedPose);
                // A preset with a captured partner half plays and relays immediately rather than
                // going through the queue-and-wait-for-a-matching-selection flow — see
                // couple-preset-relay's spec and Plugin.PlayCouplePreset.
                else if (namedPose.PartnerHalf != null)
                    plugin.PlayCouplePreset(namedPose);
                // Under mutual override, a second click (once this side already has its own queued
                // pick) forces this preset onto the partner instead of replacing this side's own —
                // see CoupleQueueService.TryForceSelect.
                else if (plugin.PairingState.MutualOverrideActive && plugin.CoupleQueueService.QueuedSelectionName != null)
                    plugin.CoupleQueueService.TryForceSelect(namedPose.Name, () => plugin.PlayPreset(namedPose));
                else if (plugin.PairingState.Active)
                    plugin.CoupleQueueService.QueueSelection(namedPose.Name, () => plugin.PlayPreset(namedPose));
                else
                    plugin.PlayPreset(namedPose);
            }
        }
        PoseKitUi.DrawPickBadge(pick);

        ImGui.SameLine();
        if (ImGui.SmallButton($"x##PoseKitDeletePreset{namedPose.GetHashCode()}"))
            plugin.PresetManager.Delete(namedPose);

        if (namedPose.Anchor?.Spot is { } spot)
        {
            var p = spot.Position;
            PoseKitUi.TextWrappedDisabled($"Location: {spot.ZoneName} ({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0})");
        }

        if (namedPose.Penumbra is { ModName.Length: > 0 } link)
        {
            var animation = link.OptionName is "" or "Default" ? link.ModName : $"{link.ModName} — {link.OptionName}";
            PoseKitUi.TextWrappedDisabled($"Animation: {animation} (enabled automatically when played)");
        }

        var status = pick switch
        {
            PoseKitUi.PickState.Both => "both picked — playing shortly!",
            PoseKitUi.PickState.Own => "queued — waiting on partner",
            PoseKitUi.PickState.Partner => "your partner picked this — pick it too to play together",
            _ => null,
        };
        if (status != null)
            PoseKitUi.TextWrappedDisabled(status);
    }
}
