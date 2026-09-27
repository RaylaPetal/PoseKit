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

    private bool sampled;
    private Vector3 bestGap;
    private float bestDistance;

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

        if (!BoneReader.TryGetBoneWorldPosition(localPlayer, BodyParts.CandidateBones(selfPart), out _))
        {
            Status = $"Couldn't find your {BodyParts.DisplayName(selfPart).ToLowerInvariant()} bone.";
            return;
        }

        if (!BoneReader.TryGetBoneWorldPosition(partner, BodyParts.CandidateBones(partnerPart), out _))
        {
            Status = $"Couldn't find {partner.Name.TextValue}'s {BodyParts.DisplayName(partnerPart).ToLowerInvariant()} bone — " +
                     "their body mod may not be synced to you.";
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

        if (BoneReader.TryGetBoneWorldPosition(localPlayer, BodyParts.CandidateBones(selfPart), out var mine) &&
            BoneReader.TryGetBoneWorldPosition(partner, BodyParts.CandidateBones(partnerPart), out var theirs))
        {
            var gap = theirs - mine;
            var distance = gap.Length();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestGap = gap;
                sampled = true;
            }
        }

        if (elapsed >= SampleMs)
            Finish(localPlayer);
    }

    private void Finish(IPlayerCharacter localPlayer)
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

        // Same world-to-local conversion as the partner anchor: the offset's position is applied in
        // the character's final drawn facing (actual rotation plus the rotation offset, when the
        // rotation hook actually applies one).
        var facing = localPlayer.Rotation + (offsetEngine.RotationHookResolved ? offsetEngine.DesiredOffset.Rotation : 0f);
        var inverseFacing = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(facing, 0, 0));
        var localGap = Vector3.Transform(bestGap, inverseFacing);

        // Bones are measured on the drawn model, which already includes any previous bone correction,
        // so the fresh gap is added to it — pressing Align again refines instead of stacking.
        var previous = poseTrigger.BoneCorrection;
        poseTrigger.SetBoneCorrection(new PoseOffset { Position = previous.Position + localGap, Rotation = previous.Rotation });

        Status = $"Aligned {BodyParts.DisplayName(selfPart)} -> {BodyParts.DisplayName(partnerPart)} ({bestDistance:0.00}y).";
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
