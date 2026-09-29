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

    // A mostly vertical part (within ~60 degrees of straight up/down) gives no reliable heading.
    private const float MinHorizontalFraction = 0.5f;

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

    /// Auto tries the shared-origin facing, then the part-direction turn with the 180-degree check.
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

        if (request.Facing == AlignFacing.Auto && rotationApplies && SharedOriginTurn(localPlayer, partner) is { } snapped)
            return snapped;

        var (turn, note) = FacingTurn(rotationApplies);
        if (rotationApplies && ShouldFlip(turn, localPlayer, partner))
        {
            turn = MathF.IEEERemainder(turn + MathF.PI, MathF.Tau);
            note = $", flipped to {turn * 180f / MathF.PI:0} deg — the bodies overlapped the other way";
        }
        return (turn, note, AlignFacing.PartDirection);
    }

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
    private (float Turn, string Note) FacingTurn(bool rotationApplies)
    {
        if (!rotationApplies)
            return (0f, ", facing unchanged: rotation offset unavailable");
        if (bestMyDirection is not { } mine || bestTheirDirection is not { } theirs)
            return (0f, ", facing unchanged: couldn't read which way the parts face");
        if (!IsMostlyHorizontal(mine) || !IsMostlyHorizontal(theirs))
            return (0f, ", facing unchanged: a part is too vertical to judge");

        var turn = MathF.IEEERemainder(Heading(-theirs) - Heading(mine), MathF.Tau);
        if (MathF.Abs(turn) < MinFacingTurn)
            return (0f, "");

        return (turn, $", turned {turn * 180f / MathF.PI:0} deg");
    }

    // Couple animations are usually authored with both characters on one spot.
    private static readonly AlignFacing[] SharedOriginFacings =
        [AlignFacing.SameWay, AlignFacing.Facing, AlignFacing.QuarterLeft, AlignFacing.QuarterRight];

    // Generous enough for different body proportions.
    private const float SharedOriginTolerance = 0.3f;

    /// For a couple animation authored on one spot: the fixed facing whose alignment also puts both
    /// drawn models on the same spot. Null when none does.
    private (float Turn, string Note, AlignFacing Resolved)? SharedOriginTurn(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        if (!bestModelsKnown) return null;

        var actual = bestActualPosition;
        (float Turn, AlignFacing Facing, float Miss)? best = null;
        foreach (var facing in SharedOriginFacings)
        {
            var turn = MathF.IEEERemainder(bestTheirModelYaw + FixedFacingAngle(facing)!.Value - bestMyModelYaw, MathF.Tau);
            var yaw = Yaw(turn);
            var mineAfterTurn = actual + Vector3.Transform(bestMine - actual, yaw);
            var shift = StopShort(bestTheirs, mineAfterTurn, localPlayer, partner) - mineAfterTurn;
            var modelAfter = actual + Vector3.Transform(bestMyModel - actual, yaw) + shift;
            var miss = new Vector2(modelAfter.X - bestTheirModel.X, modelAfter.Z - bestTheirModel.Z).Length();
            if (best is not { } b || miss < b.Miss)
                best = (turn, facing, miss);
        }

        if (best is not { } found || found.Miss > SharedOriginTolerance) return null;
        return (found.Turn, $"{TurnNote(found.Turn)}, {FacingLabel(found.Facing)}", found.Facing);
    }

    // Outline bones closer than this count as bodies passing through each other.
    private const float OverlapDistance = 0.15f;

    // The flip must clearly reduce overlap to win.
    private const float FlipMinGain = 0.1f;
    private const float FlipMaxRatio = 0.7f;

    /// Part directions can't always tell "same way" from "facing each other", so compare the turn
    /// with the turn plus 180 degrees and keep whichever overlaps the bodies less.
    private bool ShouldFlip(float turn, IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        if (outlineFrames.Count == 0) return false;
        var kept = Overlap(turn, localPlayer, partner);
        var flipped = Overlap(turn + MathF.PI, localPlayer, partner);
        return flipped < kept - FlipMinGain && flipped < kept * FlipMaxRatio;
    }

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

    private static bool IsMostlyHorizontal(Vector3 direction)
    {
        var length = direction.Length();
        if (length < 1e-4f) return false;
        return new Vector2(direction.X, direction.Z).Length() / length >= MinHorizontalFraction;
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
