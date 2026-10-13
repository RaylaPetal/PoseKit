using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Bones;
using PoseKit.Presets;
using PoseKit.Sync;

namespace PoseKit.Library;

/// <summary>
/// Puts a groundsit or emote played from the seated or bed library back on that seat or bed: once the
/// new animation is running, the render position and facing are moved to the reference, then (in Hips
/// mode) the animation's hips are snapped onto the reference's recorded hip point.
///
/// Both corrections go into the play's own offset, so partner Bone Align stays a separate layer on
/// top. If the game won't start the animation from the seat or bed, the character gets up and the
/// play is retried once. A preset played while seated gets the same retry without the placement
/// (<see cref="BeginRetryOnly"/>), since its own anchor already puts it there.
/// </summary>
public sealed class SeatPlacement(Plugin plugin)
{
    private enum Phase { Idle, WaitingForPose, StandingUp, Settling, Sampling, Placed }

    // How long the character may stay in the chair sit or doze before the play counts as refused.
    private const long RefusedAfterMs = 1500;
    private const long WaitForPoseMs = 6000;
    // The new pose must hold this long, so a pose cycle's intermediate steps aren't placed.
    private const long PoseStableMs = 200;
    private const long SnapSettleMs = 300;
    private const long SnapSampleMs = 1200;

    private Phase phase = Phase.Idle;
    private long phaseStartedAt;
    private SeatReference? reference;
    private string memoryKey = "";
    private Action? replay;
    private bool retried;
    // False for a preset's retry-only run: once its pose starts there's nothing to place.
    private bool placeOnStart;

    // The pose running when the play was clicked and the one the play should start (null if unknown),
    // so a previous play still running isn't mistaken for this one.
    private PoseIdentifier? poseAtBegin;
    private PoseIdentifier? expectedPose;

    private PoseIdentifier? candidatePose;
    private long candidateSince;
    private PoseIdentifier placedPose;
    private int placedGeneration;

    // The play's context, kept so a stand-up (which clears them) doesn't lose them.
    private PenumbraLink? penumbraContext;
    private (string ModDirectory, string ModName, string Group, string Option, string Trigger, PoseIdentifier? Pose) playContext;

    // The placement base last applied, so a re-apply keeps the player's live-offset edits on top.
    private PoseOffset? appliedBase;

    private Vector3 hipSum;
    private int hipSamples;
    private PoseOffset? snapCorrection;

    public SnapMode Mode { get; private set; }

    /// The move from where the game put the character to the seat; zero when nothing was placed.
    public PoseOffset PlacementOffset { get; private set; } = PoseOffset.Zero;

    public string Status { get; private set; } = "";

    /// From the play until the placed animation ends.
    public bool IsActive => phase != Phase.Idle;

    /// Placed and running, so the snap mode can be switched.
    public bool IsPlaced => phase is Phase.Placed or Phase.Settling or Phase.Sampling;

    /// Still moving the character; an auto-align waits for this, since it would measure a position
    /// that's about to change.
    public bool IsMoving => phase is Phase.WaitingForPose or Phase.StandingUp or Phase.Settling or Phase.Sampling;

    /// Call right before the play runs. <paramref name="replayPlay"/> re-runs it directly (not
    /// through the pairing queue) if the game refused it from the seat or bed.
    public void Begin(SeatReference seat, string key, SnapMode mode, Action replayPlay,
        (string ModDirectory, string ModName, string Group, string Option, string Trigger, PoseIdentifier? Pose) context)
    {
        Start(seat, replayPlay, context.Pose, place: true);
        memoryKey = key;
        Mode = mode;
        playContext = context;
        Status = $"Placing on {seat.Label}...";
    }

    /// For a preset played while seated or lying down: if the game refuses it there, gets up and plays
    /// it again. Nothing is placed. Call right before the play runs.
    public void BeginRetryOnly(SeatReference seat, PoseIdentifier pose, Action replayPlay)
    {
        Start(seat, replayPlay, pose, place: false);
        memoryKey = "";
        playContext = default;
        Status = "";
    }

    private void Start(SeatReference seat, Action replayPlay, PoseIdentifier? expected, bool place)
    {
        reference = seat;
        replay = replayPlay;
        placeOnStart = place;
        expectedPose = expected;
        poseAtBegin = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        penumbraContext = null;
        retried = false;
        candidatePose = null;
        snapCorrection = null;
        appliedBase = null;
        PlacementOffset = PoseOffset.Zero;
        Enter(Phase.WaitingForPose);
    }

    /// Switches the snap mode for the running play, re-applying placement, and remembers the choice.
    public void SetMode(SnapMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        if (memoryKey.Length > 0)
            plugin.SeatSnapMemory.Record(memoryKey, mode);
        if (!IsPlaced) return;

        if (mode == SnapMode.PositionOnly)
        {
            ApplyBase(PlacementOffset);
            Enter(Phase.Placed);
            Status = $"On {reference?.Label}, {reference?.Surface} position only.";
        }
        else if (snapCorrection is { } snap)
        {
            ApplyBase(PoseTrigger.ComposeOffset(PlacementOffset, snap));
            Status = $"On {reference?.Label}, hips snapped to the {reference?.Surface}.";
        }
        else
        {
            StartSnap();
        }
    }

    public void Tick(IPlayerCharacter? localPlayer, PoseIdentifier? currentPose)
    {
        if (phase == Phase.Idle) return;
        if (localPlayer == null)
        {
            Stop("");
            return;
        }

        // Keep the newest context the play set; a stand-up clears the plugin's copy.
        if (plugin.LastPlayedPenumbraContext is { } link)
            penumbraContext = link;

        var elapsed = Environment.TickCount64 - phaseStartedAt;
        switch (phase)
        {
            case Phase.WaitingForPose:
                TickWaiting(localPlayer, currentPose, elapsed);
                break;

            case Phase.StandingUp:
                if (currentPose?.EmoteModeId != reference?.NativeMode)
                {
                    retried = true;
                    replay?.Invoke();
                    Enter(Phase.WaitingForPose);
                }
                else if (elapsed > RefusedAfterMs)
                    Stop($"Couldn't get up from the {reference?.Surface} to play this.");
                break;

            case Phase.Settling:
            case Phase.Sampling:
            case Phase.Placed:
                if (currentPose != placedPose || plugin.PoseTrigger.OffsetGeneration != placedGeneration)
                {
                    // The placed animation ended or another play replaced it.
                    Stop("");
                    return;
                }
                if (phase == Phase.Settling && elapsed >= SnapSettleMs)
                {
                    hipSum = Vector3.Zero;
                    hipSamples = 0;
                    Enter(Phase.Sampling);
                }
                else if (phase == Phase.Sampling)
                    TickSampling(localPlayer, elapsed);
                break;
        }
    }

    private void TickWaiting(IPlayerCharacter localPlayer, PoseIdentifier? currentPose, long elapsed)
    {
        var trigger = plugin.PoseTrigger;
        var nativeMode = reference?.NativeMode;
        if (currentPose is { } pose && pose.EmoteModeId != nativeMode && !trigger.IsCycling && IsThisPlaysPose(pose, elapsed))
        {
            if (candidatePose != pose)
            {
                candidatePose = pose;
                candidateSince = Environment.TickCount64;
                return;
            }
            if (Environment.TickCount64 - candidateSince < PoseStableMs) return;
            if (placeOnStart)
                Place(localPlayer, pose);
            else
                Stop("");
            return;
        }

        candidatePose = null;
        if (currentPose?.EmoteModeId == nativeMode && !trigger.IsCycling && elapsed > RefusedAfterMs && !retried)
        {
            // The game wouldn't start it from the seat or bed: get up, then play again.
            ChatCommand.Execute(reference?.Kind == ReferenceKind.Bed ? "/doze" : "/sit");
            Enter(Phase.StandingUp);
            return;
        }

        if (elapsed > WaitForPoseMs)
            Stop($"This animation didn't start a looping pose, so it couldn't be placed on the {reference?.Surface}.");
    }

    /// The play's own pose when it's known. Otherwise any new pose, or after a while the one that was
    /// already running (the same animation played again).
    private bool IsThisPlaysPose(PoseIdentifier pose, long elapsed)
    {
        if (expectedPose is { } expected) return pose == expected;
        return pose != poseAtBegin || elapsed > RefusedAfterMs;
    }

    private void Place(IPlayerCharacter localPlayer, PoseIdentifier pose)
    {
        if (reference is not { } seat) { Stop(""); return; }

        // Restore what a stand-up cleared, so saving and alignment memory still see this play.
        plugin.LastPlayedPenumbraContext ??= penumbraContext;
        if (plugin.CurrentPlayContext == null)
            plugin.SetPlayContext(playContext.ModDirectory, playContext.ModName, playContext.Group, playContext.Option,
                playContext.Trigger, pose);

        var anchor = new LocationAnchor { TerritoryType = seat.TerritoryType, Position = seat.Position, Rotation = seat.Rotation };
        var correction = anchor.TryComputeCorrection(localPlayer, Plugin.ClientState.TerritoryType, 0f,
            plugin.OffsetEngine.RotationHookResolved);
        if (correction is not { } c)
        {
            Stop($"Too far from {seat.Label} to place this on it.");
            return;
        }

        placedPose = pose;
        PlacementOffset = c;
        // Whatever offset the previous play left is replaced, not kept as an edit.
        ApplyBase(c, keepEdits: false);
        placedGeneration = plugin.PoseTrigger.OffsetGeneration;

        if (Mode == SnapMode.Hips)
            StartSnap();
        else
        {
            Enter(Phase.Placed);
            Status = $"On {seat.Label}, {seat.Surface} position only.";
        }
    }

    private void StartSnap()
    {
        if (reference?.HipPoint == null)
        {
            Enter(Phase.Placed);
            Status = $"Snap skipped: the {reference?.Surface}'s hip point couldn't be read. Position only.";
            return;
        }
        Status = $"Snapping hips to the {reference.Surface}...";
        Enter(Phase.Settling);
    }

    private void TickSampling(IPlayerCharacter localPlayer, long elapsed)
    {
        if (HipPoint.TryGet(localPlayer, out var hip))
        {
            hipSum += hip;
            hipSamples++;
        }
        if (elapsed < SnapSampleMs) return;

        Enter(Phase.Placed);
        if (hipSamples == 0 || reference?.HipPoint is not { } seatHip)
        {
            Status = "Snap skipped: this animation's hip bones couldn't be read. Position only.";
            return;
        }

        var delta = seatHip - hipSum / hipSamples;
        if (delta.Length() > BoneAlignService.MaxAlignDistance)
        {
            Status = $"Snap skipped: the hips were {delta.Length():0.00}y from the {reference.Surface}, too far apart. Position only.";
            return;
        }

        // Offsets are applied in the character's final facing.
        var current = plugin.OffsetEngine.DesiredOffset;
        var finalFacing = localPlayer.Rotation + (plugin.OffsetEngine.RotationHookResolved ? current.Rotation : 0f);
        var local = Vector3.Transform(delta, Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(finalFacing, 0, 0)));
        var snap = new PoseOffset { Position = local, Rotation = 0f };
        snapCorrection = snap;
        ApplyBase(PoseTrigger.ComposeOffset(PlacementOffset, snap));
        Status = $"On {reference.Label}, hips snapped to the {reference.Surface} ({delta.Length():0.00}y).";
    }

    /// Sets the play's own offset, keeping the player's live-offset edits since the last apply and any
    /// partner Bone Align correction on top. A Bone Align in progress is cancelled, since it measured
    /// the position this moves away from.
    private void ApplyBase(PoseOffset baseOffset, bool keepEdits = true)
    {
        if (plugin.BoneAlign.IsAligning)
            plugin.BoneAlign.Cancel($"Alignment cancelled — the pose was moved onto the {reference?.Surface}.");

        var trigger = plugin.PoseTrigger;
        var edits = keepEdits && appliedBase is { } applied
            ? PoseTrigger.Subtract(trigger.GetOffsetForNewPreset(), applied)
            : PoseOffset.Zero;
        trigger.ApplyOffset(PoseTrigger.ComposeOffset(PoseTrigger.ComposeOffset(baseOffset, edits), trigger.BoneCorrection));
        appliedBase = baseOffset;
        placedGeneration = trigger.OffsetGeneration;
    }

    private void Enter(Phase next)
    {
        phase = next;
        phaseStartedAt = Environment.TickCount64;
    }

    private void Stop(string status)
    {
        phase = Phase.Idle;
        replay = null;
        Status = status;
        if (status.Length == 0) PlacementOffset = PoseOffset.Zero;
        // The library leaves the seated view once placement stops, so a failure goes to chat too.
        else Plugin.ChatGui.PrintError($"[PoseKit] {status}");
    }
}
