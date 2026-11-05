using System;
using System.Collections.Generic;
using System.Linq;
using PoseKit.Library;

namespace PoseKit.Bones;

/// <summary>
/// Measures which body parts come together during a paired play, without the player aligning, and
/// saves the closest pair as a suggested tag. Reads bones locally and sends nothing. Only runs while
/// paired, so it's never measured against a bystander. Each play is measured once.
/// </summary>
public sealed class ContactObserver
{
    // Long enough for a loop to come round and for auto-align to finish, short enough to stay cheap.
    private const long MeasureMs = 8000;
    private const long SampleIntervalMs = 250;

    // Partner may start up to this long after this side before the play is given up on.
    private const long GiveUpMs = 15000;

    private static readonly BodyPart[] Parts =
        [BodyPart.Penis, BodyPart.Vagina, BodyPart.Mouth, BodyPart.Anus, BodyPart.LeftFingers, BodyPart.RightFingers];

    private readonly Plugin plugin;
    private readonly AnimationReadiness readiness = new();
    private readonly Dictionary<(BodyPart, BodyPart), float> closest = new();

    private PlayContext? measuring;
    private bool finished;
    private long startedAt;
    private long nextSampleAt;

    public ContactObserver(Plugin plugin) => this.plugin = plugin;

    public void Tick()
    {
        var context = plugin.CurrentPlayContext;
        if (context == null || !plugin.PairingState.Active)
        {
            measuring = null;
            return;
        }

        if (!ReferenceEquals(context, measuring))
        {
            measuring = context;
            finished = false;
            startedAt = 0;
            closest.Clear();
            readiness.Reset();
        }
        if (finished) return;

        var now = Environment.TickCount64;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var partner = localPlayer == null ? null : plugin.BoneAlign.FindPartner(localPlayer);

        // Both checked every tick, so both keep tracking how long their animation has held.
        var ownReady = localPlayer != null && readiness.IsReady(localPlayer);
        var partnerReady = partner != null && readiness.IsReady(partner);

        if (startedAt == 0)
        {
            if (now - context.SetAt > GiveUpMs) finished = true;
            else if (ownReady && partnerReady) startedAt = now;
            return;
        }

        if (now - startedAt > MeasureMs)
        {
            finished = true;
            Finish(context);
            return;
        }

        if (now < nextSampleAt || localPlayer == null || partner == null) return;
        nextSampleAt = now + SampleIntervalMs;

        foreach (var own in Parts)
        {
            if (!BodyParts.TryLocate(localPlayer, own, out var ownAt)) continue;
            foreach (var theirs in Parts)
            {
                if (!BodyParts.TryLocate(partner, theirs, out var theirAt)) continue;
                var distance = System.Numerics.Vector3.Distance(ownAt, theirAt);
                var pair = (own, theirs);
                if (!closest.TryGetValue(pair, out var best) || distance < best)
                    closest[pair] = distance;
            }
        }
    }

    private void Finish(PlayContext context)
    {
        var contacts = closest.Select(pair => new PartContact(pair.Key.Item1, pair.Key.Item2, pair.Value)).ToList();
        if (ContactEvidence.Suggest(contacts) is not { } suggestion) return;

        var entry = plugin.LibraryIndex.FirstOrDefault(candidate =>
            candidate.Mod.ModDirectory == context.ModDirectory && candidate.Option.Name == context.Option &&
            (candidate.Group.IsImplicit ? "" : candidate.Group.Name) == context.Group);
        if (entry?.AnimationKey is not { } key) return;

        var nearest = contacts.Where(c => c.Distance <= ContactEvidence.TouchingDistance).MinBy(c => c.Distance);
        plugin.ContactObservations.Record(key, new ContactObservation
        {
            A = nearest.A,
            B = nearest.B,
            Distance = nearest.Distance,
            Suggestion = suggestion,
            Label = $"{entry.Mod.ModName} - {entry.Option.Name}",
        });
        Plugin.Log.Debug($"[PoseKit] Contact on \"{entry.Option.Name}\": {nearest.A} to {nearest.B} at {nearest.Distance:0.000}, suggests {suggestion}.");
    }
}
