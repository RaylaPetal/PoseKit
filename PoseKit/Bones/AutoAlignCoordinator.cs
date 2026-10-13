using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Pairing;

namespace PoseKit.Bones;

/// <summary>
/// Records each successful manual Align or Remember, and when a remembered animation starts, runs
/// one automatic Align once both characters are posing. Each play (PlayContext instance) is handled
/// once.
///
/// When offsets are shared through SimpleHeels, only the side whose "Name@World" sorts first
/// auto-aligns, so the correction isn't applied twice.
/// </summary>
public sealed class AutoAlignCoordinator : IDisposable
{
    // Room for an intro, a redraw on each side, and the partner starting later.
    private const long GiveUpMs = 15000;

    // How long the offset must stay untouched after a hand nudge before it's saved, so a drag or a
    // run of slider clicks is saved once, at the end.
    private const long NudgeSettleMs = 1500;

    private readonly Plugin plugin;
    private readonly AnimationReadiness readiness = new();

    /// What a pending auto-align is waiting for, or empty.
    public string Waiting { get; private set; } = "";

    private PlayContext? handled;
    private AlignmentEntry? handledEntry;
    private bool done;

    // When the player last nudged a remembered animation by hand, and that play; 0/null when nothing
    // is waiting to be saved. The save only applies to that same play.
    private long nudgedAt;
    private PlayContext? nudgedPlay;

    public AutoAlignCoordinator(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.BoneAlign.Succeeded += OnSucceeded;
        plugin.PoseTrigger.ManualOffsetEdited += OnManualOffsetEdited;
    }

    public void Dispose()
    {
        plugin.BoneAlign.Succeeded -= OnSucceeded;
        plugin.PoseTrigger.ManualOffsetEdited -= OnManualOffsetEdited;
    }

    /// A nudge on an animation with a remembered alignment is saved into it once the player stops.
    /// Only while paired, so it's never measured against a targeted bystander.
    private void OnManualOffsetEdited()
    {
        if (!plugin.PairingState.Active || CurrentEntry == null) return;
        nudgedAt = Environment.TickCount64;
        nudgedPlay = plugin.CurrentPlayContext;
    }

    public AlignmentEntry? CurrentEntry =>
        plugin.CurrentPlayContext is { } context ? plugin.AlignmentMemory.TryGet(context.Key) : null;

    /// Records manual Aligns, Remember/Update memory and saved nudges; an auto-align leaves its entry alone.
    private void OnSucceeded(AlignRequest request, AlignResult result)
    {
        if (request.Origin == AlignOrigin.Memory || plugin.CurrentPlayContext is not { } context) return;
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
            RelativeYaw = result.RelativeYaw,
            ContactOffset = StoredVector.From(result.ContactOffset),
            Updated = DateTime.UtcNow,
        });
    }

    public void Tick()
    {
        var context = plugin.CurrentPlayContext;
        if (context == null)
        {
            handled = null;
            Waiting = "";
            nudgedAt = 0;
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

        TickNudge(context);

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

        // Placing on a seat or bed moves the character; aligning first would be undone by it.
        if (plugin.SeatPlacement.IsMoving)
        {
            Waiting = "Waiting for the seat placement to finish...";
            return;
        }

        Finish("");
        if (OffsetsShared && !SortsFirst(localPlayer, partner)) return;
        plugin.BoneAlign.Start(AlignRequest.FromMemory(known));
    }

    /// Saves a settled nudge by re-measuring how this player now sits against the partner. Waits
    /// while an align, a pending auto-align or a seat placement could still move things; dropped once
    /// another play started or the pairing ended.
    private void TickNudge(PlayContext context)
    {
        if (nudgedAt == 0) return;
        if (!ReferenceEquals(context, nudgedPlay) || !plugin.PairingState.Active)
        {
            nudgedAt = 0;
            nudgedPlay = null;
            return;
        }
        if (Environment.TickCount64 - nudgedAt < NudgeSettleMs) return;
        if (plugin.BoneAlign.IsAligning || (!done && handledEntry != null) || plugin.SeatPlacement.IsMoving) return;

        nudgedAt = 0;
        nudgedPlay = null;
        if (CurrentEntry is { } entry)
            plugin.BoneAlign.Start(AlignRequest.ForNudge(entry));
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

    /// True when this player's offset is shared with other players through SimpleHeels.
    private bool OffsetsShared => plugin.Configuration.BridgeOffsetToSimpleHeels && plugin.SimpleHeelsBridge.IsLoaded;

    private static bool SortsFirst(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        return string.CompareOrdinal(PartnerIdentity.FromCharacter(localPlayer).TellAddress,
            PartnerIdentity.FromCharacter(partner).TellAddress) < 0;
    }
}
