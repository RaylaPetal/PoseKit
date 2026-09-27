using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Pairing;
using PoseKit.Presets;
using PoseKit.Sync;

namespace PoseKit.Bones;

/// <summary>
/// The manual Bone Align action: lines up one of this player's body parts with one of their
/// partner's, as both are drawn on this client, by folding the gap into this player's render-only
/// offset (PoseTrigger.SetBoneCorrection) — never writing bones or actual positions.
///
/// One press: guards → announce to the paired partner → resync nearby emote loops (so both
/// characters' animations restart together here) → short settle → sample both bones for a window,
/// keeping the moment they were closest → apply that gap once and leave it alone; a correctly
/// authored animation carries the rest. Sampling rather than reading one frame matters for loops
/// that start with the parts apart and bring them together partway through.
///
/// Single mover: if both paired sides press Align at overlapping times, the side whose own identity
/// sorts first (ordinal) continues and the other cancels — both compute the same answer from state
/// they already have, with no further messages.
/// </summary>
public sealed class BoneAlignService : IDisposable
{
    private enum Phase { Idle, Settling, Sampling }

    // Long enough for the emote-loop reset to take effect before measuring.
    private const long SettleMs = 300;

    // Long enough to catch the contact moment of a typical short loop; pressing Align again
    // re-samples and replaces the previous correction.
    private const long SampleMs = 1500;

    // How long after a partner's announcement their Align is still considered in progress.
    private const long PartnerAlignWindowMs = SettleMs + SampleMs + 500;

    // Past this, the rendered model would visibly drift from the character's hitbox/nameplate —
    // bigger gaps are for moving closer (or the partner anchor), not for a bone nudge.
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
    private BodyPart selfPart;
    private BodyPart partnerPart;

    // Everything about the closest sampled moment: both parts' positions and facing directions, and
    // this player's actual (server) position, which the facing turn pivots around.
    private bool sampled;
    private float bestDistance;
    private Vector3 bestMine;
    private Vector3 bestTheirs;
    private Vector3 bestActualPosition;
    private Vector3? bestMyDirection;
    private Vector3? bestTheirDirection;

    /// The outcome of the last Align (or what it's doing right now) — shown under the button.
    public string Status { get; private set; } = "";

    public bool IsAligning => phase != Phase.Idle;

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

    public void Start()
    {
        if (IsAligning) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        if (PoseIdentifier.FromCharacter(localPlayer) is not { } pose)
        {
            Status = "Start a pose or emote first.";
            return;
        }

        if (ResolvePartner(localPlayer, out var failure) is not { } partner)
        {
            Status = failure;
            return;
        }

        selfPart = configuration.BoneAlignSelf;
        partnerPart = configuration.BoneAlignPartner;

        if (!BodyParts.TryLocate(localPlayer, selfPart, out _))
        {
            Status = $"Couldn't find your {BodyParts.DisplayName(selfPart).ToLowerInvariant()} bone.";
            return;
        }

        if (!BodyParts.TryLocate(partner, partnerPart, out _))
        {
            Status = $"Couldn't find {partner.Name.TextValue}'s {BodyParts.DisplayName(partnerPart).ToLowerInvariant()} bone — " +
                     "their mods may still be loading.";
            return;
        }

        if (pairingState.Active)
        {
            if (Environment.TickCount64 < partnerAlignUntil && !ThisSideGoesFirst())
            {
                YieldToPartner();
                return;
            }
            pairingListener.AnnounceBoneAlign();
        }

        poseAtStart = pose;
        offsetGenerationAtStart = poseTrigger.OffsetGeneration;
        partnerObjectId = partner.GameObjectId;
        sampled = false;
        bestDistance = float.MaxValue;

        emoteSync.Sync();
        poseTrigger.PartnerTrackingPaused = true;
        phase = Phase.Settling;
        phaseStartedAt = Environment.TickCount64;
        Status = "Aligning...";
    }

    /// The paired partner while pairing is active; otherwise the current target, if it's another
    /// player character.
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

    /// Called every framework tick from Plugin.
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
                sampled = true;
            }
        }

        if (elapsed >= SampleMs)
            Finish(localPlayer, partner);
    }

    // Turns smaller than this are left alone — measurement noise, not a wrong facing.
    private const float MinFacingTurn = 10f * MathF.PI / 180f;

    // A part's direction must be at least this horizontal (as a fraction of its length — 0.5 is
    // within about 60 degrees of level) to give a trustworthy heading; a mostly vertical part (an
    // upright penis, a face looking at the ceiling) says nothing reliable about which way to turn.
    private const float MinHorizontalFraction = 0.5f;

    private void Finish(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        phase = Phase.Idle;
        poseTrigger.PartnerTrackingPaused = false;

        if (!sampled)
        {
            Status = "Couldn't read the bones while aligning.";
            return;
        }

        if (bestDistance > MaxAlignDistance)
        {
            Status = $"Too far apart to align ({bestDistance:0.00}y, max {MaxAlignDistance:0.0}y) — move closer first.";
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
        var (turn, facingNote) = configuration.BoneAlignMatchFacing ? FacingTurn(rotationApplies) : (0f, "");

        var actual = bestActualPosition;
        var mineAfterTurn = actual + Vector3.Transform(bestMine - actual, Yaw(turn));
        var target = StopShort(bestTheirs, mineAfterTurn, localPlayer, partner);
        var positionChange = Vector3.Transform(target - actual, Quaternion.Inverse(Yaw(facing + turn)))
                             - Vector3.Transform(bestMine - actual, Quaternion.Inverse(Yaw(facing)));

        // Bones are measured on the drawn model, which already includes any previous bone correction,
        // so the fresh change is added to it — pressing Align again refines instead of stacking.
        var previous = poseTrigger.BoneCorrection;
        poseTrigger.SetBoneCorrection(new PoseOffset
        {
            Position = previous.Position + positionChange,
            Rotation = previous.Rotation + turn,
        });

        Status = $"Aligned {BodyParts.DisplayName(selfPart)} -> {BodyParts.DisplayName(partnerPart)} " +
                 $"({bestDistance:0.00}y, {configuration.BoneAlignGap:0.00}y gap{facingNote}).";
    }

    /// How far to turn this player so the two chosen parts face each other — this player's part's
    /// horizontal direction onto the reverse of the partner's — plus a short note for the status line.
    /// Zero (with the reason) whenever the turn can't be trusted or applied.
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

    private static bool IsMostlyHorizontal(Vector3 direction)
    {
        var length = direction.Length();
        if (length < 1e-4f) return false;
        return new Vector2(direction.X, direction.Z).Length() / length >= MinHorizontalFraction;
    }

    /// The game's facing convention: a character with rotation r faces (sin r, 0, cos r).
    private static float Heading(Vector3 direction) => MathF.Atan2(direction.X, direction.Z);

    private static Quaternion Yaw(float angle) => Quaternion.CreateFromYawPitchRoll(angle, 0, 0);

    // Below this the two parts are effectively touching, so there's no approach line to back off along.
    private const float TouchingDistance = 0.005f;

    /// Where this player's part should end up: the configured gap short of the partner's part, so the
    /// parts meet with a little room rather than fusing, which reads badly once the animation moves.
    /// The gap is taken along the line the parts approach on (this player's part, after any facing
    /// turn, → the partner's). If they're touching there's no such line, so it backs off horizontally,
    /// straight away from the partner.
    private Vector3 StopShort(Vector3 theirs, Vector3 mine, IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        var room = configuration.BoneAlignGap;
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
        Status = reason;
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
        Plugin.ChatGui.Print("[PoseKit] Your partner is aligning instead.");
    }

    /// The shared tie-break: whichever side's own "Name@World" sorts first (ordinal) keeps aligning.
    /// Both sides compare the same two identities, so they always agree.
    private bool ThisSideGoesFirst()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || pairingState.Peer is not { } peer) return true;
        var own = new PartnerIdentity(localPlayer.Name.TextValue, localPlayer.HomeWorld.Value.Name.ExtractText());
        return string.CompareOrdinal(own.TellAddress, peer.TellAddress) < 0;
    }
}
