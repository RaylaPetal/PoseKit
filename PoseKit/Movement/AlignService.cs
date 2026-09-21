using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using PoseKit.Pairing;

namespace PoseKit.Movement;

/// <summary>
/// Walks the local player to their current target's exact position and facing, for lining up
/// duo/group poses/emotes — ported from Encore (IcarusXIV/Encore, Plugin.cs's
/// AlignToTarget/GetAlignState/ApplyTargetRotation/SnapToPosition + Services/MovementService.cs's
/// RMIWalk detour). Movement is a synthetic override of the native walk-input function (not a
/// teleport), so the character visibly walks into place and any animation-sync plugin mirroring
/// real movement sees the same thing everyone else does. Falls back to a direct position/rotation
/// write if the RMIWalk signature fails to resolve, matching every other native signature in this
/// codebase (see FreeCamService/OffsetEngine) — resolution failure degrades the feature rather
/// than crashing.
///
/// The position/rotation writes on arrival are Encore's own proven approach, kept exactly as
/// written there: one-shot native writes, called synchronously from Arrive() — still inside
/// RMIWalkDetour's call stack, same as Encore does. Several earlier revisions of this file tried
/// to defer and/or repeatedly re-assert these writes to chase inconsistent facing, and each attempt
/// introduced a different, sometimes worse symptom (see openspec/changes/align-handoff-cancel-guard's
/// design.md for the history) — going back to Encore's original, simpler approach resolved it.
/// The one thing Encore doesn't have to deal with is PoseKit's own addition, auto-align triggering
/// a pose/emote afterward: that callback (ultimately PoseTrigger's ChatCommand.Execute) is deferred
/// to Tick() — since it proved unreliable when invoked reentrantly from inside this native hook, see
/// align-to-target's design.md, "Post-implementation finding" — and held back a further
/// PoseTriggerDelayTicks ticks past that, a short fixed pause (not a loop, not a re-check) giving the
/// rotation write above time to actually land before the pose-enter command reads the character's
/// transform. See align-handoff-cancel-guard's design.md, Decision 3e, for why: a zero-delay handoff
/// was observed triggering the pose with the character still facing its pre-write direction.
/// </summary>
public sealed unsafe class AlignService : IDisposable
{
    public const float MaxAlignDistance = 2f;

    private delegate void RMIWalkDelegate(
        void* self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe,
        byte* a6, byte bAdditiveUnk);

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool CancelEmoteDelegate(EmoteController* emoteController, nint unknown);

    private readonly Hook<RMIWalkDelegate>? rmiWalkHook;
    private readonly Hook<CancelEmoteDelegate>? cancelEmoteHook;
    private readonly PairingState pairingState;
    private readonly OffsetEngine offsetEngine;

    private volatile bool isWalking;
    private Vector3 destination;
    private float faceRotation;
    private Action? onArrived;
    private Action? onCancelled;
    private long walkStartTick;

    /// Set by Arrive()/Cancel() (called from inside the hook) and run from the next Tick() instead —
    /// see class doc. Only ever holds the pose-trigger callback (never the position/rotation writes,
    /// which happen synchronously in Arrive() itself, matching Encore).
    private Action? pendingCompletion;

    /// Extra ticks Tick() waits, past the next tick, before invoking a pending auto-align pose-trigger
    /// callback — gives the native engine a moment to actually process the rotation write in Arrive()
    /// before the pose-enter command reads the character's transform (a zero-delay handoff was
    /// observed to trigger the pose using the pre-write facing — see design.md Decision 3e). Always 0
    /// for Cancel()'s deferred callback, which has no preceding write to wait on.
    private int pendingCompletionDelayTicks;

    /// True only while an auto-align-triggered pose could still be cancelled by this alignment's own
    /// position/rotation writes landing at effectively the same moment as the pose-enter command —
    /// see CancelEmoteDetour. Armed right before handing off to the pose-trigger callback, cleared by
    /// PoseTrigger.PoseCycleSettled (see OnPoseCycleSettled) or, failing that, the safety-net timeout
    /// below. Never armed for manual AlignToTarget — there's no pose to protect there.
    private bool cancelGuardActive;
    private long cancelGuardExpiresAt;

    private const float SnapDistance = 0.05f;
    private const float SlowdownRadius = 0.3f;
    private const long TimeoutMs = 2000;

    /// Safety net only, in case PoseTrigger.PoseCycleSettled never fires (e.g. an unresolvable emote
    /// command) — comfortably covers PoseTrigger.EnterPoseCycle's worst case (500ms initial settle +
    /// 8x100ms /cpose cycling attempts ~= 1.3s) with margin.
    private const long CancelGuardSafetyNetMs = 1500;

    /// Widened from an earlier 8 (~130ms) — see design.md Decision 3i. This change's own cancel-guard
    /// exists because the game's native code treats a character whose position/rotation was just
    /// written as "recently disturbed" and reacts defensively (there, by cancelling an already-active
    /// pose); live testing (pose entry consistently failing — EmoteModeId never leaving Normal, not
    /// just intermittently) suggests the same disturbance signal may also block *entering* a new pose,
    /// not just cancel an existing one, and 130ms is far short of the up-to-1.5s CancelGuardSafetyNetMs
    /// already needed for that same signal on the cancellation side. Matching that same window here.
    private const int PoseTriggerDelayTicks = 90;

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

    public AlignService(PairingState pairingState, OffsetEngine offsetEngine)
    {
        this.pairingState = pairingState;
        this.offsetEngine = offsetEngine;

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

        try
        {
            // Same native function and signature FreeCamInput already resolves independently for
            // freecam's own doze-preservation fix (see openspec/changes/preserve-emote-during-freecam)
            // — resolved again here rather than shared, since freecam may not be active during an
            // ordinary align and this hook's lifecycle (a brief post-arrival window) is unrelated to
            // freecam's own enable/disable session.
            var cancelEmoteAddress = Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 7B 08 45 33 C0");
            cancelEmoteHook = Plugin.GameInteropProvider.HookFromAddress<CancelEmoteDelegate>(cancelEmoteAddress, CancelEmoteDetour);
            cancelEmoteHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PoseKit] cancelEmote signature did not resolve; auto-align's pose handoff will not be protected from cancellation.");
        }
    }

    public void Dispose()
    {
        isWalking = false;
        rmiWalkHook?.Disable();
        rmiWalkHook?.Dispose();
        cancelEmoteHook?.Disable();
        cancelEmoteHook?.Dispose();
    }

    /// Returns "don't cancel" only while an auto-align pose could still be cancelled by this
    /// alignment's own writes (see cancelGuardActive); otherwise defers to whatever the native check
    /// (or FreeCamInput's own independent hook on this same function, if freecam also happens to be
    /// active) would decide.
    private bool CancelEmoteDetour(EmoteController* emoteController, nint unknown)
    {
        if (cancelGuardActive) return false;
        return cancelEmoteHook!.Original(emoteController, unknown);
    }

    /// Called every framework tick from Plugin — runs the pose-trigger callback the hook recorded on
    /// a prior frame (see class doc for why it can't just happen inline inside the hook), after
    /// pendingCompletionDelayTicks more ticks have passed if any were requested, and expires the
    /// cancel-guard's safety-net timeout if PoseCycleSettled never fired.
    public void Tick()
    {
        if (pendingCompletion is { } cb)
        {
            if (pendingCompletionDelayTicks > 0)
            {
                // Keep insisting on the same real, absolute facing every tick during this wait —
                // not an offset, the same ApplyRotationOnce/ForceDrawRotation write Arrive() already
                // does once, just repeated. A single write can lose to the tail end of the walk/run
                // animation still playing out for a tick or two after RMIWalk reports arrival (the
                // character hasn't necessarily finished decelerating into idle) — live testing showed
                // the turn briefly land and then visibly revert while still "running". Reapplying the
                // same value for the length of the wait we already have before triggering the pose
                // outlasts that tail end, the same way OffsetEngine's own position reapplication
                // already insists on the same desired value every tick rather than writing it once.
                // Stops the instant the pose actually triggers — writing rotation any closer to pose
                // entry than that has repeatedly proven risky (see design.md's revision history).
                ApplyRotationOnce(faceRotation);
                offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, faceRotation);
                pendingCompletionDelayTicks--;
            }
            else
            {
                pendingCompletion = null;
                cb();
            }
        }

        if (cancelGuardActive && Environment.TickCount64 >= cancelGuardExpiresAt)
            cancelGuardActive = false;
    }

    /// Subscribed to PoseTrigger.PoseCycleSettled (wired in Plugin.cs). Fires for every
    /// Trigger/TriggerCommand call, not just ones this guard is protecting — harmless no-op if the
    /// guard isn't currently active.
    public void OnPoseCycleSettled() => cancelGuardActive = false;

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

        RefreshTargeting(target);

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
            ApplyRotationOnce(targetRotation);
            offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, targetRotation);
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
        // Busy covers a still-pending completion, not just isWalking — see design.md Decision 3m. A
        // prior attempt's walk can finish (isWalking false) while it's still counting down
        // pendingCompletionDelayTicks (waiting to trigger its own pose, and reapplying facing every
        // tick while it does). WalkTo() never resets that state, so a second attempt starting during
        // that window would silently overwrite the first attempt's in-flight pendingCompletion/
        // faceRotation before it ever fires — the first attempt's pose trigger callback would be lost
        // entirely, and whatever facing the second attempt captures would stomp the first's. Treating
        // a pending completion as busy, the same as an in-progress walk, closes that race.
        if (isWalking || pendingCompletion != null) { onDone(); return; }

        var target = Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget;
        if (target == null) { onDone(); return; }

        RefreshTargeting(target);

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null) { onDone(); return; }

        if (player->Mode != CharacterModes.Normal) { onDone(); return; }

        if (ShouldYieldToPartner(target)) { onDone(); return; }

        var playerPos = new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z);
        var targetPos = target.Position;
        var distance = Vector3.Distance(playerPos, targetPos);
        if (distance > MaxAlignDistance) { onDone(); return; }

        var targetRotation = target.Rotation;

        if (IsHookActive)
            WalkTo(targetPos, targetRotation, arrived: onDone, cancelled: onDone);
        else
        {
            // Not inside the native hook here, so no reentrancy concern, but the same rotation-write-
            // needs-a-moment-to-land reasoning as Arrive() still applies (see design.md Decision 3e) —
            // defer onDone through the same delayed-pendingCompletion path rather than calling it
            // synchronously right after the write.
            SnapToPosition(targetPos);
            ApplyRotationOnce(targetRotation);
            offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, targetRotation);
            // Tick()'s reapplication loop below reads the faceRotation field, not a local variable —
            // set it here too so the no-hook path keeps insisting on the correct value during the wait.
            faceRotation = targetRotation;
            ArmCancelGuard();
            pendingCompletion = onDone;
            pendingCompletionDelayTicks = PoseTriggerDelayTicks;
        }
    }

    /// True when the align target is the active pairing partner and this side should not be the one
    /// to walk. Both clients independently compare the same two fixed identities (their own vs the
    /// partner's), so they always agree on which side yields — with no extra message exchanged —
    /// preventing both sides of a couple-play from simultaneously walking toward each other's
    /// pre-walk position when both have auto-align on. Deliberately never consulted from
    /// AlignToTarget (manual align) — see openspec/changes/auto-align-partner-tiebreak's proposal.md.
    private bool ShouldYieldToPartner(IGameObject target)
    {
        if (!pairingState.Active || pairingState.Peer is not { } peer) return false;
        if (target is not IPlayerCharacter targetPlayer) return false;

        var targetWorld = targetPlayer.HomeWorld.Value.Name.ExtractText();
        if (!peer.Matches(targetPlayer.Name.TextValue, targetWorld)) return false;

        if (GetOwnIdentity() is not { } own) return false;

        // This side yields (does not walk) unless its own identity sorts strictly first.
        return string.CompareOrdinal(own.TellAddress, peer.TellAddress) >= 0;
    }

    private static PartnerIdentity? GetOwnIdentity()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return null;
        return new PartnerIdentity(localPlayer.Name.TextValue, localPlayer.HomeWorld.Value.Name.ExtractText());
    }

    /// Clears and immediately re-selects the same target before walking to it. Live testing found
    /// that repeatedly align-and-pose'ing against the *same held* target — never deselecting and
    /// reselecting it between plays — silently breaks the pose entry itself (the game's own
    /// EmoteController never leaves Normal mode, no error, no exception) on every retry after the
    /// first, while manually deselecting and reselecting the identical target (even with no other
    /// change) reliably fixes it. This automates that exact manual workaround rather than requiring
    /// the user to do it themselves before every replay — see align-handoff-cancel-guard's design.md,
    /// "the same-target caching finding", for the elimination of every other theory (position overlap,
    /// duplicate command text, accumulated session state) that ruled this in.
    private static void RefreshTargeting(IGameObject target)
    {
        if (Plugin.TargetManager.Target != null)
        {
            Plugin.TargetManager.Target = null;
            Plugin.TargetManager.Target = target;
        }
        else if (Plugin.TargetManager.SoftTarget != null)
        {
            Plugin.TargetManager.SoftTarget = null;
            Plugin.TargetManager.SoftTarget = target;
        }
    }

    private void ArmCancelGuard()
    {
        cancelGuardActive = true;
        cancelGuardExpiresAt = Environment.TickCount64 + CancelGuardSafetyNetMs;
    }

    private void ApplyRotationOnce(float rotation)
    {
        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player != null)
            player->GameObject.SetRotation(rotation);
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
        // A fresh walk supersedes any guard still running from a prior align.
        cancelGuardActive = false;
    }

    private void Cancel()
    {
        if (!isWalking) return;
        isWalking = false;
        var cb = onCancelled;
        onArrived = null;
        onCancelled = null;
        // No position/rotation snap on a real-movement cancel — the character stays wherever the
        // player moved it, and plays with no facing correction either (per align-to-target's spec).
        // Deferred to Tick() the same as the arrival callback below, for the same reentrancy reason —
        // this ultimately triggers a pose too (TryAutoAlign passes the same callback for both arrived
        // and cancelled). No write precedes this, so no extra delay is needed before firing it — fires
        // on the very next tick.
        pendingCompletion = cb;
        pendingCompletionDelayTicks = 0;
    }

    private void Arrive()
    {
        isWalking = false;
        var arrivedCb = onArrived;
        onArrived = null;
        onCancelled = null;

        // Position + rotation: Encore's own proven approach (IcarusXIV/Encore, MovementService.cs's
        // Arrive() + Plugin.cs's ApplyTargetRotation/SnapToPosition) — plain one-shot native writes,
        // called synchronously here, still inside RMIWalkDetour's call stack, exactly like Encore.
        // An earlier revision additionally routed a facing correction through PoseTrigger's additive
        // draw-rotation offset (the same mechanism saved presets use for their own anchor corrections)
        // to make facing survive a pose's own per-frame draw recompute; removed per explicit direction
        // — it stacked on top of a preset's own saved rotation offset and visibly distorted it (see
        // Decision 3h). ApplyRotationOnce alone then proved insufficient on its own too — live testing
        // confirmed the authoritative write lands and reads back correctly, but the character still
        // didn't visually turn, since the game's draw-time rotation is separate state it doesn't
        // necessarily re-derive from GameObject.Rotation every frame. ForceDrawRotation below is not
        // an offset — it's a direct, one-shot, absolute write of the same real value, using the same
        // native function OffsetEngine already hooks, with nothing added and no preset offset touched.
        // See Decision 3k.
        SnapToPosition(destination);
        ApplyRotationOnce(faceRotation);
        offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, faceRotation);

        if (arrivedCb == null) return; // manual align — nothing further to do

        // Auto-align only: about to hand off into playing a pose. Arm the cancel-guard now, before
        // that callback runs, so the pose it triggers can't be cancelled by the writes just above
        // landing at effectively the same moment. The callback itself (ultimately
        // PoseTrigger.TriggerNow -> ChatCommand.Execute) is the one thing that proved unreliable when
        // invoked reentrantly from inside this native hook — deferred to Tick(), and held back an
        // extra PoseTriggerDelayTicks ticks past that so the position/rotation writes above have time
        // to actually land before the pose-enter command reads the character's transform.
        ArmCancelGuard();
        pendingCompletion = arrivedCb;
        pendingCompletionDelayTicks = PoseTriggerDelayTicks;
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
