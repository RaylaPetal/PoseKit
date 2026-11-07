using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
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

    /// The option name without its number, spacing or case, so a sibling mod's copy matches. Part of
    /// the pairing protocol: changing it breaks role matching with other versions.
    private static string OptionKeyHash(string optionName) =>
        ModDirectoryHash.Compute(NormalizeOptionName(optionName).ToLowerInvariant());

    // The number must be set apart ("176 ", "121. ", "3)"), so "3some" or "2B" keep theirs.
    private static readonly Regex LeadingNumber = new(@"^\s*\d+(?:[.)]\s*|\s+)", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// The option name without a leading number ("176 ", "121. ") and with runs of spaces collapsed.
    /// A name that is only a number keeps it, so options "1", "2" and "3" stay apart.
    private static string NormalizeOptionName(string name)
    {
        var collapsed = Whitespace.Replace(name, " ").Trim();
        var stripped = LeadingNumber.Replace(collapsed, "").Trim();
        return stripped.Length > 0 ? stripped : collapsed;
    }

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

    /// One role of an option, identified so the partner's side can find it. See FindRole.
    public static RoleHashes HashesFor(PoseModInfo mod, PoseModGroup group, PoseModOption option, PoseTriggerHint trigger) =>
        new(ModDirectoryHash.Compute(mod.ModDirectory),
            group.IsImplicit ? "0" : ModDirectoryHash.Compute(group.Name),
            ModDirectoryHash.Compute(option.Name),
            ModDirectoryHash.Compute(TriggerText(trigger)),
            OptionKeyHash(option.Name));

    /// Offers the partner their role of a scene; this side plays its own once they accept. The name
    /// states their role because it is also what an older partner's prompt shows, and answers match
    /// on it.
    public static void OfferScene(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option, SceneRole own, SceneRole theirs)
    {
        var title = SceneRoles.OfferTitle(option.Name is "" or "Default" ? mod.ModName : option.Name);
        var name = $"{title} (you: {theirs.Label}, {TriggerButtonText(theirs.Trigger)})";
        plugin.CoupleRelayOutbox.StartRole(name, HashesFor(mod, group, option, theirs.Trigger), () =>
        {
            // Selection is read when it plays, since a play may have just changed it.
            var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
            PlayOptionTrigger(plugin, mod, group, option, own.Trigger, collectionId,
                SelectOptionBeforePlay(plugin, mod, group, option, collectionId));
        });
    }

    /// The scene a received role belongs to and which side it is, when this side can offer it back
    /// reversed. Null for a role this side doesn't have or can't read as a scene.
    public static (PoseModInfo Mod, PoseModGroup Group, PoseModOption Option, SceneRole Offered, SceneRole Opposite)? FindSwappable(Plugin plugin, RoleHashes role)
    {
        if (FindRole(plugin, role) is not { } found) return null;
        var (mod, group, option, trigger) = found;
        if (SceneRoles.TryParse(option.Name, group.Name, mod.ModName, option.Triggers) is not { } scene ||
            scene.OppositeOf(trigger) is not { } opposite)
            return null;

        var offered = scene.Dominant.Trigger == trigger ? scene.Dominant : scene.Other;
        return (mod, group, option, offered, opposite);
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

    /// <param name="joinPartner">False when something else places the character, such as a seat placement.</param>
    public static void PlayOptionTrigger(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option,
        PoseTriggerHint trigger, Guid? collectionId, Action? beforePlay = null, bool joinPartner = true)
    {
        beforePlay?.Invoke();
        EnsureModEnabled(plugin, mod, collectionId);
        CapturePenumbraContext(plugin, mod, group, option);
        PlayTrigger(plugin, trigger, joinPartner ? JointPlayAnchor(plugin) : null);
        plugin.SetPlayContext(mod.ModDirectory, mod.ModName, group.IsImplicit ? "" : group.Name, option.Name,
            TriggerText(trigger), trigger.PoseIdentifier);

        var pairing = plugin.PairingState;
        plugin.PlayVerifier.Queue(mod, option);
        plugin.RecentPlays.Record(pairing.Active && !pairing.SoloPlayEnabled ? pairing.Peer : null, $"{mod.ModDirectory}|{group.Name}|{option.Name}");
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

    /// Makes this option the one that plays, when invoked. It's worked out then rather than here: the
    /// main window calls this every frame, and a queued pick may play long after it's clicked.
    public static Action? SelectOptionBeforePlay(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option, Guid? collectionId) =>
        collectionId is not { } cid ? null : () => MakeExclusive(plugin, mod, group, option, cid);

    /// Clears overlapping options in this mod and outranks other mods actively claiming its files. Only
    /// temporary settings change, and nothing happens (so no redraw) when there's nothing to fix.
    private static void MakeExclusive(Plugin plugin, PoseModInfo mod, PoseModGroup group, PoseModOption option, Guid collectionId)
    {
        var groups = mod.Groups.Select(candidate => new ExclusiveGroup(candidate.Name, !candidate.MultiSelect, candidate.IsImplicit,
            candidate.Options.Select(o => new ExclusiveOption(o.Name, o.AnimationRedirects.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase))).ToList(),
            candidate.Selected)).ToList();
        var changes = ExclusivePlay.SelectionChanges(groups, group.Name, option.Name);
        var byGroup = changes.ToDictionary(change => mod.Groups.First(candidate => candidate.Name == change.Key), change => change.Value);

        var priority = mod.Priority;
        if (plugin.PenumbraIpc.TryGetAllSettings(collectionId) is { } settings)
        {
            var claimants = plugin.AnimationClaims.ActiveClaimants(option.AnimationRedirects.Keys, mod.ModDirectory, settings);
            priority = ExclusivePlay.RequiredPriority(mod.Priority, claimants.Select(claimant => claimant.Priority));
        }

        if (byGroup.Count == 0 && priority == mod.Priority) return;

        var previous = mod.Priority;
        mod.Priority = priority;
        if (!ApplyGroupChanges(plugin, mod, byGroup, collectionId))
            mod.Priority = previous;
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

    /// While paired, every play is a joint one (a solo play goes through the bypass), so the character
    /// stands on the partner's spot facing their way: couple animations are made with both on one
    /// origin. The offset is only drawn on this player's screen, so each side moves itself; when
    /// SimpleHeels shares offsets with others, only one side may move or they'd swap places.
    private static PresetAnchor? JointPlayAnchor(Plugin plugin)
    {
        var pairing = plugin.PairingState;
        if (!pairing.Active || pairing.SoloPlayEnabled || pairing.Peer is not { } peer) return null;

        var offsetsShared = plugin.Configuration.BridgeOffsetToSimpleHeels && plugin.SimpleHeelsBridge.IsLoaded;
        if (offsetsShared && Plugin.ObjectTable.LocalPlayer is { } localPlayer &&
            string.CompareOrdinal(PartnerIdentity.FromCharacter(localPlayer).TellAddress, peer.TellAddress) > 0)
            return null;

        return PresetAnchor.FromPartner(new PartnerAnchor { Partner = peer });
    }

    private static void PlayTrigger(Plugin plugin, PoseTriggerHint trigger, PresetAnchor? anchor)
    {
        if (trigger.SlashCommand is { } command)
            plugin.PoseTrigger.TriggerCommand(command, PoseOffset.Zero, anchor);
        else if (trigger.PoseIdentifier is { } identifier)
            plugin.PoseTrigger.Trigger(identifier, PoseOffset.Zero, anchor);
    }
}
