using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Windows;

/// <summary>
/// Replicates each Penumbra mod's own settings page (one collapsible section per mod, and — since a
/// mod can easily have dozens of option groups (86 for a large curated collection) — each multi-select
/// group gets its own collapsible sub-section too, rather than dumping every checkbox in a flat wall
/// of text). Single-select groups stay a compact one-line combo, since a combo already summarizes to
/// just the current selection.
///
/// A small Play button per detected trigger sits next to each group/option — some options bind more
/// than one real emote at once (e.g. a two-person animation redirecting both /confirm's and /shiver's
/// files), so there can be more than one button. Options with no detected trigger are still shown (so
/// the mod can be configured through PoseKit) but have no Play button — some options genuinely aren't
/// gestures themselves (e.g. a paired "which weapon prop" companion option next to the real animation
/// option), same as picking it in Penumbra and playing it manually.
///
/// The mod list itself only ever contains mods explicitly picked in the Settings window and currently
/// enabled — see PenumbraPoseScanner. Playing anything here records which mod/selections produced it
/// (Plugin.LastPlayedPenumbraContext) so saving a preset from the Live Offset panel can carry that
/// along and re-enable the same mod state on replay.
/// </summary>
public static class PenumbraPosePanel
{
    private static string animationSearch = "";

    public static void DrawToolbar(Plugin plugin)
    {
        PoseKitUi.SectionHeader("Animation Library");

        var buttonWidth = 82f;
        ImGui.SetNextItemWidth(Math.Max(140f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##PoseKitAnimationSearch", "Search animation name, command, or pose number...",
            ref animationSearch, 128);
        ImGui.SameLine();
        if (ImGui.Button("Rescan##PoseKitPenumbraRescan", new System.Numerics.Vector2(buttonWidth, 0)))
            plugin.RefreshPenumbraPoses();

        if (animationSearch.Length > 0)
        {
            ImGui.TextDisabled($"Filtering by “{animationSearch}”");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##PoseKitClearAnimationSearch"))
                animationSearch = "";
        }
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
        var activePoses = BuildActivePoseMap(plugin.DiscoveredPoses);

        foreach (var mod in plugin.DiscoveredPoses)
        {
            if (!ModMatches(mod, animationSearch))
                continue;

            anyVisible = true;
            var searchTreeFlags = string.IsNullOrWhiteSpace(animationSearch)
                ? ImGuiTreeNodeFlags.None
                : ImGuiTreeNodeFlags.DefaultOpen;
            var headerLabel = mod.Enabled ? mod.ModName : $"{mod.ModName} (disabled)";
            if (!ImGui.CollapsingHeader($"{headerLabel}##PoseKitMod{mod.ModDirectory.GetHashCode()}", searchTreeFlags))
                continue;

            ImGui.Indent();
            if (!mod.Enabled)
                PoseKitUi.TextWrappedDisabled("Disabled in Penumbra — playing anything below enables it temporarily.");
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
            // No real Penumbra group backs this — it's just the mod's always-active default files.
            // Nothing to select (there's only ever the one implicit option), so skip straight to
            // trigger buttons instead of a one-item combo that would falsely imply a choice exists.
            ImGui.TextUnformatted(group.Name);
            foreach (var option in group.Options)
            {
                if (DescribeConflict(activePoses, mod, option) is { } conflict)
                    PoseKitUi.DrawConflictMarker(conflict);
                DrawTriggerButtons(plugin, mod, option, collectionId, "PoseKitDefaultPlay");
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
            var searchTreeFlags = string.IsNullOrWhiteSpace(filter)
                ? ImGuiTreeNodeFlags.None
                : ImGuiTreeNodeFlags.DefaultOpen;
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

                    if (isChecked && DescribeConflict(activePoses, mod, option) is { } conflict)
                        PoseKitUi.DrawConflictMarker(conflict);

                    DrawTriggerButtons(plugin, mod, option, collectionId, $"PoseKitMultiPlay{option.Name.GetHashCode()}");
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
                }

                ImGui.EndCombo();
            }

            if (selectedOption != null && DescribeConflict(activePoses, mod, selectedOption) is { } comboConflict)
                PoseKitUi.DrawConflictMarker(comboConflict);

            if (selectedOption != null)
                DrawTriggerButtons(plugin, mod, selectedOption, collectionId, "PoseKitGroupPlay");
        }

        ImGui.PopID();
    }

    /// Every currently-*selected* option's pose triggers across the whole discovered-mods list, keyed
    /// by PoseIdentifier — only selected options are actually "live" in Penumbra (an unselected option
    /// contributes no file redirects), so those are the only ones that can genuinely collide. With a
    /// large curated pack like GoonersLife, it's easy to have e.g. two different groups (or two
    /// checked options in the same multi-select group) both claim "GroundSit Pose 3": only one of
    /// their file redirects actually wins in Penumbra, so playing either button may not produce what
    /// its own label promised.
    private static Dictionary<PoseIdentifier, List<(PoseModInfo Mod, PoseModOption Option)>> BuildActivePoseMap(List<PoseModInfo> mods)
    {
        var map = new Dictionary<PoseIdentifier, List<(PoseModInfo, PoseModOption)>>();
        foreach (var mod in mods)
        {
            foreach (var group in mod.Groups)
            {
                foreach (var option in group.Options)
                {
                    if (!group.Selected.Contains(option.Name)) continue;
                    foreach (var trigger in option.Triggers)
                    {
                        if (trigger.PoseIdentifier is not { } pid) continue;
                        if (!map.TryGetValue(pid, out var claimants))
                            map[pid] = claimants = [];
                        claimants.Add((mod, option));
                    }
                }
            }
        }
        return map;
    }

    /// Null unless this option is currently one of two-or-more selected options claiming the same
    /// gesture — reports the first such collision found, naming the other claimant.
    private static string? DescribeConflict(
        Dictionary<PoseIdentifier, List<(PoseModInfo Mod, PoseModOption Option)>> activePoses,
        PoseModInfo mod, PoseModOption option)
    {
        foreach (var trigger in option.Triggers)
        {
            if (trigger.PoseIdentifier is not { } pid) continue;
            if (!activePoses.TryGetValue(pid, out var claimants) || claimants.Count <= 1) continue;

            var other = claimants.FirstOrDefault(c => c.Option != option);
            if (other.Option != null)
                return $"Also currently selected: \"{other.Option.Name}\" ({other.Mod.ModName}) — both claim {pid.DisplayName}. Only one will actually play.";
        }

        return null;
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
                || Matches((pose.CPoseState + 1).ToString(), filter))
                return true;
        }
        return false;
    }

    private static bool Matches(string value, string filter)
        => value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    /// Penumbra's temporary-settings IPC replaces a mod's *entire* set of group selections in one
    /// call, so every group's current selection has to be sent even though only one is changing.
    /// Implicit groups (PenumbraPoseScanner's synthetic "Default" from default_mod.json) are skipped
    /// — Penumbra has no group by that name, so including one would corrupt the payload.
    private static bool ApplyGroupChange(Plugin plugin, PoseModInfo mod, PoseModGroup changedGroup,
        HashSet<string> newSelection, Guid collectionId)
    {
        var allSelections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var g in mod.Groups)
        {
            if (g.IsImplicit) continue;
            allSelections[g.Name] = g == changedGroup ? [.. newSelection] : [.. g.Selected];
        }

        if (!plugin.PenumbraIpc.TrySetTemporarySettings(collectionId, mod.ModDirectory, true, allSelections))
            return false;

        changedGroup.Selected = newSelection;
        mod.Enabled = true;
        plugin.PenumbraIpc.TryRedrawLocalPlayer();
        return true;
    }

    /// Playing a pose from a mod that's currently disabled in Penumbra shouldn't require going there
    /// first to flip it on — temporarily enable it here (same mechanism ApplyGroupChange already uses
    /// for selection changes) with its current group selections, so the redirect is actually live by
    /// the time the pose is triggered.
    private static void EnsureModEnabled(Plugin plugin, PoseModInfo mod, Guid? collectionId)
    {
        if (mod.Enabled || collectionId is not { } cid) return;

        var allSelections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var g in mod.Groups)
        {
            if (g.IsImplicit) continue;
            allSelections[g.Name] = [.. g.Selected];
        }

        if (!plugin.PenumbraIpc.TrySetTemporarySettings(cid, mod.ModDirectory, true, allSelections))
            return;

        mod.Enabled = true;
        plugin.PenumbraIpc.TryRedrawLocalPlayer();
    }

    private static PoseModOption? FindSelected(PoseModGroup group)
    {
        foreach (var option in group.Options)
            if (group.Selected.Contains(option.Name))
                return option;
        return null;
    }

    private static void DrawTriggerButtons(Plugin plugin, PoseModInfo mod, PoseModOption option, Guid? collectionId, string idPrefix)
    {
        var triggers = option.Triggers;
        for (var i = 0; i < triggers.Count; i++)
        {
            var trigger = triggers[i];
            var label = trigger.SlashCommand is { } cmd ? $"/{cmd}" : trigger.PoseIdentifier!.Value.DisplayName;
            ImGui.SameLine();
            if (ImGui.SmallButton($"{label}##{idPrefix}{i}"))
            {
                EnsureModEnabled(plugin, mod, collectionId);
                CapturePenumbraContext(plugin, mod, option);
                PlayTrigger(plugin, trigger);
            }
        }
    }

    private static void CapturePenumbraContext(Plugin plugin, PoseModInfo mod, PoseModOption option)
    {
        var link = new PenumbraLink { ModDirectory = mod.ModDirectory, ModName = mod.ModName, OptionName = option.Name };
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
