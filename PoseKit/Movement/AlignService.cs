using System;
using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace PoseKit.Movement;

/// <summary>
/// Walks the local player to their current target's exact position and facing, for lining up
/// duo/group poses/emotes — ported from Encore (IcarusXIV/Encore, Plugin.cs's
/// AlignToTarget/GetAlignState + Services/MovementService.cs's RMIWalk detour). Movement is a
/// synthetic override of the native walk-input function (not a teleport), so the character
/// visibly walks into place and any animation-sync plugin mirroring real movement sees the same
/// thing everyone else does. Falls back to a direct position/rotation write if the RMIWalk
/// signature fails to resolve, matching every other native signature in this codebase (see
/// FreeCamService/OffsetEngine) — resolution failure degrades the feature rather than crashing.
///
/// Arrival/cancellation callbacks (facing rotation, and — for auto-align — the actual pose trigger)
/// are deliberately never invoked directly from inside RMIWalkDetour: that runs reentrantly, deep in
/// the native per-frame movement call stack, and both ChatCommand.Execute (UIModule's chat pipeline)
/// and GameObject.SetRotation proved unreliable when called from there — observed as the pose
/// sometimes not playing, and facing sometimes not sticking, both requiring a second align to "fix"
/// (see openspec/changes/align-to-target's design.md, "Post-implementation finding"). Instead the
/// hook only records what happened; Tick() (called from Plugin's Framework.Update, the same safe
/// context every other pose/rotation write in this codebase already uses) applies it.
/// </summary>
public sealed unsafe class AlignService : IDisposable
{
    public const float MaxAlignDistance = 2f;

    private delegate void RMIWalkDelegate(
        void* self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe,
        byte* a6, byte bAdditiveUnk);

    private readonly Hook<RMIWalkDelegate>? rmiWalkHook;

    private volatile bool isWalking;
    private Vector3 destination;
    private float faceRotation;
    private Action? onArrived;
    private Action? onCancelled;
    private long walkStartTick;

    /// Set by Arrive()/Cancel() (called from inside the hook) and run from the next Tick() instead —
    /// see class doc.
    private Action? pendingCompletion;

    /// Re-applied every tick after arrival, in addition to the single application already inside
    /// pendingCompletion, until the character's actual rotation reads back as matching — cheap
    /// insurance against the native engine's own facing/turn interpolation still catching up for a
    /// variable number of frames right after a real (non-fallback) walk ends, which would otherwise
    /// silently overwrite a one-shot SetRotation call. Self-terminates as soon as it sticks (usually
    /// within a tick or two), bounded by MaxRotationHoldTicks so it can't spin forever, and stops
    /// early if the player's position drifts away from where the hold started — a sign of real
    /// movement it shouldn't fight.
    private Vector3 holdPosition;
    private float holdRotation;
    private int rotationHoldTicksLeft;

    private const float SnapDistance = 0.05f;
    private const float SlowdownRadius = 0.3f;
    private const long TimeoutMs = 2000;
    private const int MaxRotationHoldTicks = 60;
    private const float RotationEpsilon = 0.02f;
    private const float HoldPositionDriftTolerance = 0.1f;

    public bool IsMovingToDestination => isWalking;
    public bool IsHookActive => rmiWalkHook != null;

    /// Shared between AlignToTarget's chat error and the main-window status line, so the two never
    /// drift out of sync with each other.
    public static string BlockedReason(CharacterModes mode) => mode switch
    {
        CharacterModes.Mounted => "Dismount first.",
        CharacterModes.EmoteLoop => "Stop your emote first.",
        CharacterModes.InPositionLoop => "Stand up first.",
        CharacterModes.Performance => "Stop performing first.",
        _ => "Stop what you're doing first.",
    };

    public AlignService()
    {
        try
        {
            rmiWalkHook = Plugin.GameInteropProvider.HookFromSignature<RMIWalkDelegate>(
                "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", RMIWalkDetour);
            rmiWalkHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PoseKit] RMIWalk signature did not resolve; align will fall back to an instant position snap.");
        }
    }

    public void Dispose()
    {
        isWalking = false;
        rmiWalkHook?.Disable();
        rmiWalkHook?.Dispose();
    }

    /// Called every framework tick from Plugin — runs any arrival/cancellation completion the hook
    /// recorded on a prior frame, then re-asserts a just-landed rotation until it actually sticks. See
    /// class doc for why this can't just happen inline inside the hook.
    public void Tick()
    {
        if (pendingCompletion is { } cb)
        {
            pendingCompletion = null;
            cb();
        }

        if (rotationHoldTicksLeft <= 0) return;

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null) { rotationHoldTicksLeft = 0; return; }

        var currentPos = new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z);
        if (Vector3.Distance(currentPos, holdPosition) > HoldPositionDriftTolerance)
        {
            // The player moved for real since the hold started — don't fight their own movement.
            rotationHoldTicksLeft = 0;
            return;
        }

        if (MathF.Abs(NormalizeAngle(player->GameObject.Rotation - holdRotation)) <= RotationEpsilon)
        {
            rotationHoldTicksLeft = 0; // already matches — stop
            return;
        }

        player->GameObject.SetRotation(holdRotation);
        rotationHoldTicksLeft--;
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 2f * MathF.PI;
        if (angle > MathF.PI) angle -= 2f * MathF.PI;
        else if (angle < -MathF.PI) angle += 2f * MathF.PI;
        return angle;
    }

    public unsafe (bool hasTarget, string targetName, float distance, bool inRange, CharacterModes mode, bool isWalking) GetAlignState()
    {
        var target = Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget;
        if (target == null)
            return (false, "", 0f, false, CharacterModes.Normal, isWalking);

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null)
            return (false, "", 0f, false, CharacterModes.Normal, isWalking);

        var playerMode = player->Mode;
        var playerPos = new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z);
        var distance = Vector3.Distance(playerPos, target.Position);

        return (true, target.Name.TextValue, distance, distance <= MaxAlignDistance, playerMode, isWalking);
    }

    /// Manual align, triggered via `/posekit align` or the main-window button — reports every guard
    /// failure to chat, matching PoseKit's existing OnCommand error-reporting convention.
    public void AlignToTarget()
    {
        if (isWalking)
        {
            Plugin.ChatGui.Print("[PoseKit] Already walking to target.");
            return;
        }

        var target = Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget;
        if (target == null)
        {
            Plugin.ChatGui.PrintError("[PoseKit] Select a target first.");
            return;
        }

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null) return;

        if (player->Mode != CharacterModes.Normal)
        {
            Plugin.ChatGui.PrintError($"[PoseKit] {BlockedReason(player->Mode)}");
            return;
        }

        var playerPos = new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z);
        var targetPos = target.Position;
        var distance = Vector3.Distance(playerPos, targetPos);

        if (distance > MaxAlignDistance)
        {
            Plugin.ChatGui.PrintError($"[PoseKit] Move closer to {target.Name.TextValue} and try again.");
            return;
        }

        var targetRotation = target.Rotation;

        if (IsHookActive)
            WalkTo(targetPos, targetRotation, arrived: null, cancelled: null);
        else
        {
            SnapToPosition(targetPos);
            BeginRotationHold(targetPos, targetRotation);
        }
    }

    /// Silent variant used by PoseTrigger before playing a pose/emote when the auto-align setting is
    /// on. Every guard failure (already walking, no target, blocked state, out of range) — and a
    /// walk cancelled by real player movement — calls <paramref name="onDone"/> immediately with no
    /// chat message, so playing a pose is never blocked by alignment. A successful walk (or its own
    /// timeout, which still snaps to the destination the same way the manual hook path does) calls
    /// <paramref name="onDone"/> only once the character has arrived and faced the target.
    public void TryAutoAlign(Action onDone)
    {
        if (isWalking) { onDone(); return; }

        var target = Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget;
        if (target == null) { onDone(); return; }

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null) { onDone(); return; }

        if (player->Mode != CharacterModes.Normal) { onDone(); return; }

        var playerPos = new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z);
        var targetPos = target.Position;
        var distance = Vector3.Distance(playerPos, targetPos);
        if (distance > MaxAlignDistance) { onDone(); return; }

        var targetRotation = target.Rotation;

        if (IsHookActive)
            WalkTo(targetPos, targetRotation, arrived: onDone, cancelled: onDone);
        else
        {
            SnapToPosition(targetPos);
            BeginRotationHold(targetPos, targetRotation);
            onDone();
        }
    }

    private void BeginRotationHold(Vector3 pos, float rotation)
    {
        holdPosition = pos;
        holdRotation = rotation;
        rotationHoldTicksLeft = MaxRotationHoldTicks;
    }

    private void SnapToPosition(Vector3 pos)
    {
        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player != null)
            player->GameObject.SetPosition(pos.X, pos.Y, pos.Z);
    }

    private void WalkTo(Vector3 dest, float rotation, Action? arrived, Action? cancelled)
    {
        destination = dest;
        faceRotation = rotation;
        onArrived = arrived;
        onCancelled = cancelled;
        walkStartTick = Environment.TickCount64;
        isWalking = true;
        rotationHoldTicksLeft = 0; // a fresh walk supersedes any hold still running from a prior align
    }

    private void Cancel()
    {
        if (!isWalking) return;
        isWalking = false;
        var cb = onCancelled;
        onArrived = null;
        onCancelled = null;
        // No position/rotation snap on a real-movement cancel — the character stays wherever the
        // player moved it.
        pendingCompletion = cb;
    }

    private void Arrive()
    {
        isWalking = false;
        var dest = destination;
        var rotation = faceRotation;
        var arrivedCb = onArrived;
        onArrived = null;
        onCancelled = null;
        pendingCompletion = () =>
        {
            SnapToPosition(dest);
            BeginRotationHold(dest, rotation);
            arrivedCb?.Invoke();
        };
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        rmiWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft,
            haveBackwardOrStrafe, a6, bAdditiveUnk);

        if (!isWalking) return;

        if (Environment.TickCount64 - walkStartTick > TimeoutMs)
        {
            Arrive();
            return;
        }

        // user input cancels
        if (*sumLeft != 0 || *sumForward != 0)
        {
            Cancel();
            return;
        }

        // skip additive/continuation passes
        if (bAdditiveUnk != 0) return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            Cancel();
            return;
        }

        var playerPos = player.Position;
        var diff = destination - playerPos;
        var horizDist = MathF.Sqrt(diff.X * diff.X + diff.Z * diff.Z);

        if (horizDist <= SnapDistance)
        {
            Arrive();
            return;
        }

        var dirH = MathF.Atan2(diff.X, diff.Z);

        // sumForward/sumLeft are camera-relative in both standard and legacy mode
        var camera = CameraManager.Instance()->GetActiveCamera();
        float refDir;
        if (camera != null)
            refDir = *(float*)((byte*)camera + 0x140) + MathF.PI;
        else
            refDir = player.Rotation;

        var relAngle = dirH - refDir;
        var forward = MathF.Cos(relAngle);
        var left = MathF.Sin(relAngle);

        if (horizDist < SlowdownRadius)
        {
            var scale = MathF.Max(horizDist / SlowdownRadius, 0.15f);
            forward *= scale;
            left *= scale;
        }

        *sumForward = forward;
        *sumLeft = left;
    }
}
