using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using PoseKit.Library.Categories;
using PoseKit.Pairing;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Library;

/// <summary>
/// What clicking an animation trigger or a preset does, shared by the main window and the quick
/// library so the two can't drift apart: solo play, the pairing queue, force-select under mutual
/// override, and couple-preset relay. Also the Penumbra option changes playing needs.
/// </summary>
public static class PlayDispatch
{
    /// A trigger button click, from the main window or the library alike. <paramref name="beforePlay"/>
    /// runs first, e.g. to select the option. Paired, it's queued until the partner picks too (or forced
    /// on them under mutual override), so one click sends at most one tell.
    public static void ClickTrigger(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option,
        PoseTriggerHint trigger, Guid? collectionId, Action? beforePlay = null)
    {
        void Play() => PlayOptionTrigger(plugin, mod, group, option, trigger, collectionId, beforePlay);

        var triggerLabel = DescribeTriggerLabel(mod, option, trigger);

        // Solo play always plays immediately.
        if (plugin.PairingState.SoloPlayEnabled)
        {
            Play();
        }
        // Paired: queue. Under mutual override, a second click forces this pick on the partner, with
        // hashes so they can resolve it.
        else if (plugin.PairingState.MutualOverrideActive && plugin.CoupleQueueService.QueuedSelectionName != null)
        {
            var penumbraLink = new PenumbraLink
            {
                ModDirectory = mod.ModDirectory, ModName = mod.ModName, OptionName = option.Name,
                GroupName = group.IsImplicit ? "" : group.Name,
            };
            plugin.CoupleQueueService.TryForceSelect(triggerLabel, Play, penumbraLink, TriggerText(trigger));
        }
        else if (plugin.PairingState.Active)
            plugin.CoupleQueueService.QueueSelection(triggerLabel, Play);
        else
            Play();
    }

    /// The option name without its number, spacing or case, so a sibling mod's copy matches.
    private static string OptionKeyHash(string optionName) =>
        ModDirectoryHash.Compute(CategorySource.NormalizeName(optionName).ToLowerInvariant());

    /// Finds a relayed role among this side's mods: the same mod first, then the same option name in
    /// any mod (e.g. Nightlife+ for a GoonersLife+ pick).
    public static (PoseModInfo Mod, PoseModGroup Group, PoseModOption Option, PoseTriggerHint Trigger)? FindRole(Plugin plugin, RoleHashes role)
    {
        (PoseModInfo, PoseModGroup, PoseModOption, PoseTriggerHint)? byName = null;
        foreach (var mod in plugin.DiscoveredPoses)
        {
            var sameMod = ModDirectoryHash.Compute(mod.ModDirectory) == role.ModDirectoryHash;
            foreach (var group in mod.Groups)
            {
                var sameGroup = role.GroupNameHash == "0" ? group.IsImplicit : ModDirectoryHash.Compute(group.Name) == role.GroupNameHash;
                foreach (var option in group.Options)
                {
                    var exact = sameMod && sameGroup && ModDirectoryHash.Compute(option.Name) == role.OptionNameHash;
                    if (!exact && (byName != null || OptionKeyHash(option.Name) != role.OptionKeyHash)) continue;

                    foreach (var trigger in option.Triggers)
                    {
                        if (ModDirectoryHash.Compute(TriggerText(trigger)) != role.TriggerHash) continue;
                        if (exact) return (mod, group, option, trigger);
                        byName ??= (mod, group, option, trigger);
                    }
                }
            }
        }
        return byName;
    }

    /// Plays a role the partner sent. False when it isn't in this side's mods.
    public static bool PlayRole(Plugin plugin, RoleHashes role)
    {
        if (FindRole(plugin, role) is not { } found) return false;
        var (mod, group, option, trigger) = found;
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        PlayOptionTrigger(plugin, mod, group, option, trigger, collectionId,
            SelectOptionBeforePlay(plugin, mod, group, option, collectionId));
        return true;
    }

    /// A preset click.
    public static void ClickPreset(Plugin plugin, NamedPose preset)
    {
        // Solo play: only this side's half, nothing relayed.
        if (plugin.PairingState.SoloPlayEnabled)
            plugin.PlayPreset(preset);
        // Couple presets relay directly instead of queueing.
        else if (preset.PartnerHalf != null)
            plugin.PlayCouplePreset(preset);
        // Under mutual override, a second click forces this preset on the partner.
        else if (plugin.PairingState.MutualOverrideActive && plugin.CoupleQueueService.QueuedSelectionName != null)
            plugin.CoupleQueueService.TryForceSelect(preset.Name, () => plugin.PlayPreset(preset));
        else if (plugin.PairingState.Active)
            plugin.CoupleQueueService.QueueSelection(preset.Name, () => plugin.PlayPreset(preset));
        else
            plugin.PlayPreset(preset);
    }

    public static void PlayOptionTrigger(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option,
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

    /// "/command" or the pose's display name, for buttons.
    public static string TriggerButtonText(PoseTriggerHint trigger) =>
        trigger.SlashCommand is { } cmd ? $"/{cmd}" : trigger.PoseIdentifier!.Value.DisplayName;

    /// "ModName — OptionName", which identifies an animation choice (the pose slot name doesn't).
    public static string DescribePlayLabel(PoseModInfo mod, PoseModOption option) =>
        option.Name is "" or "Default" ? mod.ModName : $"{mod.ModName} — {option.Name}";

    /// DescribePlayLabel plus the trigger, when the option has more than one. Also the queue's name
    /// for this pick.
    public static string DescribeTriggerLabel(PoseModInfo mod, PoseModOption option, PoseTriggerHint trigger)
    {
        var optionLabel = DescribePlayLabel(mod, option);
        if (option.Triggers.Count <= 1) return optionLabel;
        return $"{optionLabel} ({TriggerButtonText(trigger)})";
    }

    /// Selects the option before playing when it isn't selected yet.
    public static Action? SelectOptionBeforePlay(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option, Guid? collectionId)
    {
        var alreadySelected = group.IsImplicit || group.Selected.Contains(option.Name);
        return alreadySelected || collectionId is not { } cid
            ? null
            : () => ApplyGroupChange(plugin, mod, group,
                group.MultiSelect ? new HashSet<string>(group.Selected) { option.Name } : [option.Name], cid);
    }

    public static bool ApplyGroupChange(Plugin plugin, PoseModInfo mod, PoseModGroup changedGroup,
        HashSet<string> newSelection, Guid collectionId, bool enabled = true) =>
        ApplyGroupChanges(plugin, mod, new Dictionary<PoseModGroup, HashSet<string>> { [changedGroup] = newSelection }, collectionId, enabled);

    /// Sends every group's selection, since Penumbra replaces them all at once. The implicit group
    /// isn't a real Penumbra group and is left out.
    public static bool ApplyGroupChanges(Plugin plugin, PoseModInfo mod, Dictionary<PoseModGroup, HashSet<string>> changes,
        Guid collectionId, bool enabled = true)
    {
        var allSelections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var g in mod.Groups)
        {
            if (g.IsImplicit) continue;
            allSelections[g.Name] = changes.TryGetValue(g, out var changed) ? [.. changed] : [.. g.Selected];
        }

        if (!plugin.PenumbraIpc.TrySetTemporarySettings(collectionId, mod.ModDirectory, enabled, mod.Priority, allSelections))
            return false;

        foreach (var (group, selection) in changes)
            group.Selected = selection;
        mod.Enabled = enabled;
        plugin.PenumbraIpc.TryRedrawLocalPlayer();
        return true;
    }

    public static void EnsureModEnabled(Plugin plugin, PoseModInfo mod, Guid? collectionId)
        => SetModEnabled(plugin, mod, collectionId, true);

    /// Keeps the current selections, since Penumbra resets any group left out.
    public static void SetModEnabled(Plugin plugin, PoseModInfo mod, Guid? collectionId, bool enabled)
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
