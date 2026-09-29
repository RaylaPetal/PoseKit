using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using PoseKit.Pairing;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Windows;

/// <summary>
/// The Animations tab: each scanned mod's groups and options, like Penumbra's own settings page,
/// with a Play button per detected trigger. Options without a trigger are still shown so they can be
/// configured.
///
/// Playing records the mod state (Plugin.LastPlayedPenumbraContext) so a saved preset can restore it.
/// </summary>
public static class PenumbraPosePanel
{
    private static string animationSearch = "";

    public static void DrawToolbar(Plugin plugin)
    {
        ImGui.AlignTextToFramePadding();
        PoseKitUi.CardTitle("All Animations", $"{plugin.DiscoveredPoses.Count} MODS");
        ImGui.SameLine(0, 10);
        if (ImGui.Button("Rescan##PoseKitToolbarRescan"))
            plugin.RefreshPenumbraPoses();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-scan your selected Penumbra mods for poses and animations.");
        ImGui.SameLine();

        var avail = ImGui.GetContentRegionAvail().X;
        var searchWidth = Math.Min(280f, avail);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - searchWidth);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##PoseKitAnimationSearch", "Search name, command, or pose number...",
            ref animationSearch, 128);

        if (animationSearch.Length > 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(PoseKitUi.Muted, $"Filtering by \"{animationSearch}\"");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##PoseKitClearAnimationSearch"))
                animationSearch = "";
        }
        else
        {
            PoseKitUi.TextWrappedDisabled("Open a mod to choose an animation, pose, or option, then press Play.");
        }

        PoseKitUi.CardTitleRule();
    }

    public static void Draw(Plugin plugin)
    {
        if (!plugin.PenumbraIpc.IsAvailable)
        {
            PoseKitUi.TextWrappedDisabled("Penumbra not found — pose discovery unavailable.");
            return;
        }

        if (plugin.DiscoveredPoses.Count == 0)
        {
            PoseKitUi.TextWrappedDisabled("No mods selected — pick some in Settings.");
            return;
        }

        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        var anyVisible = false;
        var activePoses = ActivePoseMap.Build(plugin.DiscoveredPoses);

        foreach (var mod in plugin.DiscoveredPoses)
        {
            if (!ModMatches(mod, animationSearch))
                continue;

            anyVisible = true;
            var modPick = ModPickState(plugin, mod);
            var searchTreeFlags = string.IsNullOrWhiteSpace(animationSearch) || modPick != PoseKitUi.PickState.None
                ? ImGuiTreeNodeFlags.None
                : ImGuiTreeNodeFlags.DefaultOpen;
            if (modPick != PoseKitUi.PickState.None)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            var expanded = ImGui.CollapsingHeader($"{mod.ModName}##PoseKitMod{mod.ModDirectory.GetHashCode()}", searchTreeFlags);
            PoseKitUi.DrawPickBadge(modPick);

            // The header spans the whole row; without this it eats the checkbox's clicks.
            ImGui.SetItemAllowOverlap();

            // Playing only ever enables a mod, so this is the way to turn one back off.
            ImGui.SameLine(0, 20);
            var modEnabled = mod.Enabled;
            if (ImGui.Checkbox($"Enabled##PoseKitModEnabled{mod.ModDirectory.GetHashCode()}", ref modEnabled))
                SetModEnabled(plugin, mod, collectionId, modEnabled);

            if (!expanded)
                continue;

            ImGui.Indent();
            if (!mod.Enabled)
                PoseKitUi.TextWrappedDisabled("Disabled in Penumbra — playing anything below enables it temporarily, or use the checkbox above.");
            foreach (var group in mod.Groups)
            {
                if (GroupMatches(mod, group, animationSearch))
                    DrawGroup(plugin, mod, group, collectionId, animationSearch, activePoses);
            }
            ImGui.Unindent();
        }

        if (!anyVisible)
            ImGui.TextDisabled("No animations match this search.");
    }

    private static void DrawGroup(Plugin plugin, PoseModInfo mod, PoseModGroup group, Guid? collectionId, string filter,
        Dictionary<PoseIdentifier, List<(PoseModInfo Mod, PoseModOption Option)>> activePoses)
    {
        ImGui.PushID(group.Name);

        if (group.IsImplicit)
        {
            // The mod's always-active files: nothing to select, just trigger buttons.
            ImGui.TextUnformatted(group.Name);
            foreach (var option in group.Options)
            {
                if (DescribeConflict(plugin, activePoses, mod, option, collectionId) is { } conflict)
                    PoseKitUi.DrawConflictMarker(ConflictId(mod, option), conflict.Tooltip, conflict.Resolve);
                DrawOptionPickMarker(plugin, mod, option);
                DrawTriggerButtons(plugin, mod, group, option, collectionId, "PoseKitDefaultPlay");
            }
            ImGui.PopID();
            return;
        }

        var showAllOptions = Matches(mod.ModName, filter) || Matches(group.Name, filter);
        var visibleOptions = showAllOptions
            ? group.Options
            : group.Options.FindAll(option => OptionMatches(option, filter));

        if (group.MultiSelect)
        {
            var groupHasPick = visibleOptions.Any(o => OptionPickState(plugin, mod, o) != PoseKitUi.PickState.None);
            var searchTreeFlags = string.IsNullOrWhiteSpace(filter) || groupHasPick
                ? ImGuiTreeNodeFlags.None
                : ImGuiTreeNodeFlags.DefaultOpen;
            if (groupHasPick)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            if (ImGui.CollapsingHeader(group.Name, searchTreeFlags))
            {
                ImGui.Indent();
                foreach (var option in visibleOptions)
                {
                    var isChecked = group.Selected.Contains(option.Name);
                    if (ImGui.Checkbox(option.Name, ref isChecked) && collectionId is { } cid)
                    {
                        var newSelection = new HashSet<string>(group.Selected);
                        if (isChecked) newSelection.Add(option.Name);
                        else newSelection.Remove(option.Name);

                        ApplyGroupChange(plugin, mod, group, newSelection, cid);
                    }

                    if (isChecked && DescribeConflict(plugin, activePoses, mod, option, collectionId) is { } conflict)
                        PoseKitUi.DrawConflictMarker(ConflictId(mod, option), conflict.Tooltip, conflict.Resolve);
                    DrawOptionPickMarker(plugin, mod, option);

                    DrawTriggerButtons(plugin, mod, group, option, collectionId, $"PoseKitMultiPlay{option.Name.GetHashCode()}");
                }
                ImGui.Unindent();
            }
        }
        else
        {
            ImGui.TextUnformatted(group.Name);

            var selectedOption = FindSelected(group);
            var currentLabel = selectedOption?.Name ?? "Disabled";
            ImGui.SetNextItemWidth(Math.Min(260f, ImGui.GetContentRegionAvail().X));
            if (ImGui.BeginCombo("##PoseKitGroupCombo", currentLabel))
            {
                foreach (var option in visibleOptions)
                {
                    var isSelected = group.Selected.Contains(option.Name);
                    if (ImGui.Selectable(option.Name, isSelected) && collectionId is { } cid)
                    {
                        ApplyGroupChange(plugin, mod, group, [option.Name], cid);
                    }

                    DrawOptionPickMarker(plugin, mod, option);

                    // Every option gets a Play button that selects it first.
                    DrawTriggerButtons(plugin, mod, group, option, collectionId, $"PoseKitComboPlay{option.Name.GetHashCode()}",
                        beforePlay: isSelected || collectionId is null
                            ? null
                            : () => ApplyGroupChange(plugin, mod, group, [option.Name], collectionId.Value),
                        compact: true);
                }

                ImGui.EndCombo();
            }

            if (selectedOption != null && DescribeConflict(plugin, activePoses, mod, selectedOption, collectionId) is { } comboConflict)
                PoseKitUi.DrawConflictMarker(ConflictId(mod, selectedOption), comboConflict.Tooltip, comboConflict.Resolve);
            if (selectedOption != null)
                DrawOptionPickMarker(plugin, mod, selectedOption);

            if (selectedOption != null)
                DrawTriggerButtons(plugin, mod, group, selectedOption, collectionId, "PoseKitGroupPlay");
        }

        ImGui.PopID();
    }

    /// Whether a queued pick is in this mod, so its header can open and show a badge.
    private static PoseKitUi.PickState ModPickState(Plugin plugin, PoseModInfo mod)
    {
        var best = PoseKitUi.PickState.None;
        foreach (var group in mod.Groups)
        foreach (var option in group.Options)
        {
            var state = OptionPickState(plugin, mod, option);
            if (state == PoseKitUi.PickState.Both) return state;
            if (state != PoseKitUi.PickState.None) best = state;
        }
        return best;
    }

    /// Whether any of the option's triggers is queued.
    private static PoseKitUi.PickState OptionPickState(Plugin plugin, PoseModInfo mod, PoseModOption option)
    {
        var best = PoseKitUi.PickState.None;
        foreach (var trigger in option.Triggers)
        {
            var state = PoseKitUi.GetPickState(plugin, DescribeTriggerLabel(mod, option, trigger));
            if (state == PoseKitUi.PickState.Both) return state;
            if (state != PoseKitUi.PickState.None) best = state;
        }
        return best;
    }

    private static void DrawOptionPickMarker(Plugin plugin, PoseModInfo mod, PoseModOption option) =>
        PoseKitUi.DrawPickBadge(OptionPickState(plugin, mod, option));

    /// "ModName — OptionName", which identifies an animation choice (the pose slot name doesn't).
    private static string DescribePlayLabel(PoseModInfo mod, PoseModOption option) =>
        option.Name is "" or "Default" ? mod.ModName : $"{mod.ModName} — {option.Name}";

    /// DescribePlayLabel plus the trigger, when the option has more than one.
    private static string DescribeTriggerLabel(PoseModInfo mod, PoseModOption option, PoseTriggerHint trigger)
    {
        var optionLabel = DescribePlayLabel(mod, option);
        if (option.Triggers.Count <= 1) return optionLabel;
        var triggerText = trigger.SlashCommand is { } cmd ? $"/{cmd}" : trigger.PoseIdentifier!.Value.DisplayName;
        return $"{optionLabel} ({triggerText})";
    }

    /// Null unless another selected option (in any enabled mod) claims the same pose.
    ///
    /// The fix keeps this option: other mods are disabled, and other options in this same mod are
    /// deselected (a single-select group switches to an option without triggers, if it has one).
    /// Resolve is null when nothing can be fixed.
    private static (string Tooltip, Action? Resolve)? DescribeConflict(Plugin plugin,
        Dictionary<PoseIdentifier, List<(PoseModInfo Mod, PoseModOption Option)>> activePoses,
        PoseModInfo mod, PoseModOption option, Guid? collectionId)
    {
        string? description = null;
        var otherTrackedMods = new List<PoseModInfo>();
        var otherExternalMods = new List<(string ModDirectory, string ModName)>();
        var sameModOthers = new List<PoseModOption>();

        foreach (var trigger in option.Triggers)
        {
            if (trigger.PoseIdentifier is not { } pid) continue;

            if (activePoses.TryGetValue(pid, out var claimants) && claimants.Count > 1)
            {
                foreach (var other in claimants)
                {
                    if (other.Option == option) continue;
                    description ??= $"Also currently selected: \"{other.Option.Name}\" ({other.Mod.ModName}) — both claim {pid.DisplayName}. Only one will actually play.";
                    if (other.Mod != mod && !otherTrackedMods.Contains(other.Mod))
                        otherTrackedMods.Add(other.Mod);
                    else if (other.Mod == mod && !sameModOthers.Contains(other.Option))
                        sameModOthers.Add(other.Option);
                }
            }

            if (plugin.ExternalPoseClaims.TryGetValue(pid, out var external))
            {
                foreach (var claim in external)
                {
                    description ??= $"Also currently active: \"{claim.Label}\" ({claim.ModName}) — not in your Animations list, but both claim {pid.DisplayName}. Only one will actually play.";
                    if (!otherExternalMods.Exists(e => e.ModDirectory == claim.ModDirectory))
                        otherExternalMods.Add((claim.ModDirectory, claim.ModName));
                }
            }
        }

        if (description == null) return null;

        var sameModChanges = SameModDeselection(mod, sameModOthers, out var clearedOptions);
        if (otherTrackedMods.Count + otherExternalMods.Count + clearedOptions.Count == 0 || collectionId is not { } cid)
            return (description, null);

        var fixes = new List<string>();
        if (otherTrackedMods.Count + otherExternalMods.Count > 0)
            fixes.Add("disable " + string.Join(", ", otherTrackedMods.Select(m => m.ModName).Concat(otherExternalMods.Select(e => e.ModName))));
        if (clearedOptions.Count > 0)
            fixes.Add("deselect " + string.Join(", ", clearedOptions.Select(o => $"\"{o.Name}\"")));
        var tooltip = $"{description}\n\nClick to {string.Join(" and ", fixes)} so \"{option.Name}\" plays instead.";

        return (tooltip, () =>
        {
            foreach (var other in otherTrackedMods)
                SetModEnabled(plugin, other, cid, false);
            foreach (var (modDirectory, _) in otherExternalMods)
                DisableExternalMod(plugin, cid, modDirectory);
            if (sameModChanges.Count > 0)
                ApplyGroupChanges(plugin, mod, sameModChanges, cid);
            SetModEnabled(plugin, mod, cid, true);
            plugin.PenumbraIpc.TryRedrawLocalPlayer();
        });
    }

    /// The selections that clear <paramref name="others"/>; <paramref name="cleared"/> lists the
    /// ones that can be cleared.
    private static Dictionary<PoseModGroup, HashSet<string>> SameModDeselection(PoseModInfo mod, List<PoseModOption> others,
        out List<PoseModOption> cleared)
    {
        var changes = new Dictionary<PoseModGroup, HashSet<string>>();
        cleared = [];
        foreach (var other in others)
        {
            if (mod.Groups.FirstOrDefault(g => !g.IsImplicit && g.Options.Contains(other)) is not { } group)
                continue;
            if (!changes.TryGetValue(group, out var selection))
                selection = new HashSet<string>(group.Selected);

            if (group.MultiSelect)
            {
                selection.Remove(other.Name);
            }
            else if (group.Options.FirstOrDefault(o => o.Triggers.Count == 0) is { } none)
            {
                selection = [none.Name];
            }
            else
            {
                continue;
            }

            changes[group] = selection;
            cleared.Add(other);
        }
        return changes;
    }

    /// Group names repeat across mods, so the id includes the mod.
    private static string ConflictId(PoseModInfo mod, PoseModOption option) =>
        $"PoseKitConflict{mod.ModDirectory.GetHashCode()}{option.Name.GetHashCode()}";

    /// Temporarily disables an unscanned mod and drops its claims so the marker clears right away.
    private static void DisableExternalMod(Plugin plugin, Guid collectionId, string modDirectory)
    {
        var (_, priority, selections) = plugin.PenumbraIpc.TryGetCurrentSettings(collectionId, modDirectory);
        var allSelections = new Dictionary<string, IReadOnlyList<string>>();
        if (selections != null)
        {
            foreach (var (group, options) in selections)
                allSelections[group] = options;
        }

        if (!plugin.PenumbraIpc.TrySetTemporarySettings(collectionId, modDirectory, false, priority, allSelections))
            return;

        foreach (var claims in plugin.ExternalPoseClaims.Values)
            claims.RemoveAll(c => c.ModDirectory == modDirectory);
    }

    private static bool ModMatches(PoseModInfo mod, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || Matches(mod.ModName, filter)) return true;
        foreach (var group in mod.Groups)
            if (GroupMatches(mod, group, filter)) return true;
        return false;
    }

    private static bool GroupMatches(PoseModInfo mod, PoseModGroup group, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || Matches(mod.ModName, filter) || Matches(group.Name, filter))
            return true;
        foreach (var option in group.Options)
            if (OptionMatches(option, filter)) return true;
        return false;
    }

    private static bool OptionMatches(PoseModOption option, string filter)
    {
        if (Matches(option.Name, filter)) return true;
        foreach (var trigger in option.Triggers)
        {
            if (trigger.SlashCommand is { } command && Matches(command, filter)) return true;
            if (trigger.PoseIdentifier is not { } pose) continue;
            if (Matches(pose.DisplayName, filter) || Matches(pose.EmoteModeId.ToString(), filter)
                || Matches(pose.CPoseState.ToString(), filter))
                return true;
        }
        return false;
    }

    private static bool Matches(string value, string filter)
        => value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ApplyGroupChange(Plugin plugin, PoseModInfo mod, PoseModGroup changedGroup,
        HashSet<string> newSelection, Guid collectionId) =>
        ApplyGroupChanges(plugin, mod, new Dictionary<PoseModGroup, HashSet<string>> { [changedGroup] = newSelection }, collectionId);

    /// Sends every group's selection, since Penumbra replaces them all at once. The implicit group
    /// isn't a real Penumbra group and is left out.
    private static bool ApplyGroupChanges(Plugin plugin, PoseModInfo mod, Dictionary<PoseModGroup, HashSet<string>> changes,
        Guid collectionId)
    {
        var allSelections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var g in mod.Groups)
        {
            if (g.IsImplicit) continue;
            allSelections[g.Name] = changes.TryGetValue(g, out var changed) ? [.. changed] : [.. g.Selected];
        }

        if (!plugin.PenumbraIpc.TrySetTemporarySettings(collectionId, mod.ModDirectory, true, mod.Priority, allSelections))
            return false;

        foreach (var (group, selection) in changes)
            group.Selected = selection;
        mod.Enabled = true;
        plugin.PenumbraIpc.TryRedrawLocalPlayer();
        return true;
    }

    private static void EnsureModEnabled(Plugin plugin, PoseModInfo mod, Guid? collectionId)
        => SetModEnabled(plugin, mod, collectionId, true);

    /// Keeps the current selections, since Penumbra resets any group left out.
    private static void SetModEnabled(Plugin plugin, PoseModInfo mod, Guid? collectionId, bool enabled)
    {
        if (mod.Enabled == enabled || collectionId is not { } cid) return;

        var allSelections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var g in mod.Groups)
        {
            if (g.IsImplicit) continue;
            allSelections[g.Name] = [.. g.Selected];
        }

        if (!plugin.PenumbraIpc.TrySetTemporarySettings(cid, mod.ModDirectory, enabled, mod.Priority, allSelections))
            return;

        mod.Enabled = enabled;
        plugin.PenumbraIpc.TryRedrawLocalPlayer();
    }

    private static PoseModOption? FindSelected(PoseModGroup group)
    {
        foreach (var option in group.Options)
            if (group.Selected.Contains(option.Name))
                return option;
        return null;
    }

    /// <param name="beforePlay">Runs first, e.g. to select the option.</param>
    /// <param name="compact">Small buttons, for rows inside a combo dropdown.</param>
    private static void DrawTriggerButtons(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option,
        Guid? collectionId, string idPrefix, Action? beforePlay = null, bool compact = false)
    {
        var triggers = option.Triggers;
        for (var i = 0; i < triggers.Count; i++)
        {
            var trigger = triggers[i];
            var triggerLabel = DescribeTriggerLabel(mod, option, trigger);
            var pick = PoseKitUi.GetPickState(plugin, triggerLabel);
            var buttonText = trigger.SlashCommand is { } cmd ? $"/{cmd}" : trigger.PoseIdentifier!.Value.DisplayName;

            using (PoseKitUi.PushPickButtonStyle(pick))
            {
                var label = $"{buttonText}##{idPrefix}{i}";
                PoseKitUi.SameLineIfFits(PoseKitUi.ButtonWidth(label));
                if (compact ? ImGui.SmallButton(label) : ImGui.Button(label))
                {
                    void Play() => PlayOptionTrigger(plugin, mod, group, option, trigger, collectionId, beforePlay);

                    // Solo play always plays immediately.
                    if (plugin.PairingState.SoloPlayEnabled)
                    {
                        Play();
                    }
                    // Paired: queue. Under mutual override, a second click forces this pick on the
                    // partner, with hashes so they can resolve it.
                    else if (plugin.PairingState.MutualOverrideActive && plugin.CoupleQueueService.QueuedSelectionName != null)
                    {
                        var penumbraLink = new PenumbraLink
                        {
                            ModDirectory = mod.ModDirectory, ModName = mod.ModName, OptionName = option.Name,
                            GroupName = group.IsImplicit ? "" : group.Name,
                        };
                        var triggerText = trigger.SlashCommand is { } slashCmd ? slashCmd : trigger.PoseIdentifier!.Value.DisplayName;
                        plugin.CoupleQueueService.TryForceSelect(triggerLabel, Play, penumbraLink, triggerText);
                    }
                    else if (plugin.PairingState.Active)
                        plugin.CoupleQueueService.QueueSelection(triggerLabel, Play);
                    else
                        Play();
                }
            }

            PoseKitUi.DrawPickBadge(pick);
        }
    }

    private static void PlayOptionTrigger(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option,
        PoseTriggerHint trigger, Guid? collectionId, Action? beforePlay = null)
    {
        beforePlay?.Invoke();
        EnsureModEnabled(plugin, mod, collectionId);
        CapturePenumbraContext(plugin, mod, group, option);
        PlayTrigger(plugin, trigger);
        plugin.SetPlayContext(mod.ModDirectory, mod.ModName, group.IsImplicit ? "" : group.Name, option.Name,
            TriggerText(trigger), trigger.PoseIdentifier);
    }

    /// The slash command (without "/") or the pose's display name.
    public static string TriggerText(PoseTriggerHint trigger) =>
        trigger.SlashCommand is { } cmd ? cmd : trigger.PoseIdentifier!.Value.DisplayName;

    private static (PoseModInfo Mod, PoseModGroup Group, PoseModOption Option, PoseTriggerHint Trigger)? FindByTriggerLabel(
        Plugin plugin, string label)
    {
        foreach (var mod in plugin.DiscoveredPoses)
        foreach (var group in mod.Groups)
        foreach (var option in group.Options)
        foreach (var trigger in option.Triggers)
        {
            if (DescribeTriggerLabel(mod, option, trigger) == label)
                return (mod, group, option, trigger);
        }
        return null;
    }

    public static bool TryPlayByLabel(Plugin plugin, string label)
    {
        if (FindByTriggerLabel(plugin, label) is not { } found) return false;
        var (mod, group, option, trigger) = found;

        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        var beforePlay = SelectOptionBeforePlay(plugin, mod, group, option, collectionId);
        PlayOptionTrigger(plugin, mod, group, option, trigger, collectionId, beforePlay);
        return true;
    }

    /// Like FindByTriggerLabel but by hash, so it survives local renames. A "0" group hash matches
    /// the implicit group.
    private static (PoseModInfo Mod, PoseModGroup Group, PoseModOption Option, PoseTriggerHint Trigger)? FindByHash(
        Plugin plugin, ForceSelectionHashes hashes)
    {
        foreach (var mod in plugin.DiscoveredPoses)
        {
            if (ModDirectoryHash.Compute(mod.ModDirectory) != hashes.ModDirectoryHash) continue;

            foreach (var group in mod.Groups)
            {
                var groupMatches = hashes.GroupNameHash == "0"
                    ? group.IsImplicit
                    : ModDirectoryHash.Compute(group.Name) == hashes.GroupNameHash;
                if (!groupMatches) continue;

                foreach (var option in group.Options)
                {
                    var optionMatches = hashes.OptionNameHash == "0" || ModDirectoryHash.Compute(option.Name) == hashes.OptionNameHash;
                    if (!optionMatches) continue;

                    foreach (var trigger in option.Triggers)
                    {
                        var triggerText = trigger.SlashCommand is { } cmd ? cmd : trigger.PoseIdentifier!.Value.DisplayName;
                        if (ModDirectoryHash.Compute(triggerText) == hashes.TriggerHash)
                            return (mod, group, option, trigger);
                    }
                }
            }
        }
        return null;
    }

    public static bool TryPlayByHash(Plugin plugin, ForceSelectionHashes hashes)
    {
        if (!hashes.HasPenumbraData) return false;
        if (FindByHash(plugin, hashes) is not { } found) return false;
        var (mod, group, option, trigger) = found;

        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        var beforePlay = SelectOptionBeforePlay(plugin, mod, group, option, collectionId);
        PlayOptionTrigger(plugin, mod, group, option, trigger, collectionId, beforePlay);
        return true;
    }

    /// Trigger buttons for the partner's pick, shown in the pairing panel. An option with several
    /// triggers already covers both roles, so only it is shown; otherwise the roles are sibling
    /// options and all of them are shown. False when the mod isn't found.
    public static bool TryDrawQuickTriggerButtons(Plugin plugin, string label)
    {
        if (FindByTriggerLabel(plugin, label) is not { } found) return false;
        var (mod, group, matchedOption, _) = found;
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();

        ImGui.TextWrapped($"{mod.ModName} — {group.Name}:");

        if (matchedOption.Triggers.Count > 1)
        {
            ImGui.TextWrapped(matchedOption.Name);
            DrawTriggerButtons(plugin, mod, group, matchedOption, collectionId, $"PoseKitQuickPlay{matchedOption.Name.GetHashCode()}",
                SelectOptionBeforePlay(plugin, mod, group, matchedOption, collectionId));
            return true;
        }

        foreach (var sibling in group.Options)
        {
            if (sibling.Triggers.Count == 0) continue;
            ImGui.TextWrapped(sibling.Name);
            DrawTriggerButtons(plugin, mod, group, sibling, collectionId, $"PoseKitQuickPlay{sibling.Name.GetHashCode()}",
                SelectOptionBeforePlay(plugin, mod, group, sibling, collectionId));
        }
        return true;
    }

    /// Selects the option before playing when it isn't selected yet.
    private static Action? SelectOptionBeforePlay(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option, Guid? collectionId)
    {
        var alreadySelected = group.IsImplicit || group.Selected.Contains(option.Name);
        return alreadySelected || collectionId is not { } cid
            ? null
            : () => ApplyGroupChange(plugin, mod, group,
                group.MultiSelect ? new HashSet<string>(group.Selected) { option.Name } : [option.Name], cid);
    }

    private static void CapturePenumbraContext(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option)
    {
        var link = new PenumbraLink
        {
            ModDirectory = mod.ModDirectory, ModName = mod.ModName, OptionName = option.Name,
            GroupName = group.IsImplicit ? "" : group.Name,
        };
        foreach (var g in mod.Groups)
            link.GroupSelections[g.Name] = [.. g.Selected];
        plugin.LastPlayedPenumbraContext = link;
    }

    private static void PlayTrigger(Plugin plugin, PoseTriggerHint trigger)
    {
        if (trigger.SlashCommand is { } command)
            plugin.PoseTrigger.TriggerCommand(command);
        else if (trigger.PoseIdentifier is { } identifier)
            plugin.PoseTrigger.Trigger(identifier, PoseOffset.Zero);
    }
}
