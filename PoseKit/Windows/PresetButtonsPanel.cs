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

/// <summary>Live-offset editor (drawn in MainWindow's sidebar) and the Presets page: a collapsible
/// save form above a searchable, filterable library grouped by pose. Pairing itself lives in
/// PairingPanel — presets don't name a partner; see DrawPresetEntry.</summary>
public static class PresetButtonsPanel
{
    private enum AnchorMode { None, Spot, Furniture }

    private enum TypeFilter { All, Solo, Couple }

    /// Which anchor kind a preset carries — "None" is a real filter value (unanchored presets), not
    /// "no filter"; that's Any.
    private enum AnchorFilter { Any, None, Spot, Furniture, Partner }

    private static readonly FurnitureScanner furnitureScanner = new();

    private static string newPresetName = "";
    private static AnchorMode anchorMode = AnchorMode.None;
    private static int selectedFurnitureIndex = -1;
    private static bool includePartner;

    // Library search/filter state — session-only, like the save form's fields above.
    private static string presetSearch = "";
    private static TypeFilter typeFilter = TypeFilter.All;
    private static AnchorFilter anchorFilter = AnchorFilter.Any;

    private static bool IsFiltering =>
        presetSearch.Trim().Length > 0 || typeFilter != TypeFilter.All || anchorFilter != AnchorFilter.Any;

    /// The Presets page's title row (title, saved count, right-aligned search) and filter row (type
    /// and anchor combos, plus Clear while anything is active) — same shape as the Animations page's
    /// toolbar so both pages read the same.
    public static void DrawToolbar(Plugin plugin)
    {
        ImGui.AlignTextToFramePadding();
        PoseKitUi.CardTitle("Presets", $"{plugin.PresetManager.Presets.Count} saved");
        ImGui.SameLine();

        var avail = ImGui.GetContentRegionAvail().X;
        var searchWidth = Math.Min(280f, avail);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - searchWidth);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##PoseKitPresetSearch", "Search name, pose, anchor, or animation...", ref presetSearch, 128);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(PoseKitUi.Muted, "Type");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        EnumCombo("##PoseKitPresetTypeFilter", ref typeFilter, TypeFilterOptions);

        ImGui.SameLine(0, 16);
        ImGui.TextColored(PoseKitUi.Muted, "Anchor");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        EnumCombo("##PoseKitPresetAnchorFilter", ref anchorFilter, AnchorFilterOptions);

        if (IsFiltering)
        {
            ImGui.SameLine(0, 16);
            if (ImGui.SmallButton("Clear##PoseKitClearPresetFilters"))
            {
                presetSearch = "";
                typeFilter = TypeFilter.All;
                anchorFilter = AnchorFilter.Any;
            }
        }

        PoseKitUi.CardTitleRule();
    }

    private static readonly TypeFilter[] TypeFilterOptions = [TypeFilter.All, TypeFilter.Solo, TypeFilter.Couple];

    private static readonly AnchorFilter[] AnchorFilterOptions =
        [AnchorFilter.Any, AnchorFilter.None, AnchorFilter.Spot, AnchorFilter.Furniture, AnchorFilter.Partner];

    private static void EnumCombo<T>(string id, ref T value, T[] options) where T : struct, Enum
    {
        if (!ImGui.BeginCombo(id, value.ToString()))
            return;
        foreach (var option in options)
        {
            if (ImGui.Selectable(option.ToString(), option.Equals(value)))
                value = option;
        }
        ImGui.EndCombo();
    }

    /// Type, then anchor kind, then free text — cheapest checks first. The text match is
    /// case-insensitive over everything a player might remember a preset by; the spot's zone name
    /// (a game-data sheet lookup) is checked last, only when nothing cheaper matched.
    private static bool Matches(NamedPose preset)
    {
        if (typeFilter == TypeFilter.Solo && preset.PartnerHalf != null) return false;
        if (typeFilter == TypeFilter.Couple && preset.PartnerHalf == null) return false;
        if (anchorFilter != AnchorFilter.Any && AnchorKindOf(preset) != anchorFilter) return false;

        var text = presetSearch.Trim();
        if (text.Length == 0) return true;

        bool Has(string? field) => field != null && field.Contains(text, StringComparison.OrdinalIgnoreCase);
        return Has(preset.Name)
               || Has(preset.Pose.DisplayName)
               || Has(preset.Anchor?.Furniture?.FurnitureName)
               || Has(preset.Anchor?.Partner?.Partner.Name)
               || Has(preset.PartnerHalf?.Partner.Name)
               || Has(preset.Penumbra?.ModName)
               || Has(preset.Penumbra?.OptionName)
               || (preset.Anchor?.Spot is { } spot && Has(spot.ZoneName));
    }

    private static AnchorFilter AnchorKindOf(NamedPose preset) =>
        preset.Anchor?.Spot != null ? AnchorFilter.Spot
        : preset.Anchor?.Furniture != null ? AnchorFilter.Furniture
        : preset.Anchor?.Partner != null ? AnchorFilter.Partner
        : AnchorFilter.None;

    public static void DrawOffsets(Plugin plugin)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var currentPose = PoseIdentifier.FromCharacter(localPlayer);
        PoseKitUi.TextWrappedDisabled(currentPose?.DisplayName ?? "Not currently in a pose/emote loop.");

        using (ImRaii.Disabled(currentPose is null))
        {
            var offset = plugin.OffsetEngine.DesiredOffset;
            var changed = false;

            // Drawn in the narrow sidebar, so the drag fields take whatever width is left after the
            // widest label rather than a fixed width that would push labels off the edge.
            var dragWidth = MathF.Max(60f, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Forward / Back").X
                                            - ImGui.GetStyle().ItemSpacing.X);
            changed |= PoseKitUi.AxisDragFloat("OffsetX", "Left / Right", ref offset.Position.X, width: dragWidth);
            changed |= PoseKitUi.AxisDragFloat("OffsetY", "Height", ref offset.Position.Y, width: dragWidth);
            changed |= PoseKitUi.AxisDragFloat("OffsetZ", "Forward / Back", ref offset.Position.Z, width: dragWidth);

            if (plugin.OffsetEngine.RotationHookResolved)
            {
                var degrees = offset.Rotation * (180f / MathF.PI);
                if (PoseKitUi.AxisDragFloat("Rotation", "Rotation", ref degrees, 1f, dragWidth))
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

    /// The Presets page body below its toolbar: the collapsible save form, then the library.
    public static void DrawPresets(Plugin plugin)
    {
        if (ImGui.CollapsingHeader("Save current offset##PoseKitSaveSection", ImGuiTreeNodeFlags.DefaultOpen))
            DrawSaveForm(plugin);

        ImGui.Spacing();
        DrawLibrary(plugin);
    }

    private static void DrawSaveForm(Plugin plugin)
    {
        var currentPose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
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
    }

    /// Presets matching the current search/filters, grouped by pose — groups ordered by pose name,
    /// presets by their own name, each header showing how many it currently lists. While filtering,
    /// every remaining group is forced open so matches are never hidden inside a collapsed header.
    private static void DrawLibrary(Plugin plugin)
    {
        var all = plugin.PresetManager.Presets;
        if (all.Count == 0)
        {
            PoseKitUi.TextWrappedDisabled("No presets saved yet — save your current offset above.");
            return;
        }

        var filtering = IsFiltering;
        var groups = all.Where(Matches)
            .GroupBy(p => p.Pose)
            .OrderBy(g => g.Key.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
        {
            PoseKitUi.TextWrappedDisabled("No presets match your search or filters.");
            return;
        }

        foreach (var group in groups)
        {
            var presets = group.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (filtering)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (!ImGui.CollapsingHeader($"{group.Key.DisplayName}  ({presets.Count})##PoseKitPresetGroup{group.Key.GetHashCode()}",
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.Indent();
            foreach (var namedPose in presets)
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
    ///
    /// One compact row: the name as the play button, then short tags (Couple, and the anchor kind with
    /// its target), a right-aligned delete, and the linked animation as a muted second line.
    public static void DrawPresetEntry(Plugin plugin, NamedPose namedPose)
    {
        var pick = PoseKitUi.GetPickState(plugin, namedPose.Name);
        using (PoseKitUi.PushPickButtonStyle(pick))
        {
            if (ImGui.Button($"{namedPose.Name}##PoseKitPreset{namedPose.GetHashCode()}"))
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

        if (namedPose.PartnerHalf != null)
            DrawEntryTag("Couple", PoseKitUi.Info);
        if (AnchorTag(namedPose) is { } anchorTag)
            DrawEntryTag(anchorTag, PoseKitUi.Muted);

        // Right-aligned, unless the row is already too full to leave room for it.
        var deleteLabel = $"Delete##PoseKitDeletePreset{namedPose.GetHashCode()}";
        ImGui.SameLine();
        var deleteX = ImGui.GetWindowContentRegionMax().X - PoseKitUi.ButtonWidth(deleteLabel);
        if (ImGui.GetCursorPosX() < deleteX)
            ImGui.SetCursorPosX(deleteX);
        if (PoseKitUi.DangerButton(deleteLabel))
            plugin.PresetManager.Delete(namedPose);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Delete the \"{namedPose.Name}\" preset");

        if (namedPose.Penumbra is { ModName.Length: > 0 } link)
        {
            var animation = link.OptionName is "" or "Default" ? link.ModName : $"{link.ModName} — {link.OptionName}";
            PoseKitUi.TextWrappedDisabled($"Animation: {animation}");
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

    private static string? AnchorTag(NamedPose preset) =>
        preset.Anchor?.Spot is { } spot ? $"Spot: {spot.ZoneName}"
        : preset.Anchor?.Furniture is { } furniture ? $"Furniture: {furniture.FurnitureName}"
        : preset.Anchor?.Partner is { } partner ? $"Partner: {partner.Partner.Name}"
        : null;

    /// A short tag continuing the entry's row, wrapping to the next line instead when it wouldn't
    /// fit beside what's already there (with room left for the delete button).
    private static void DrawEntryTag(string text, Vector4 color)
    {
        const float deleteReserve = 70f;
        ImGui.SameLine(0, 10);
        if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(text).X + deleteReserve)
            ImGui.NewLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, text);
    }
}
