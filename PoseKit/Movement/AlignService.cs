using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using PoseKit.Pairing;
using PoseKit.Presets;

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
/// Auto-align is a paired-only feature: the only thing that ever triggers a walk here is either the
/// manual align action (AlignToTarget — chat command or main-window button, any time) or a paired
/// player queueing a selection with the auto-align setting on (TryAutoAlignOnQueue, called from
/// CoupleQueueService.QueueSelection, the instant they queue — decoupled entirely from when the
/// queued pick actually plays). Neither one ever hands off into playing a pose afterward — there is
/// no walk-then-play machinery left in this class at all; a queued/forced/matched pick always plays
/// immediately wherever the character already is by then. Position snaps immediately on arrival;
/// rotation deliberately doesn't — see Arrive()'s own comment for why.
/// </summary>
public sealed unsafe class AlignService : IDisposable
{
    public const float MaxAlignDistance = 2f;

    private delegate void RMIWalkDelegate(
        void* self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe,
        byte* a6, byte bAdditiveUnk);

    private readonly Hook<RMIWalkDelegate>? rmiWalkHook;
    private readonly PairingState pairingState;
    private readonly OffsetEngine offsetEngine;

    private volatile bool isWalking;
    private Vector3 destination;
    private float faceRotation;
    private long walkStartTick;

    /// True from the moment a walk arrives until Arrive()'s deferred rotation write actually fires —
    /// see Arrive()'s own comment for why that write waits instead of happening immediately. Treated
    /// as "still busy" the same as isWalking, so a second align attempt can't start mid-wait and stomp
    /// destination/faceRotation before the first one's write ever happens.
    private bool rotationWritePending;
    private int rotationWriteDelayTicks;

    private const float SnapDistance = 0.05f;
    private const float SlowdownRadius = 0.3f;
    private const long TimeoutMs = 2000;

    /// How long Arrive() silently waits before writing the rotation, instead of writing immediately
    /// and fighting whatever the walk-to-idle transition (or, worst observed, a delayed "face the
    /// direction you just moved" correction after arriving by walking backward) does to facing in the
    /// meantime. ~2s: simple and reliable over clever — let it all finish, then set it once.
    private const int RotationSettleDelayTicks = 120;

    public bool IsMovingToDestination => isWalking;

    /// True from the start of a walk until its deferred rotation write has landed — i.e. until the
    /// character is fully done moving and turning. A couple-preset play waits on this before prompting
    /// the partner, so both halves start from where the walk actually ended up (see CoupleRelayOutbox).
    public bool IsBusy => isWalking || rotationWritePending;
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
    }

    public void Dispose()
    {
        isWalking = false;
        rmiWalkHook?.Disable();
        rmiWalkHook?.Dispose();
    }

    /// Called every framework tick from Plugin — writes the pending rotation once
    /// rotationWriteDelayTicks has counted down (see Arrive()'s own comment for why it waits).
    public void Tick()
    {
        if (rotationWritePending)
        {
            if (rotationWriteDelayTicks > 0)
            {
                rotationWriteDelayTicks--;
            }
            else
            {
                rotationWritePending = false;
                ApplyRotationOnce(faceRotation);
                offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, faceRotation);
            }
        }
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
            WalkTo(targetPos, targetRotation);
        else
        {
            SnapToPosition(targetPos);
            ApplyRotationOnce(targetRotation);
            offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, targetRotation);
        }
    }

    /// Fire-and-forget walk toward the current target, called the instant a paired player queues a
    /// selection with the auto-align setting on (see CoupleQueueService.QueueSelection) — decoupled
    /// entirely from whether/when that queued pick actually plays. Auto-align is a paired-only
    /// feature: there's no duo/group pose to line up for outside a pairing, so this is never called
    /// while unpaired. No pose is attached to this walk at all — a queued/matched/force-selected pick
    /// always just plays immediately wherever the character already is by then; this method only ever
    /// moves the character. Every guard failure (busy, no target, blocked state, out of range, or the
    /// partner tie-break saying this side should yield — only one side actually walks when both
    /// target each other) is a silent no-op — a failed/skipped align here must never block queueing.
    public void TryAutoAlignOnQueue()
    {
        if (isWalking || rotationWritePending) return;

        var target = Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget;
        if (target == null) return;

        RefreshTargeting(target);

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null) return;

        if (player->Mode != CharacterModes.Normal) return;

        if (ShouldYieldToPartner(target)) return;

        var playerPos = new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z);
        var targetPos = target.Position;
        if (Vector3.Distance(playerPos, targetPos) > MaxAlignDistance) return;

        var targetRotation = target.Rotation;

        if (IsHookActive)
            WalkTo(targetPos, targetRotation);
        else
        {
            SnapToPosition(targetPos);
            ApplyRotationOnce(targetRotation);
            offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, targetRotation);
        }
    }

    /// Walks to the pairing partner's position and facing ahead of playing a couple preset — called
    /// from CoupleRelayOutbox.Start when auto-align is on. Unlike TryAutoAlignOnQueue, this finds the
    /// partner directly rather than requiring them to be targeted, and skips the partner tie-break:
    /// only the side that clicked the couple preset ever walks (the partner just accepts), so there's
    /// no risk of both sides walking at once. Same silent no-op on every guard failure (busy, partner
    /// not loaded, blocked state, out of range). True only if a walk (or snap fallback) actually
    /// started.
    public bool TryAutoAlignToPartner()
    {
        if (isWalking || rotationWritePending) return false;
        if (!pairingState.Active || pairingState.Peer is not { } peer) return false;
        if (PartnerAnchor.TryFindLive(peer) is not { } partner) return false;

        // Same held-target workaround as every other align path (see RefreshTargeting) — only needed,
        // and only applied, when the partner is the one currently targeted.
        var target = Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget;
        if (target != null && target.GameObjectId == partner.GameObjectId)
            RefreshTargeting(target);

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null) return false;
        if (player->Mode != CharacterModes.Normal) return false;

        var playerPos = new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z);
        if (Vector3.Distance(playerPos, partner.Position) > MaxAlignDistance) return false;

        if (IsHookActive)
            WalkTo(partner.Position, partner.Rotation);
        else
        {
            SnapToPosition(partner.Position);
            ApplyRotationOnce(partner.Rotation);
            offsetEngine.ForceDrawRotation(Plugin.ObjectTable.LocalPlayer, partner.Rotation);
        }
        return true;
    }

    /// True when the given target is the active pairing partner — the shared identity check
    /// ShouldYieldToPartner's tie-break comparison builds on.
    private bool IsTargetPartner(IGameObject target)
    {
        if (!pairingState.Active || pairingState.Peer is not { } peer) return false;
        if (target is not IPlayerCharacter targetPlayer) return false;

        var targetWorld = targetPlayer.HomeWorld.Value.Name.ExtractText();
        return peer.Matches(targetPlayer.Name.TextValue, targetWorld);
    }

    /// True when the align target is the active pairing partner and this side should not be the one
    /// to walk. Both clients independently compare the same two fixed identities (their own vs the
    /// partner's), so they always agree on which side yields — with no extra message exchanged —
    /// preventing both sides of a couple-play from simultaneously walking toward each other's
    /// pre-walk position when both have auto-align on. Deliberately never consulted from
    /// AlignToTarget (manual align) — see openspec/changes/auto-align-partner-tiebreak's proposal.md.
    private bool ShouldYieldToPartner(IGameObject target)
    {
        if (!IsTargetPartner(target)) return false;
        if (GetOwnIdentity() is not { } own) return false;

        // This side yields (does not walk) unless its own identity sorts strictly first.
        return string.CompareOrdinal(own.TellAddress, pairingState.Peer!.Value.TellAddress) >= 0;
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

    private void WalkTo(Vector3 dest, float rotation)
    {
        destination = dest;
        faceRotation = rotation;
        walkStartTick = Environment.TickCount64;
        isWalking = true;
        // A fresh walk supersedes any deferred rotation write still pending from a prior align.
        rotationWritePending = false;
    }

    private void Cancel()
    {
        if (!isWalking) return;
        isWalking = false;
        // No position/rotation snap on a real-movement cancel — the character stays wherever the
        // player moved it, and with no facing correction either (per align-to-target's spec). Nothing
        // further to do: there's no pose or other completion waiting on this walk any more than there
        // is on an ordinary arrival — see Arrive().
    }

    private void Arrive()
    {
        isWalking = false;

        // Position: always snap immediately — Encore's own proven approach (IcarusXIV/Encore,
        // MovementService.cs's Arrive()/Plugin.cs's SnapToPosition), a plain one-shot native write,
        // called synchronously here, still inside RMIWalkDetour's call stack. Never reported as
        // fighting anything the way rotation below can.
        SnapToPosition(destination);

        // Rotation: deliberately NOT written yet. Writing it immediately, right as the walk-to-idle
        // transition begins, means fighting it — worst observed after arriving by walking backward
        // (starting faced away from the target at close range never visually turns the character
        // during the walk itself, since backward camera-relative movement doesn't rotate the body),
        // where the correct facing would flicker in for a moment and then lose to a delayed "face the
        // direction you just moved" correction once the fight ended. Simple and reliable beats clever
        // here, per explicit direction: wait out RotationSettleDelayTicks in silence (see Tick()) so
        // every transition has time to fully finish, then write the rotation exactly once into a
        // character that's actually done moving.
        rotationWritePending = true;
        rotationWriteDelayTicks = RotationSettleDelayTicks;
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
