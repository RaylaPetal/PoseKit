using System;
using System.Collections.Generic;
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
            ImGui.TextDisabled("Penumbra not found — pose discovery unavailable.");
            return;
        }

        if (plugin.DiscoveredPoses.Count == 0)
        {
            ImGui.TextDisabled("No mods selected — pick some in Settings.");
            return;
        }

        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        var anyVisible = false;

        foreach (var mod in plugin.DiscoveredPoses)
        {
            if (!ModMatches(mod, animationSearch))
                continue;

            anyVisible = true;
            var searchTreeFlags = string.IsNullOrWhiteSpace(animationSearch)
                ? ImGuiTreeNodeFlags.None
                : ImGuiTreeNodeFlags.DefaultOpen;
            if (!ImGui.CollapsingHeader($"{mod.ModName}##PoseKitMod{mod.ModDirectory.GetHashCode()}", searchTreeFlags))
                continue;

            ImGui.Indent();
            foreach (var group in mod.Groups)
            {
                if (GroupMatches(mod, group, animationSearch))
                    DrawGroup(plugin, mod, group, collectionId, animationSearch);
            }
            ImGui.Unindent();
        }

        if (!anyVisible)
            ImGui.TextDisabled("No animations match this search.");
    }

    private static void DrawGroup(Plugin plugin, PoseModInfo mod, PoseModGroup group, Guid? collectionId, string filter)
    {
        ImGui.PushID(group.Name);

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

                    DrawTriggerButtons(plugin, mod, option.Triggers, $"PoseKitMultiPlay{option.Name.GetHashCode()}");
                }
                ImGui.Unindent();
            }
        }
        else
        {
            ImGui.TextUnformatted(group.Name);

            var selectedOption = FindSelected(group);
            var currentLabel = selectedOption?.Name ?? "Disabled";
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

            DrawTriggerButtons(plugin, mod, selectedOption?.Triggers ?? [], "PoseKitGroupPlay");
        }

        ImGui.PopID();
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
    private static bool ApplyGroupChange(Plugin plugin, PoseModInfo mod, PoseModGroup changedGroup,
        HashSet<string> newSelection, Guid collectionId)
    {
        var allSelections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var g in mod.Groups)
            allSelections[g.Name] = g == changedGroup ? [.. newSelection] : [.. g.Selected];

        if (!plugin.PenumbraIpc.TrySetTemporarySettings(collectionId, mod.ModDirectory, true, allSelections))
            return false;

        changedGroup.Selected = newSelection;
        plugin.PenumbraIpc.TryRedrawLocalPlayer();
        return true;
    }

    private static PoseModOption? FindSelected(PoseModGroup group)
    {
        foreach (var option in group.Options)
            if (group.Selected.Contains(option.Name))
                return option;
        return null;
    }

    private static void DrawTriggerButtons(Plugin plugin, PoseModInfo mod, List<PoseTriggerHint> triggers, string idPrefix)
    {
        for (var i = 0; i < triggers.Count; i++)
        {
            var trigger = triggers[i];
            var label = trigger.SlashCommand is { } cmd ? $"/{cmd}" : trigger.PoseIdentifier!.Value.DisplayName;
            ImGui.SameLine();
            if (ImGui.SmallButton($"{label}##{idPrefix}{i}"))
            {
                CapturePenumbraContext(plugin, mod);
                PlayTrigger(plugin, trigger);
            }
        }
    }

    private static void CapturePenumbraContext(Plugin plugin, PoseModInfo mod)
    {
        var link = new PenumbraLink { ModDirectory = mod.ModDirectory };
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
