using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using PoseKit.Furniture;
using PoseKit.Presets;
using PoseKit.Sync;

namespace PoseKit;

/// <summary>
/// Triggers a saved or Penumbra-discovered pose so its offset can be applied once active.
/// Sit/GroundSit/Doze use the stable PlayerState.SelectedPoses + "/cpose" cycling path, mirroring
/// Synastry-main/EmoteLink/PoseService.cs and the ExecutePose/UpdatePoseCycling flow in
/// Synastry-main/EmoteLink/Plugin.cs. Any other pose falls back to a literal "/emotename motion"
/// chat command (per the design doc's stated fallback) — deliberately not porting Synastry's
/// AOB-hooked AnywherePoseService/ActionTimelinePlayback, which bypass emote-unlock/server checks.
/// </summary>
public sealed unsafe class PoseTrigger(Configuration configuration, OffsetEngine offsetEngine, SimpleHeelsBridge simpleHeelsBridge)
{
    private readonly FurnitureScanner furnitureScanner = new();

    private (PoseIdentifier Pose, PoseOffset Offset, PresetAnchor? Anchor, bool Silent)? cyclingTarget;
    private int attempts;
    private long nextAttemptTime;

    /// Live state for a partner-anchored offset — see PartnerTracking and TickPartnerTracking. Null
    /// whenever the currently-applied offset (if any) isn't partner-anchored.
    private PartnerTracking? partnerTracking;

    /// True whenever an offset is currently applied through *either* path (OffsetEngine's own hook or
    /// the SimpleHeels bridge) — OffsetEngine.Active alone isn't enough to tell, since bridging
    /// deliberately leaves it false to avoid double-applying the offset.
    public bool HasAppliedOffset { get; private set; }

    public void Trigger(NamedPose pose) => Trigger(pose.Pose, pose.Offset, pose.Anchor);

    /// <param name="silent">Suppresses the chat notice ResolveOffset would otherwise print when the
    /// anchor can't be resolved — used for an accepted/auto-accepted relayed couple-preset half,
    /// where couple-preset-relay's spec calls for the pose/offset to still play with no error shown,
    /// unlike a local preset's own anchor failing (where the notice is useful, established
    /// feedback).</param>
    public void Trigger(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor = null, bool silent = false) =>
        TriggerNow(pose, offset, anchor, silent);

    private void TriggerNow(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, bool silent)
    {
        partnerTracking = null;
        switch (pose.EmoteModeId)
        {
            case 1: EnterPoseCycle(pose, offset, anchor, silent, EmoteController.PoseType.GroundSit, "/groundsit"); break;
            case 2: EnterPoseCycle(pose, offset, anchor, silent, EmoteController.PoseType.Sit, "/sit"); break;
            case 3: EnterPoseCycle(pose, offset, anchor, silent, EmoteController.PoseType.Doze, "/doze"); break;
            default:
                cyclingTarget = null;
                if (pose.SlashCommand is not { } command) break; // no resolvable trigger — don't fake one
                ChatCommand.Execute($"/{command} motion");
                ApplyOffset(ResolveOffset(offset, anchor, silent));
                break;
        }
    }

    /// Routes the offset through SimpleHeels' "/heels temp set" command (so Mare/Snowcloak/etc. sync
    /// it to nearby players) when bridging is enabled and SimpleHeels is actually loaded, otherwise
    /// falls back to PoseKit's own local-only OffsetEngine hook. Never both at once — see
    /// SimpleHeelsBridge's class doc for why. The only entry point for setting an offset — the
    /// live-offset editor (PresetButtonsPanel) calls this too rather than touching OffsetEngine
    /// directly, so bridging isn't silently bypassed.
    public void ApplyOffset(PoseOffset offset)
    {
        // DesiredOffset is kept up to date regardless of routing — it's the single source of truth
        // the live-offset editor reads back to display current values, bridging or not.
        offsetEngine.DesiredOffset = offset;

        if (configuration.BridgeOffsetToSimpleHeels && simpleHeelsBridge.IsLoaded)
        {
            offsetEngine.Active = false;
            simpleHeelsBridge.Apply(offset);
        }
        else
        {
            offsetEngine.Active = true;
        }

        HasAppliedOffset = true;
    }

    /// The live-offset editor's entry point: same as ApplyOffset, except that while a partner-anchored
    /// offset is being tracked, the edit is folded back into the tracked base offset (edited minus the
    /// correction currently applied) — otherwise the next recompute would rebuild the offset from the
    /// old base and silently discard the nudge. Composition is field-wise (see ComposeOffset), so this
    /// subtraction is exact.
    public void ApplyManualOffset(PoseOffset edited)
    {
        if (partnerTracking is { } tracking)
        {
            tracking.BaseOffset = new PoseOffset
            {
                Position = edited.Position - tracking.LastCorrection.Position,
                Rotation = edited.Rotation - tracking.LastCorrection.Rotation,
            };
        }
        ApplyOffset(edited);
    }

    /// The offset a partner-anchored preset should be saved back as by "Update preset" — the tracked
    /// base, without the live partner correction folded in (so replaying it doesn't double-apply the
    /// correction). False when no partner-anchored offset is being tracked.
    public bool TryGetTrackedBaseOffset(out PoseOffset baseOffset)
    {
        baseOffset = partnerTracking?.BaseOffset ?? PoseOffset.Zero;
        return partnerTracking != null;
    }

    /// Clears whichever path is currently applying the offset. Safe to call unconditionally — both
    /// OffsetEngine.Reset and SimpleHeelsBridge.Clear are no-ops if nothing was applied.
    public void ClearOffset(IPlayerCharacter? localPlayer)
    {
        offsetEngine.Reset(localPlayer);
        simpleHeelsBridge.Clear();
        HasAppliedOffset = false;
        partnerTracking = null;
    }

    /// Directly issues a known slash-emote command (e.g. from a Penumbra option's explicit
    /// "(/command)" naming hint) — no pose-cycling, no offset; the mod's own redirect handles the
    /// visual.
    public void TriggerCommand(string emoteCommand)
    {
        cyclingTarget = null;
        partnerTracking = null;
        ChatCommand.Execute($"/{emoteCommand} motion");
    }

    /// Re-enters the same sit/groundsit/doze variant after it unexpectedly ended for a reason other
    /// than the player actually moving. Freecam's own EmoteController.cancelEmote hook (see
    /// FreeCamInput.cs) is the primary defense against this now, so this should rarely fire in
    /// practice — kept as a defensive fallback (see openspec/changes/preserve-emote-during-freecam).
    /// Deliberately skips offset application, unlike EnterPoseCycle: nothing cleared the offset on
    /// this path, so it doesn't need reapplying, and doing so would turn on PoseKit's own offset
    /// tracking even for a player who never used it. Silently does nothing for any other emote —
    /// this is specifically the sit/groundsit/doze recovery path, not a general re-trigger.
    public void RestorePose(PoseIdentifier pose)
    {
        (EmoteController.PoseType PoseType, string Command)? target = pose.EmoteModeId switch
        {
            1 => (EmoteController.PoseType.GroundSit, "/groundsit"),
            2 => (EmoteController.PoseType.Sit, "/sit"),
            3 => (EmoteController.PoseType.Doze, "/doze"),
            _ => null,
        };
        if (target is not { } t) return;

        var state = PlayerState.Instance();
        if (state != null) state->SelectedPoses[(int)t.PoseType] = pose.CPoseState;
        ChatCommand.Execute(t.Command);
    }

    private void EnterPoseCycle(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, bool silent, EmoteController.PoseType poseType, string enterCommand)
    {
        var currentPose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        var alreadyInThatEmote = currentPose is { } c && c.EmoteModeId == pose.EmoteModeId;

        if (alreadyInThatEmote)
        {
            // Deliberately don't write SelectedPoses here — mirrors Synastry-main/EmoteLink's own
            // ExecutePose, which notes doing so also changes CPoseState immediately, making the
            // cycling check below believe the target's already reached before the animation has
            // actually transitioned. Triggering a different option while already in the same
            // pose loop needs a redraw instead: the currently-playing animation's resolved files
            // are already loaded, and Penumbra won't re-check which file a redirect now points to
            // without being told to — without this, switching options mid-pose can keep showing
            // the previous animation.
            ChatCommand.Execute("/penumbra redraw self");
        }
        else
        {
            var state = PlayerState.Instance();
            if (state != null) state->SelectedPoses[(int)poseType] = pose.CPoseState;
            ChatCommand.Execute(enterCommand);
        }

        cyclingTarget = (pose, offset, anchor, silent);
        attempts = 0;
        nextAttemptTime = Environment.TickCount64 + (alreadyInThatEmote ? 150 : 500);
    }

    private const int CposeAttemptDelayMs = 100;
    // Widened from Synastry-main/EmoteLink's original 8 (~800ms after the initial settle, ~1.3s
    // total) — live testing showed the pose-enter command sometimes not registering within that
    // budget specifically when other plugins were doing heavy concurrent work in the same window
    // (Penumbra resource loads/redraws, Mare building character data, etc., all visible in the same
    // log slice as a failed attempt). 20 attempts (~2s after settle, ~2.5s total) gives real headroom
    // under that kind of load without meaningfully changing the felt latency of a normal play.
    private const int MaxCposeAttempts = 20;

    /// Called every framework tick from Plugin; steps "/cpose" until the target CPoseState is
    /// reached, then hands the offset to OffsetEngine.
    public void Tick()
    {
        TickPartnerTracking();

        if (cyclingTarget is not { } target || Environment.TickCount64 < nextAttemptTime) return;

        var current = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        if (current is not { } c || c.EmoteModeId != target.Pose.EmoteModeId)
        {
            if (++attempts >= MaxCposeAttempts) cyclingTarget = null;
            else nextAttemptTime = Environment.TickCount64 + CposeAttemptDelayMs;
            return;
        }

        if (c.CPoseState == target.Pose.CPoseState)
        {
            ApplyOffset(ResolveOffset(target.Offset, target.Anchor, target.Silent));
            cyclingTarget = null;
            return;
        }

        ChatCommand.Execute("/cpose");
        if (++attempts >= MaxCposeAttempts) cyclingTarget = null;
        else nextAttemptTime = Environment.TickCount64 + CposeAttemptDelayMs;
    }

    /// Folds an anchor's correction into the base offset using the position/rotation at the moment
    /// the offset is actually about to be applied — not whenever Trigger() was first called.
    /// Sit/GroundSit/Doze poses can take several frames to actually settle into place (EnterPoseCycle
    /// waits on CPoseState via Tick), and entering the pose
    /// can itself change the character's facing (e.g. sitting snapping/settling rotation) before it's
    /// fully active — an eagerly-computed correction would use stale rotation and land wrong. The
    /// anchor correction dispatches to whichever of spot/furniture/partner the preset's PresetAnchor
    /// actually carries (the three are structurally exclusive already); a partner anchor additionally
    /// starts live tracking — see StartPartnerTracking.
    ///
    private PoseOffset ResolveOffset(PoseOffset baseOffset, PresetAnchor? anchor, bool silent = false)
    {
        var offset = baseOffset;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var rotationOffsetApplies = offsetEngine.RotationHookResolved;

        if (anchor?.Partner is { } partnerAnchor)
            return StartPartnerTracking(partnerAnchor, baseOffset, silent);

        if (localPlayer != null && anchor is { IsSet: true })
        {
            PoseOffset? correction = anchor.Spot != null
                ? anchor.Spot.TryComputeCorrection(localPlayer, Plugin.ClientState.TerritoryType, offset.Rotation, rotationOffsetApplies)
                : ResolveFurnitureCorrection(anchor.Furniture!, localPlayer, offset.Rotation, rotationOffsetApplies);

            if (correction is { } c)
                offset = new PoseOffset { Position = offset.Position + c.Position, Rotation = offset.Rotation + c.Rotation };
            else if (!silent)
            {
                var what = anchor.Spot != null ? "saved spot — different zone or too far away" : "furniture — none nearby";
                Plugin.ChatGui.PrintError($"[PoseKit] Can't restore this preset's {what}. Playing with just the offset.");
            }
        }

        return offset;
    }

    private PoseOffset? ResolveFurnitureCorrection(FurnitureAnchor furnitureAnchor, IPlayerCharacter localPlayer, float baseRotationOffset, bool rotationOffsetApplies)
    {
        var nearby = furnitureScanner.ScanNearby(localPlayer);
        var live = furnitureAnchor.TryFindLiveInstance(nearby, localPlayer.Position);
        return live is { } furniture ? furnitureAnchor.TryComputeCorrection(localPlayer, furniture, baseRotationOffset, rotationOffsetApplies) : null;
    }

    // Below these, a partner's (or this player's own) transform change is treated as network jitter
    // rather than real movement — keeps a still partner from re-applying the offset every tick.
    private const float PartnerTrackPositionThreshold = 0.02f;
    private const float PartnerTrackRotationThreshold = MathF.PI / 180f; // ~1°

    // The SimpleHeels bridge applies offsets via a chat command (and a sync upload behind it), so a
    // partner walking around mustn't turn into one "/heels temp set" per frame.
    private const long BridgeReapplyIntervalMs = 250;

    /// A partner-anchored offset's live state: the preset's own offset (BaseOffset — adjustable via
    /// ApplyManualOffset) is kept separate from the partner correction folded on top of it, so the
    /// correction can be recomputed as the partner (the root) moves without losing either one.
    private sealed class PartnerTracking(PartnerAnchor anchor, PoseOffset baseOffset, bool silent)
    {
        public PartnerAnchor Anchor { get; } = anchor;
        public PoseOffset BaseOffset { get; set; } = baseOffset;
        public bool Silent { get; } = silent;

        /// Zero whenever the anchor couldn't be resolved (partner not loaded yet, or out of range).
        public PoseOffset LastCorrection { get; set; } = PoseOffset.Zero;

        /// The partner's and this player's own transforms the last correction was computed against —
        /// null until the partner has been found at least once.
        public (Vector3 PartnerPosition, float PartnerRotation, Vector3 OwnPosition, float OwnRotation)? LastTransforms { get; set; }

        /// At most one fallback notice per play, per partner-anchor's spec.
        public bool NoticeShown { get; set; }
        public long LastApplyTick { get; set; }
    }

    /// Resolves a partner anchor for the first time and begins tracking it — tracking starts even if
    /// the partner isn't found or is out of range right now, so the correction still lands if they
    /// load in/come into range while the pose is held (e.g. the partner accepting the relay late and
    /// sitting down). Returns the offset to apply now.
    private PoseOffset StartPartnerTracking(PartnerAnchor anchor, PoseOffset baseOffset, bool silent)
    {
        var tracking = new PartnerTracking(anchor, baseOffset, silent);
        partnerTracking = tracking;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var partner = PartnerAnchor.TryFindLive(anchor.Partner);
        if (localPlayer == null || partner == null)
        {
            NotifyPartnerAnchorFallback(tracking, $"Can't find {anchor.Partner.Name} nearby to position this preset around them.");
            return baseOffset;
        }

        RecomputePartnerCorrection(tracking, localPlayer, partner);
        tracking.LastApplyTick = Environment.TickCount64;
        return ComposeOffset(tracking.BaseOffset, tracking.LastCorrection);
    }

    /// Called every tick: re-applies the partner correction once either character's actual transform
    /// has moved past the jitter thresholds. A partner who's unloaded just leaves the last applied
    /// offset in place until they're back (or tracking ends).
    private void TickPartnerTracking()
    {
        if (partnerTracking is not { } tracking) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;
        var partner = PartnerAnchor.TryFindLive(tracking.Anchor.Partner);
        if (partner == null) return;

        if (tracking.LastTransforms is { } last &&
            Vector3.Distance(partner.Position, last.PartnerPosition) <= PartnerTrackPositionThreshold &&
            Vector3.Distance(localPlayer.Position, last.OwnPosition) <= PartnerTrackPositionThreshold &&
            MathF.Abs(MathF.IEEERemainder(partner.Rotation - last.PartnerRotation, MathF.Tau)) <= PartnerTrackRotationThreshold &&
            MathF.Abs(MathF.IEEERemainder(localPlayer.Rotation - last.OwnRotation, MathF.Tau)) <= PartnerTrackRotationThreshold)
            return;

        var bridging = configuration.BridgeOffsetToSimpleHeels && simpleHeelsBridge.IsLoaded;
        if (bridging && Environment.TickCount64 - tracking.LastApplyTick < BridgeReapplyIntervalMs) return;

        RecomputePartnerCorrection(tracking, localPlayer, partner);
        var offset = ComposeOffset(tracking.BaseOffset, tracking.LastCorrection);

        // Crossing the thresholds doesn't always change the result (e.g. still out of range, so the
        // plain base offset stays applied) — skip the re-apply rather than re-issuing an identical one.
        var current = offsetEngine.DesiredOffset;
        if (Vector3.Distance(current.Position, offset.Position) <= 0.001f && MathF.Abs(current.Rotation - offset.Rotation) <= 0.0001f)
            return;

        ApplyOffset(offset);
        tracking.LastApplyTick = Environment.TickCount64;
    }

    private void RecomputePartnerCorrection(PartnerTracking tracking, IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        tracking.LastTransforms = (partner.Position, partner.Rotation, localPlayer.Position, localPlayer.Rotation);

        var correction = tracking.Anchor.TryComputeCorrection(localPlayer, partner, tracking.BaseOffset.Rotation, offsetEngine.RotationHookResolved);
        if (correction is { } c)
        {
            tracking.LastCorrection = c;
            return;
        }

        tracking.LastCorrection = PoseOffset.Zero;
        NotifyPartnerAnchorFallback(tracking,
            $"Too far from {tracking.Anchor.Partner.Name} to position this preset around them — move closer.");
    }

    private static void NotifyPartnerAnchorFallback(PartnerTracking tracking, string reason)
    {
        if (tracking.Silent || tracking.NoticeShown) return;
        tracking.NoticeShown = true;
        Plugin.ChatGui.PrintError($"[PoseKit] {reason} Playing with just the offset.");
    }

    /// Field-wise composition, the same way ResolveOffset folds spot/furniture corrections in — which
    /// is what keeps ApplyManualOffset's inverse (edited minus correction) exact.
    private static PoseOffset ComposeOffset(PoseOffset baseOffset, PoseOffset correction) =>
        new() { Position = baseOffset.Position + correction.Position, Rotation = baseOffset.Rotation + correction.Rotation };
}
