using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Pairing;

namespace PoseKit.Bones;

/// <summary>
/// Connects alignment memory to Bone Align. Records each successful manual Align against the
/// animation playing (Plugin.CurrentPlayContext). When a remembered animation starts, it fills the Bone
/// Align dropdowns with the entry's parts (always, so a manual Align uses them too), then starts one
/// silent memory-origin Align once this player and their partner (the paired partner, or else the
/// targeted player) are both posing.
///
/// A play is one PlayContext instance, so each play is handled once: looping, Bone Align's own
/// correction and the Reset button never count as a new play.
///
/// Sends nothing (PairingSender's one-click-one-tell rule). With offsets shared through the
/// SimpleHeels bridge, both sides moving would double the correction, so only the side whose
/// "Name@World" sorts first auto-aligns. With local offsets each side's correction is only on its own
/// screen, so both may.
/// </summary>
public sealed class AutoAlignCoordinator : IDisposable
{
    // How long after a play starts to keep waiting for both animations (and a partner) before giving
    // up — room for an intro, a mod redraw on each side, and the partner's emote arriving later.
    private const long GiveUpMs = 15000;

    private readonly Plugin plugin;
    private readonly AnimationReadiness readiness = new();

    /// What a pending auto-align is waiting for, or empty — shown in the Bone Align section.
    public string Waiting { get; private set; } = "";

    private PlayContext? handled;
    private AlignmentEntry? handledEntry;
    private bool done;

    public AutoAlignCoordinator(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.BoneAlign.Aligned += OnAligned;
    }

    public void Dispose() => plugin.BoneAlign.Aligned -= OnAligned;

    /// The remembered entry for the animation playing right now, if any — for the Bone Align section.
    public AlignmentEntry? CurrentEntry =>
        plugin.CurrentPlayContext is { } context ? plugin.AlignmentMemory.TryGet(context.Key) : null;

    private void OnAligned(AlignRequest request, AlignFacing resolvedFacing)
    {
        if (request.Origin != AlignOrigin.Manual || plugin.CurrentPlayContext is not { } context) return;
        plugin.AlignmentMemory.Record(new AlignmentEntry
        {
            Key = context.Key,
            ModDirectory = context.ModDirectory,
            ModName = context.ModName,
            Group = context.Group,
            Option = context.Option,
            Trigger = context.Trigger,
            Self = request.Self,
            Partner = request.Partner,
            Gap = request.Gap,
            Facing = resolvedFacing,
            Updated = DateTime.UtcNow,
        });
    }

    /// Called every framework tick from Plugin.
    public void Tick()
    {
        var context = plugin.CurrentPlayContext;
        if (context == null)
        {
            handled = null;
            Waiting = "";
            return;
        }

        if (!ReferenceEquals(context, handled))
        {
            handled = context;
            done = false;
            Waiting = "";
            readiness.Reset();
            handledEntry = plugin.AlignmentMemory.TryGet(context.Key);
            if (handledEntry is { } entry)
                FillDropdowns(entry);
        }

        // Re-read rather than trusting handledEntry, so a Forget mid-wait stops it; but only for plays
        // that were known when they started, so a manual Align during this play doesn't trigger one.
        if (done || handledEntry == null || plugin.AlignmentMemory.TryGet(context.Key) is not { } known) return;

        var configuration = plugin.Configuration;
        if (!configuration.AutoAlignFromMemory || context.FromPartnerAnchoredPreset)
        {
            Finish("");
            return;
        }

        if (Environment.TickCount64 - context.SetAt > GiveUpMs)
        {
            Finish(Waiting.Length > 0 ? $"Auto-align skipped — still {Waiting[0..1].ToLowerInvariant()}{Waiting[1..]}" : "");
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || plugin.BoneAlign.IsAligning) return;

        // Both checked every tick, so both keep tracking how long their animation has held.
        var partner = plugin.BoneAlign.FindPartner(localPlayer);
        var ownReady = readiness.IsReady(localPlayer);
        var partnerReady = partner != null && readiness.IsReady(partner);

        if (PoseIdentifier.FromCharacter(localPlayer) is not { } pose || (context.Pose is { } wanted && pose != wanted) || !ownReady)
        {
            Waiting = "Waiting for your animation to start...";
            return;
        }
        if (partner == null)
        {
            Waiting = "Waiting for your partner or a targeted player...";
            return;
        }
        if (PoseIdentifier.FromCharacter(partner) == null || !partnerReady)
        {
            Waiting = $"Waiting for {partner.Name.TextValue}'s animation to start...";
            return;
        }

        Finish("");
        if (OffsetsShared && !SortsFirst(localPlayer, partner)) return;
        plugin.BoneAlign.Start(AlignRequest.FromMemory(known));
    }

    private void Finish(string waiting)
    {
        done = true;
        Waiting = waiting;
    }

    private void FillDropdowns(AlignmentEntry entry)
    {
        var configuration = plugin.Configuration;
        if (configuration.BoneAlignSelf == entry.Self && configuration.BoneAlignPartner == entry.Partner) return;
        configuration.BoneAlignSelf = entry.Self;
        configuration.BoneAlignPartner = entry.Partner;
        configuration.Save();
    }

    /// True when this player's offset reaches other players' screens (SimpleHeels bridge → Mare/Snowcloak).
    private bool OffsetsShared => plugin.Configuration.BridgeOffsetToSimpleHeels && plugin.SimpleHeelsBridge.IsLoaded;

    private static bool SortsFirst(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        var own = new PartnerIdentity(localPlayer.Name.TextValue, localPlayer.HomeWorld.Value.Name.ExtractText());
        var theirs = new PartnerIdentity(partner.Name.TextValue, partner.HomeWorld.Value.Name.ExtractText());
        return string.CompareOrdinal(own.TellAddress, theirs.TellAddress) < 0;
    }
}
