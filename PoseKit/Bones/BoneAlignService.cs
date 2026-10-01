using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Pairing;
using PoseKit.Presets;
using PoseKit.Sync;

namespace PoseKit.Bones;

/// <summary>
/// Lines up one of this player's body parts with the partner's by adjusting this player's render
/// offset. Bones and real positions are never written.
///
/// Resyncs both emotes, samples both parts over a short window and uses the closest moment, since
/// many loops only bring the parts together partway through. If both sides align at once, the side
/// whose "Name@World" sorts first wins.
/// </summary>
public sealed class BoneAlignService : IDisposable
{
    private enum Phase { Idle, Settling, Sampling }

    // Long enough for the emote-loop reset to take effect before measuring.
    private const long SettleMs = 300;

    // Long enough to catch the contact moment of a typical short loop.
    private const long SampleMs = 1500;

    // How long after a partner's announcement their Align is still considered in progress.
    private const long PartnerAlignWindowMs = SettleMs + SampleMs + 500;

    // Past this the rendered model visibly drifts from the real character.
    public const float MaxAlignDistance = 1.0f;

    private readonly Configuration configuration;
    private readonly PairingState pairingState;
    private readonly PairingListener pairingListener;
    private readonly PoseTrigger poseTrigger;
    private readonly OffsetEngine offsetEngine;
    private readonly EmoteSyncCommand emoteSync;

    private Phase phase = Phase.Idle;
    private long phaseStartedAt;
    private long partnerAlignUntil;

    // Captured at start so a mid-sample change of pose, offset or partner cancels cleanly.
    private PoseIdentifier poseAtStart;
    private int offsetGenerationAtStart;
    private ulong partnerObjectId;
    private AlignRequest request = new(BodyPart.Penis, BodyPart.Vagina, 0f, AlignFacing.Unchanged, AlignOrigin.Manual);
    private BodyPart selfPart => request.Self;
    private BodyPart partnerPart => request.Partner;

    private bool NeedsFacingSamples => request.Facing is AlignFacing.Auto or AlignFacing.PartDirection;

    // The closest sampled moment. The facing turn pivots around the real position.
    private bool sampled;
    private float bestDistance;
    private Vector3 bestMine;
    private Vector3 bestTheirs;
    private Vector3 bestActualPosition;
    private Vector3? bestMyDirection;
    private Vector3? bestTheirDirection;

    // Both drawn models at that moment, for the shared-origin facing.
    private bool bestModelsKnown;
    private Vector3 bestMyModel;
    private float bestMyModelYaw;
    private Vector3 bestTheirModel;
    private float bestTheirModelYaw;

    // Both body outlines at that moment, to tell a shared origin from bodies drawn inside each other.
    private Vector3?[] bestMyOutline = [];
    private Vector3?[] bestTheirOutline = [];

    // Both body outlines on every sampled frame, for the 180-degree check.
    private readonly List<(Vector3?[] Mine, Vector3?[] Theirs)> outlineFrames = [];

    public string Status { get; private set; } = "";

    public bool IsAligning => phase != Phase.Idle;

    /// Carries the facing actually used (never Auto).
    public event Action<AlignRequest, AlignFacing>? Aligned;

    public BoneAlignService(Configuration configuration, PairingState pairingState, PairingListener pairingListener,
        PoseTrigger poseTrigger, OffsetEngine offsetEngine, EmoteSyncCommand emoteSync)
    {
        this.configuration = configuration;
        this.pairingState = pairingState;
        this.pairingListener = pairingListener;
        this.poseTrigger = poseTrigger;
        this.offsetEngine = offsetEngine;
        this.emoteSync = emoteSync;
        pairingListener.PartnerBoneAlignStarted += OnPartnerAlignStarted;
    }

    public void Dispose()
    {
        pairingListener.PartnerBoneAlignStarted -= OnPartnerAlignStarted;
        poseTrigger.PartnerTrackingPaused = false;
    }

    public void Start(AlignRequest alignRequest)
    {
        if (IsAligning) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;
        request = alignRequest;

        if (PoseIdentifier.FromCharacter(localPlayer) is not { } pose)
        {
            SetStatus("Start a pose or emote first.");
            return;
        }

        if (ResolvePartner(localPlayer, out var failure) is not { } partner)
        {
            SetStatus(failure);
            return;
        }

        if (!BodyParts.TryLocate(localPlayer, selfPart, out _))
        {
            SetStatus($"Couldn't find your {BodyParts.DisplayName(selfPart).ToLowerInvariant()} bone.");
            return;
        }

        if (!BodyParts.TryLocate(partner, partnerPart, out _))
        {
            SetStatus($"Couldn't find {partner.Name.TextValue}'s {BodyParts.DisplayName(partnerPart).ToLowerInvariant()} bone — " +
                      "their mods may still be loading.");
            return;
        }

        if (pairingState.Active)
        {
            if (Environment.TickCount64 < partnerAlignUntil && !ThisSideGoesFirst())
            {
                YieldToPartner();
                return;
            }
            // Only a button press may send a tell, so auto-align stays silent.
            if (request.Origin == AlignOrigin.Manual)
                pairingListener.AnnounceBoneAlign();
        }

        poseAtStart = pose;
        offsetGenerationAtStart = poseTrigger.OffsetGeneration;
        partnerObjectId = partner.GameObjectId;
        sampled = false;
        bestDistance = float.MaxValue;
        outlineFrames.Clear();
        bestMyOutline = [];
        bestTheirOutline = [];

        emoteSync.Sync();
        poseTrigger.PartnerTrackingPaused = true;
        phase = Phase.Settling;
        phaseStartedAt = Environment.TickCount64;
        SetStatus($"Aligning {BodyParts.DisplayName(selfPart)} -> {BodyParts.DisplayName(partnerPart)}...");
    }

    private void SetStatus(string text) =>
        Status = request.Origin == AlignOrigin.Memory ? $"From memory: {text}" : text;

    public IPlayerCharacter? FindPartner(IPlayerCharacter localPlayer) => ResolvePartner(localPlayer, out _);

    /// The paired partner, or else the targeted player.
    private IPlayerCharacter? ResolvePartner(IPlayerCharacter localPlayer, out string failure)
    {
        failure = "";
        if (pairingState.Active && pairingState.Peer is { } peer)
        {
            var paired = PartnerAnchor.TryFindLive(peer);
            if (paired == null) failure = $"{peer.Name} isn't nearby.";
            return paired;
        }

        if ((Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget) is IPlayerCharacter target &&
            target.GameObjectId != localPlayer.GameObjectId)
            return target;

        failure = "Pair with someone or target a player first.";
        return null;
    }

    public void Tick()
    {
        if (phase == Phase.Idle) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || PoseIdentifier.FromCharacter(localPlayer) != poseAtStart ||
            poseTrigger.OffsetGeneration != offsetGenerationAtStart)
        {
            Cancel("Alignment cancelled — the pose or offset changed.");
            return;
        }

        if (Plugin.ObjectTable.SearchById(partnerObjectId) is not IPlayerCharacter partner)
        {
            Cancel("Alignment cancelled — your partner is no longer nearby.");
            return;
        }

        var elapsed = Environment.TickCount64 - phaseStartedAt;

        if (phase == Phase.Settling)
        {
            if (elapsed < SettleMs) return;
            phase = Phase.Sampling;
            phaseStartedAt = Environment.TickCount64;
            return;
        }

        if (BodyParts.TryLocate(localPlayer, selfPart, out var mine) &&
            BodyParts.TryLocate(partner, partnerPart, out var theirs))
        {
            var distance = (theirs - mine).Length();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestMine = mine;
                bestTheirs = theirs;
                bestActualPosition = localPlayer.Position;
                bestMyDirection = BodyParts.TryGetDirection(localPlayer, selfPart, out var myDirection) ? myDirection : null;
                bestTheirDirection = BodyParts.TryGetDirection(partner, partnerPart, out var theirDirection) ? theirDirection : null;
                bestModelsKnown = BoneReader.TryGetModelTransform(localPlayer, out bestMyModel, out bestMyModelYaw) &
                                  BoneReader.TryGetModelTransform(partner, out bestTheirModel, out bestTheirModelYaw);
                if (NeedsFacingSamples)
                {
                    bestMyOutline = BoneReader.GetBonePositions(localPlayer, BodyParts.BodyOutline);
                    bestTheirOutline = BoneReader.GetBonePositions(partner, BodyParts.BodyOutline);
                }
                sampled = true;
            }
        }

        if (NeedsFacingSamples)
            outlineFrames.Add((BoneReader.GetBonePositions(localPlayer, BodyParts.BodyOutline),
                               BoneReader.GetBonePositions(partner, BodyParts.BodyOutline)));

        if (elapsed >= SampleMs)
            Finish(localPlayer, partner);
    }

    // Turns smaller than this are left alone — measurement noise, not a wrong facing.
    private const float MinFacingTurn = 10f * MathF.PI / 180f;

    // A part tilted more than ~45 degrees from level gives no reliable heading: its short horizontal
    // component swings with small tilts.
    private const float MinHorizontalFraction = 0.7f;

    private void Finish(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        phase = Phase.Idle;
        poseTrigger.PartnerTrackingPaused = false;

        if (!sampled)
        {
            SetStatus("Couldn't read the bones while aligning.");
            return;
        }

        if (bestDistance > MaxAlignDistance)
        {
            SetStatus($"Too far apart to align ({bestDistance:0.00}y, max {MaxAlignDistance:0.0}y) — move closer first.");
            return;
        }

        // The drawn model is placed at actual position A, facing f (actual rotation plus the rotation
        // offset, when the rotation hook applies one), with the offset's position in that local frame.
        // Adding a turn d spins the whole drawn model — offset included — around A, so this player's
        // part moves from B to A + R(d)(B - A). Solving for the extra local offset that then lands it
        // on target T gives:  dP = R(f + d)^-1 (T - A)  -  R(f)^-1 (B - A).  With d = 0 this is just
        // R(f)^-1 (T - B), the position-only move.
        var rotationApplies = offsetEngine.RotationHookResolved;
        var facing = localPlayer.Rotation + (rotationApplies ? offsetEngine.DesiredOffset.Rotation : 0f);
        var (turn, facingNote, resolvedFacing) = ChooseFacing(rotationApplies, localPlayer, partner);

        var actual = bestActualPosition;
        var mineAfterTurn = actual + Vector3.Transform(bestMine - actual, Yaw(turn));
        var target = StopShort(bestTheirs, mineAfterTurn, localPlayer, partner);
        var positionChange = Vector3.Transform(target - actual, Quaternion.Inverse(Yaw(facing + turn)))
                             - Vector3.Transform(bestMine - actual, Quaternion.Inverse(Yaw(facing)));

        // Bones were measured with the previous correction applied, so add to it.
        var previous = poseTrigger.BoneCorrection;
        poseTrigger.SetBoneCorrection(new PoseOffset
        {
            Position = previous.Position + positionChange,
            Rotation = previous.Rotation + turn,
        });

        SetStatus($"Aligned {BodyParts.DisplayName(selfPart)} -> {BodyParts.DisplayName(partnerPart)} " +
                  $"({bestDistance:0.00}y, {request.Gap:0.00}y gap{facingNote}).");
        Aligned?.Invoke(request, resolvedFacing);
    }

    /// Auto tries the shared-origin facing, then the part-direction turn. Either only turns when
    /// confident; otherwise the facing is kept, since no turn beats a wrong one.
    private (float Turn, string Note, AlignFacing Resolved) ChooseFacing(bool rotationApplies, IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        if (request.Facing == AlignFacing.Unchanged)
            return (0f, "", AlignFacing.Unchanged);

        if (FixedFacingAngle(request.Facing) is { } angle)
        {
            if (!rotationApplies) return (0f, ", facing unchanged: rotation offset unavailable", request.Facing);
            if (!bestModelsKnown) return (0f, ", facing unchanged: couldn't read the models' facing", request.Facing);
            var fixedTurn = MathF.IEEERemainder(bestTheirModelYaw + angle - bestMyModelYaw, MathF.Tau);
            return (fixedTurn, TurnNote(fixedTurn) + $", {FacingLabel(request.Facing)}", request.Facing);
        }

        var sharedLog = "shared-origin skipped";
        if (request.Facing == AlignFacing.Auto && rotationApplies)
        {
            var snapped = SharedOriginTurn(localPlayer, partner, out sharedLog);
            if (snapped is { } found)
            {
                LogFacing(sharedLog);
                return found;
            }
        }

        var (turn, note) = FacingTurn(rotationApplies, out var directionLog);
        var overlapLog = "";
        if (turn != 0f && outlineFrames.Count > 0)
        {
            // Overlap is biased against close-contact poses, so it may only veto a turn, never choose one.
            var kept = Overlap(turn, localPlayer, partner);
            var flipped = Overlap(turn + MathF.PI, localPlayer, partner);
            var veto = ClearlyLess(flipped, kept);
            overlapLog = $", overlap kept={kept:0.00} flipped={flipped:0.00}{(veto ? " -> veto" : "")}";
            if (veto)
            {
                turn = 0f;
                note = ", facing unchanged: part directions and body overlap disagreed";
            }
        }
        LogFacing($"{sharedLog}; part-direction {directionLog}{overlapLog} -> turn {turn * 180f / MathF.PI:0} deg");
        return (turn, note, AlignFacing.PartDirection);
    }

    private static void LogFacing(string text) => Plugin.Log.Debug($"Bone Align facing: {text}");

    private static string TurnNote(float turn) =>
        MathF.Abs(turn) < MinFacingTurn ? "" : $", turned {turn * 180f / MathF.PI:0} deg";

    private static float? FixedFacingAngle(AlignFacing facing) => facing switch
    {
        AlignFacing.SameWay => 0f,
        AlignFacing.Facing => MathF.PI,
        AlignFacing.QuarterLeft => MathF.PI / 2,
        AlignFacing.QuarterRight => -MathF.PI / 2,
        _ => null,
    };

    public static string FacingLabel(AlignFacing facing) => facing switch
    {
        AlignFacing.SameWay => "same way as partner",
        AlignFacing.Facing => "facing partner",
        AlignFacing.QuarterLeft => "partner's left",
        AlignFacing.QuarterRight => "partner's right",
        AlignFacing.PartDirection => "parts facing each other",
        AlignFacing.Unchanged => "facing unchanged",
        _ => "auto facing",
    };

    /// The turn that makes the two parts face each other, or zero with the reason.
    private (float Turn, string Note) FacingTurn(bool rotationApplies, out string log)
    {
        log = "skipped";
        if (!rotationApplies)
            return (0f, ", facing unchanged: rotation offset unavailable");
        if (bestMyDirection is not { } mine || bestTheirDirection is not { } theirs)
        {
            log = "directions unreadable";
            return (0f, ", facing unchanged: couldn't read which way the parts face");
        }

        var turn = MathF.IEEERemainder(Heading(-theirs) - Heading(mine), MathF.Tau);
        log = $"mine {Heading(mine) * 180f / MathF.PI:0} deg level={HorizontalFraction(mine):0.00}, " +
              $"theirs {Heading(theirs) * 180f / MathF.PI:0} deg level={HorizontalFraction(theirs):0.00}, " +
              $"raw turn {turn * 180f / MathF.PI:0} deg";

        if (HorizontalFraction(mine) < MinHorizontalFraction || HorizontalFraction(theirs) < MinHorizontalFraction)
            return (0f, ", facing unchanged: a part is too vertical to judge");
        if (MathF.Abs(turn) < MinFacingTurn)
            return (0f, "");

        return (turn, $", turned {turn * 180f / MathF.PI:0} deg");
    }

    // Couple animations are usually authored with both characters on one spot.
    private static readonly AlignFacing[] SharedOriginFacings =
        [AlignFacing.SameWay, AlignFacing.Facing, AlignFacing.QuarterLeft, AlignFacing.QuarterRight];

    // A facing authored on one spot lands close to 0; poses authored apart missed by 0.2y and more.
    private const float SharedOriginTolerance = 0.15f;

    // The best must also miss by at most this fraction of the runner-up. A facing that only fits by
    // coincidence misses by about as much as the others.
    private const float SharedOriginClearRatio = 0.6f;

    // Matching outline bones closer than this on average (horizontally) mean the bodies are drawn
    // inside each other. Two emotes that each reach the part forward (a kiss) line up their whole
    // bodies under "same way", which fits the origins perfectly but is never the authored pose.
    private const float StackedSpread = 0.1f;

    // Fewer readable matching bones than this can't tell stacked bodies apart.
    private const int MinStackBones = 6;

    /// For a couple animation authored on one spot: the fixed facing whose alignment also puts both
    /// drawn models on the same spot without stacking the bodies. Null unless one facing clearly does.
    private (float Turn, string Note, AlignFacing Resolved)? SharedOriginTurn(IPlayerCharacter localPlayer, IPlayerCharacter partner, out string log)
    {
        if (!bestModelsKnown)
        {
            log = "shared-origin skipped: models unknown";
            return null;
        }

        var actual = bestActualPosition;
        var candidates = new List<(float Turn, AlignFacing Facing, float Miss, float? Spread)>();
        foreach (var facing in SharedOriginFacings)
        {
            var turn = MathF.IEEERemainder(bestTheirModelYaw + FixedFacingAngle(facing)!.Value - bestMyModelYaw, MathF.Tau);
            var yaw = Yaw(turn);
            var mineAfterTurn = actual + Vector3.Transform(bestMine - actual, yaw);
            var shift = StopShort(bestTheirs, mineAfterTurn, localPlayer, partner) - mineAfterTurn;
            var modelAfter = actual + Vector3.Transform(bestMyModel - actual, yaw) + shift;
            var miss = new Vector2(modelAfter.X - bestTheirModel.X, modelAfter.Z - bestTheirModel.Z).Length();
            candidates.Add((turn, facing, miss, OutlineSpread(yaw, shift)));
        }
        candidates.Sort((a, b) => a.Miss.CompareTo(b.Miss));

        var (best, second) = (candidates[0], candidates[1]);
        var stacked = best.Spread is not { } spread || spread < StackedSpread;
        var confident = best.Miss <= SharedOriginTolerance && best.Miss <= SharedOriginClearRatio * second.Miss && !stacked;
        var verdict = confident ? $"confident {best.Facing}"
            : best.Miss > SharedOriginTolerance ? "best miss too large"
            : best.Miss > SharedOriginClearRatio * second.Miss ? $"not clearly better than {second.Facing}"
            : best.Spread is null ? "couldn't read the bodies"
            : $"{best.Facing} stacks the bodies";

        var listed = new List<string>();
        foreach (var c in candidates)
            listed.Add(c.Spread is { } s ? $"{c.Facing} miss={c.Miss:0.00} spread={s:0.00}" : $"{c.Facing} miss={c.Miss:0.00}");
        log = $"shared-origin {string.Join(" | ", listed)} -> {verdict}";

        if (!confident) return null;
        return (best.Turn, $"{TurnNote(best.Turn)}, {FacingLabel(best.Facing)}", best.Facing);
    }

    /// Average horizontal distance between same-named outline bones after turning this player by
    /// <paramref name="yaw"/> and shifting by <paramref name="shift"/>, at the sampled moment.
    /// Null when too few bones were read on both bodies.
    private float? OutlineSpread(Quaternion yaw, Vector3 shift)
    {
        var actual = bestActualPosition;
        var total = 0f;
        var count = 0;
        for (var i = 0; i < bestMyOutline.Length && i < bestTheirOutline.Length; i++)
        {
            if (bestMyOutline[i] is not { } m || bestTheirOutline[i] is not { } t) continue;
            var moved = actual + Vector3.Transform(m - actual, yaw) + shift;
            total += new Vector2(moved.X - t.X, moved.Z - t.Z).Length();
            count++;
        }
        return count < MinStackBones ? null : total / count;
    }

    // Outline bones closer than this count as bodies passing through each other.
    private const float OverlapDistance = 0.15f;

    // An alternative turn must clearly reduce overlap to win.
    private const float FlipMinGain = 0.1f;
    private const float FlipMaxRatio = 0.7f;

    /// Part directions can't always tell "same way" from "facing each other", so the turn is compared
    /// with the turn plus 180 degrees, keeping the flip only when it clearly overlaps less.
    private static bool ClearlyLess(float overlap, float other) =>
        overlap < other - FlipMinGain && overlap < other * FlipMaxRatio;

    /// Average overlap per sampled frame after turning and shifting this player by the given turn.
    private float Overlap(float turn, IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        var actual = bestActualPosition;
        var yaw = Yaw(turn);
        var mineAfterTurn = actual + Vector3.Transform(bestMine - actual, yaw);
        var shift = StopShort(bestTheirs, mineAfterTurn, localPlayer, partner) - mineAfterTurn;

        var total = 0f;
        foreach (var (mine, theirs) in outlineFrames)
        {
            foreach (var m in mine)
            {
                if (m is not { } mp) continue;
                var moved = actual + Vector3.Transform(mp - actual, yaw) + shift;
                foreach (var t in theirs)
                {
                    if (t is not { } tp) continue;
                    var d = Vector3.Distance(moved, tp);
                    if (d < OverlapDistance) total += OverlapDistance - d;
                }
            }
        }
        return total / outlineFrames.Count;
    }

    /// How level a direction is: 1 when level, 0 when straight up or down.
    private static float HorizontalFraction(Vector3 direction)
    {
        var length = direction.Length();
        if (length < 1e-4f) return 0f;
        return new Vector2(direction.X, direction.Z).Length() / length;
    }

    /// The game's facing convention: a character with rotation r faces (sin r, 0, cos r).
    private static float Heading(Vector3 direction) => MathF.Atan2(direction.X, direction.Z);

    private static Quaternion Yaw(float angle) => Quaternion.CreateFromYawPitchRoll(angle, 0, 0);

    private const float TouchingDistance = 0.005f;

    /// The target: the configured gap short of the partner's part, along the approach line. When the
    /// parts already touch, backs off horizontally away from the partner.
    private Vector3 StopShort(Vector3 theirs, Vector3 mine, IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        var room = request.Gap;
        if (room <= 0f) return theirs;

        var gap = theirs - mine;
        var distance = gap.Length();
        Vector3 approach;
        if (distance > TouchingDistance)
        {
            approach = gap / distance;
        }
        else
        {
            var towardPartner = partner.Position - localPlayer.Position;
            towardPartner.Y = 0f;
            if (towardPartner.LengthSquared() < 1e-6f) return theirs;
            approach = Vector3.Normalize(towardPartner);
        }

        return theirs - approach * room;
    }

    private void Cancel(string reason)
    {
        phase = Phase.Idle;
        poseTrigger.PartnerTrackingPaused = false;
        SetStatus(reason);
    }

    private void OnPartnerAlignStarted(PartnerIdentity sender)
    {
        partnerAlignUntil = Environment.TickCount64 + PartnerAlignWindowMs;
        if (IsAligning && !ThisSideGoesFirst())
            YieldToPartner();
    }

    private void YieldToPartner()
    {
        Cancel("Your partner is aligning instead.");
        if (request.Origin == AlignOrigin.Manual)
            Plugin.ChatGui.Print("[PoseKit] Your partner is aligning instead.");
    }

    /// Both sides compare the same two names, so they always agree.
    private bool ThisSideGoesFirst()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || pairingState.Peer is not { } peer) return true;
        var own = new PartnerIdentity(localPlayer.Name.TextValue, localPlayer.HomeWorld.Value.Name.ExtractText());
        return string.CompareOrdinal(own.TellAddress, peer.TellAddress) < 0;
    }
}
