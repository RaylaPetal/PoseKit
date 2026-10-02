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

    private readonly Plugin plugin;
    private readonly AnimationReadiness readiness = new();

    /// What a pending auto-align is waiting for, or empty.
    public string Waiting { get; private set; } = "";

    private PlayContext? handled;
    private AlignmentEntry? handledEntry;
    private bool done;

    public AutoAlignCoordinator(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.BoneAlign.Succeeded += OnSucceeded;
    }

    public void Dispose() => plugin.BoneAlign.Succeeded -= OnSucceeded;

    public AlignmentEntry? CurrentEntry =>
        plugin.CurrentPlayContext is { } context ? plugin.AlignmentMemory.TryGet(context.Key) : null;

    /// Records manual Aligns and Remember/Update memory; an auto-align leaves its entry alone.
    private void OnSucceeded(AlignRequest request, AlignResult result)
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

    /// True when this player's offset is shared with other players through SimpleHeels.
    private bool OffsetsShared => plugin.Configuration.BridgeOffsetToSimpleHeels && plugin.SimpleHeelsBridge.IsLoaded;

    private static bool SortsFirst(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        var own = new PartnerIdentity(localPlayer.Name.TextValue, localPlayer.HomeWorld.Value.Name.ExtractText());
        var theirs = new PartnerIdentity(partner.Name.TextValue, partner.HomeWorld.Value.Name.ExtractText());
        return string.CompareOrdinal(own.TellAddress, theirs.TellAddress) < 0;
    }
}
