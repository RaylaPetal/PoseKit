using System;
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
///
/// Never picks a facing itself: a manual Align keeps the current one, and a memory replay turns to
/// the remembered relative facing before sampling.
/// </summary>
public sealed class BoneAlignService : IDisposable
{
    private enum Phase { Idle, Settling, Sampling }

    // Long enough for the emote-loop reset (and a replayed turn) to take effect before measuring.
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
    private AlignRequest request = new(BodyPart.Penis, BodyPart.Vagina, 0f, null, null, AlignOrigin.Manual);
    private BodyPart selfPart => request.Self;
    private BodyPart partnerPart => request.Partner;

    // A replayed turn is applied at start; this restores the correction if the align then fails.
    private PoseOffset correctionAtStart;
    private bool turnedAtStart;
    private string facingNote = "";

    // The closest sampled moment.
    private bool sampled;
    private float bestDistance;
    private Vector3 bestMine;
    private Vector3 bestTheirs;
    private float bestMyYaw;
    private float bestTheirYaw;

    public string Status { get; private set; } = "";

    public bool IsAligning => phase != Phase.Idle;

    /// A manual or memory Align that moved this player, or a Measure, completed.
    public event Action<AlignRequest, AlignResult>? Succeeded;

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

    private bool Measuring => request.Mode == AlignMode.Measure;

    public void Start(AlignRequest alignRequest)
    {
        if (IsAligning) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;
        request = alignRequest;
        turnedAtStart = false;

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

        // A Measure moves nothing, so it never conflicts with the partner's Align.
        if (pairingState.Active && !Measuring)
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
        correctionAtStart = poseTrigger.BoneCorrection;
        facingNote = Measuring ? "" : TurnToRelativeYaw(localPlayer, partner);

        emoteSync.Sync();
        poseTrigger.PartnerTrackingPaused = true;
        phase = Phase.Settling;
        phaseStartedAt = Environment.TickCount64;
        SetStatus($"{(Measuring ? "Measuring" : "Aligning")} {BodyParts.DisplayName(selfPart)} -> {BodyParts.DisplayName(partnerPart)}...");
    }

    /// Turns this player so their drawn heading is the partner's plus the request's relative yaw,
    /// pivoting around the actual position. Returns the status note.
    private string TurnToRelativeYaw(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        if (request.RelativeYaw is not { } relativeYaw)
            return request.Origin == AlignOrigin.Memory ? ", facing not saved" : "";
        if (!offsetEngine.RotationHookResolved)
            return ", facing unchanged: rotation offset unavailable";

        var turn = Wrap(DrawnYaw(partner) + relativeYaw - DrawnYaw(localPlayer));
        AddTurn(localPlayer, localPlayer.Position, turn);
        turnedAtStart = true;
        return MathF.Abs(turn) < OneDegree ? "" : $", turned {turn * 180f / MathF.PI:0} deg";
    }

    /// Turns this player by <paramref name="degrees"/> around their Self part, so a contact already
    /// made stays roughly in place. Falls back to pivoting on the actual position.
    public void QuickTurn(float degrees)
    {
        if (IsAligning || !offsetEngine.RotationHookResolved) return;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || PoseIdentifier.FromCharacter(localPlayer) == null) return;

        var pivot = BodyParts.TryLocate(localPlayer, configuration.BoneAlignSelf, out var part) ? part : localPlayer.Position;
        AddTurn(localPlayer, pivot, degrees * MathF.PI / 180f);
    }

    /// Adds a turn to the bone correction, with the position change that keeps <paramref name="pivot"/>
    /// where it is. See Finish for the frame the offset's position lives in.
    private void AddTurn(IPlayerCharacter localPlayer, Vector3 pivot, float turn)
    {
        var facing = localPlayer.Rotation + offsetEngine.DesiredOffset.Rotation;
        var arm = pivot - localPlayer.Position;
        var positionChange = Vector3.Transform(arm, Quaternion.Inverse(Yaw(facing + turn)))
                             - Vector3.Transform(arm, Quaternion.Inverse(Yaw(facing)));

        var previous = poseTrigger.BoneCorrection;
        poseTrigger.SetBoneCorrection(new PoseOffset
        {
            Position = previous.Position + positionChange,
            Rotation = previous.Rotation + turn,
        });
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
                bestMyYaw = DrawnYaw(localPlayer);
                bestTheirYaw = DrawnYaw(partner);
                sampled = true;
            }
        }

        if (elapsed >= SampleMs)
            Finish(localPlayer, partner);
    }

    private const float OneDegree = MathF.PI / 180f;

    private void Finish(IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        phase = Phase.Idle;
        poseTrigger.PartnerTrackingPaused = false;

        if (!sampled)
        {
            Fail("Couldn't read the bones while aligning.");
            return;
        }

        if (bestDistance > MaxAlignDistance)
        {
            Fail($"Too far apart to {(Measuring ? "remember" : "align")} ({bestDistance:0.00}y, max {MaxAlignDistance:0.0}y) — move closer first.");
            return;
        }

        var relativeYaw = Wrap(bestMyYaw - bestTheirYaw);
        var intoPartnerFrame = Quaternion.Inverse(Yaw(bestTheirYaw));

        // From here on the align succeeds, so the start turn stays.
        turnedAtStart = false;

        if (Measuring)
        {
            var measured = Vector3.Transform(bestMine - bestTheirs, intoPartnerFrame);
            SetStatus($"Remembered {BodyParts.DisplayName(selfPart)} -> {BodyParts.DisplayName(partnerPart)} " +
                      $"({measured.Length():0.00}y apart, {FacingText(relativeYaw)}).");
            Succeeded?.Invoke(request, new AlignResult(relativeYaw, measured));
            return;
        }

        var target = request.ContactOffset is { } contact
            ? bestTheirs + Vector3.Transform(contact, Yaw(bestTheirYaw))
            : StopShort(bestTheirs, bestMine, localPlayer, partner);

        // The drawn model sits at the actual position, facing f (actual rotation plus the rotation
        // offset, when the rotation hook applies one), with the offset's position in that local frame.
        // So moving the part from B to T takes R(f)^-1 (T - B) in that frame.
        var facing = localPlayer.Rotation + (offsetEngine.RotationHookResolved ? offsetEngine.DesiredOffset.Rotation : 0f);
        var positionChange = Vector3.Transform(target - bestMine, Quaternion.Inverse(Yaw(facing)));

        // Bones were measured with the previous correction applied, so add to it.
        var previous = poseTrigger.BoneCorrection;
        poseTrigger.SetBoneCorrection(new PoseOffset
        {
            Position = previous.Position + positionChange,
            Rotation = previous.Rotation,
        });

        var room = request.ContactOffset is { } c ? $"{c.Length():0.00}y contact" : $"{request.Gap:0.00}y gap";
        SetStatus($"Aligned {BodyParts.DisplayName(selfPart)} -> {BodyParts.DisplayName(partnerPart)} " +
                  $"({bestDistance:0.00}y, {room}{facingNote}).");
        Succeeded?.Invoke(request, new AlignResult(relativeYaw, Vector3.Transform(target - bestTheirs, intoPartnerFrame)));
    }

    /// "N deg from partner's heading", for status lines and the memory panel.
    public static string FacingText(float relativeYaw)
    {
        var degrees = MathF.Round(relativeYaw * 180f / MathF.PI);
        if (degrees <= -180f) degrees += 360f;
        return $"{degrees:0} deg from partner's heading";
    }

    /// The drawn model's heading, which includes render offsets; the actual rotation if unreadable.
    private static float DrawnYaw(IPlayerCharacter character) =>
        BoneReader.TryGetModelTransform(character, out _, out var yaw) ? yaw : character.Rotation;

    private static float Wrap(float angle) => MathF.IEEERemainder(angle, MathF.Tau);

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

    /// Ends without changing anything: a turn made at start is undone.
    private void Fail(string reason)
    {
        RevertTurn();
        SetStatus(reason);
    }

    private void Cancel(string reason)
    {
        phase = Phase.Idle;
        poseTrigger.PartnerTrackingPaused = false;
        Fail(reason);
    }

    /// Only while the offset is the one the turn was added to; a reset or new play already cleared it.
    private void RevertTurn()
    {
        if (!turnedAtStart) return;
        turnedAtStart = false;
        if (poseTrigger.OffsetGeneration == offsetGenerationAtStart)
            poseTrigger.SetBoneCorrection(correctionAtStart);
    }

    private void OnPartnerAlignStarted(PartnerIdentity sender)
    {
        partnerAlignUntil = Environment.TickCount64 + PartnerAlignWindowMs;
        if (IsAligning && !Measuring && !ThisSideGoesFirst())
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
