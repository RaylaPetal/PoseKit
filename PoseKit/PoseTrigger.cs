using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using PoseKit.Furniture;
using PoseKit.Presets;
using PoseKit.Sync;

namespace PoseKit;

/// <summary>
/// Plays a pose and applies its offset once it's active. Sit/groundsit/doze set the selected pose
/// and cycle "/cpose" until the right variant is reached; other emotes use their normal command,
/// so emote unlocks and server checks still apply. Standing idles (pose mode 0) step "/cpose" while
/// standing, more slowly, and hold their offset while the character stays on the idle.
/// </summary>
public sealed unsafe class PoseTrigger(Configuration configuration, OffsetEngine offsetEngine, SimpleHeelsBridge simpleHeelsBridge, AnywherePose anywherePose)
{
    private readonly FurnitureScanner furnitureScanner = new();

    private (PoseIdentifier Pose, PoseOffset Offset, PresetAnchor? Anchor, bool Silent)? cyclingTarget;
    private int attempts;
    private long nextAttemptTime;

    /// Set while stepping "/cpose" into a standing idle.
    private sealed class IdleCycle(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, bool silent)
    {
        public PoseIdentifier Pose { get; } = pose;
        public PoseOffset Offset { get; } = offset;
        public PresetAnchor? Anchor { get; } = anchor;
        public bool Silent { get; } = silent;
        public int Steps { get; set; }

        /// False until the first check, which redraws instead of stepping when already on the idle.
        public bool Checked { get; set; }

        /// Set once the toggle that stands up from a groundsit, sit or doze was sent.
        public long? StandUpDeadline { get; set; }
    }

    private IdleCycle? idleCycle;
    private long nextIdleStepTime;

    /// The idle reached by the last idle play and where the character stood. Its offset lasts while both
    /// hold, since standing reads as "no pose" and would otherwise clear it at once.
    private (byte Idle, Vector3 Position)? idleHold;

    /// True while an idle play's offset is held.
    public bool HoldsIdle => idleHold != null;

    /// Null unless the applied offset is partner-anchored.
    private PartnerTracking? partnerTracking;

    /// The outermost layer of the applied offset: base + partner correction + this.
    private PoseOffset boneCorrection = PoseOffset.Zero;

    public PoseOffset BoneCorrection => boneCorrection;

    /// Bumped whenever the offset is reset, so an in-progress Bone Align knows to cancel.
    public int OffsetGeneration { get; private set; }

    /// True when an offset is applied through either OffsetEngine or the SimpleHeels bridge.
    public bool HasAppliedOffset { get; private set; }

    /// True while cycling into a sit/groundsit/doze variant or a standing idle.
    public bool IsCycling => cyclingTarget != null || idleCycle != null;

    /// Raised for every pose or emote PoseKit plays, so the pose lock can tell PoseKit's poses from
    /// ones the player typed.
    public event Action? Played;

    public void Trigger(NamedPose pose) => Trigger(pose.Pose, pose.Offset, pose.Anchor);

    /// <param name="silent">Suppresses the anchor-not-found notice, for a partner's relayed half.</param>
    public void Trigger(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor = null, bool silent = false) =>
        TriggerNow(pose, offset, anchor, silent);

    private void TriggerNow(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, bool silent)
    {
        partnerTracking = null;
        boneCorrection = PoseOffset.Zero;
        OffsetGeneration++;
        idleCycle = null;
        idleHold = null;
        Played?.Invoke();
        switch (pose.EmoteModeId)
        {
            case PoseIdentifier.StandingIdleMode:
                cyclingTarget = null;
                // Drop a seat pose's offset now: left in place, the plugin would clear it on standing up,
                // and this play's context with it.
                if (HasAppliedOffset) ClearOffset(Plugin.ObjectTable.LocalPlayer);
                idleCycle = new IdleCycle(pose, offset, anchor, silent);
                nextIdleStepTime = Environment.TickCount64;
                break;
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

    /// The only way to set an offset: through SimpleHeels when bridging, otherwise OffsetEngine.
    public void ApplyOffset(PoseOffset offset)
    {
        // Always kept current, since the live-offset editor reads it back.
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

    /// For the live-offset editor. While partner tracking, the edit is folded into the tracked base
    /// offset so the next recompute doesn't discard it.
    public void ApplyManualOffset(PoseOffset edited)
    {
        if (partnerTracking is { } tracking)
            tracking.BaseOffset = Subtract(Subtract(edited, tracking.LastCorrection), boneCorrection);
        ApplyOffset(edited);
        ManualOffsetEdited?.Invoke();
    }

    /// Raised on every live-offset slider or gizmo edit.
    public event Action? ManualOffsetEdited;

    /// Replaces (never stacks) the bone-align correction. It's kept separate so it's never saved into
    /// a preset.
    public void SetBoneCorrection(PoseOffset correction)
    {
        var withoutOld = Subtract(offsetEngine.DesiredOffset, boneCorrection);
        boneCorrection = correction;
        ApplyOffset(ComposeOffset(withoutOld, correction));
    }

    /// Pauses partner tracking so Bone Align samples against a fixed offset.
    public bool PartnerTrackingPaused { get; set; }

    /// The applied offset without the bone-align correction.
    public PoseOffset GetOffsetForNewPreset() => Subtract(offsetEngine.DesiredOffset, boneCorrection);

    /// Also without the partner correction, which the preset's anchor recomputes on replay.
    public PoseOffset GetOffsetForUpdate() =>
        partnerTracking is { } tracking ? Subtract(GetOffsetForNewPreset(), tracking.LastCorrection) : GetOffsetForNewPreset();

    public void ClearOffset(IPlayerCharacter? localPlayer)
    {
        offsetEngine.Reset(localPlayer);
        simpleHeelsBridge.Clear();
        HasAppliedOffset = false;
        idleHold = null;
        partnerTracking = null;
        boneCorrection = PoseOffset.Zero;
        OffsetGeneration++;
    }

    /// Plays an emote command with no offset.
    public void TriggerCommand(string emoteCommand) => TriggerCommand(emoteCommand, PoseOffset.Zero, null);

    /// An emote with an offset, such as standing on a partner's spot facing their way.
    public void TriggerCommand(string emoteCommand, PoseOffset offset, PresetAnchor? anchor)
    {
        cyclingTarget = null;
        idleCycle = null;
        idleHold = null;
        partnerTracking = null;
        boneCorrection = PoseOffset.Zero;
        OffsetGeneration++;
        Played?.Invoke();
        ChatCommand.Execute($"/{emoteCommand} motion");
        if (anchor != null) ApplyOffset(ResolveOffset(offset, anchor));
    }

    /// Re-enters a sit/groundsit/doze variant that ended without the player moving. Fallback for
    /// freecam; the offset is left as it was.
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
        EnterEmote(t.PoseType, t.Command);
    }

    /// Doze goes through the emote agent so it works away from a bed or chair; everything else, and
    /// doze when that call isn't available, uses its slash command.
    private void EnterEmote(EmoteController.PoseType poseType, string command)
    {
        if (poseType == EmoteController.PoseType.Doze && anywherePose.EnterDoze()) return;
        ChatCommand.Execute(command);
    }

    private void EnterPoseCycle(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, bool silent, EmoteController.PoseType poseType, string enterCommand)
    {
        var currentPose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        var alreadyInThatEmote = currentPose is { } c && c.EmoteModeId == pose.EmoteModeId;

        if (alreadyInThatEmote)
        {
            // Writing SelectedPoses here would change CPoseState immediately and fool the cycling
            // check. Redraw instead so a newly selected mod option is picked up mid-pose.
            ChatCommand.Execute("/penumbra redraw self");
        }
        else
        {
            var state = PlayerState.Instance();
            if (state != null) state->SelectedPoses[(int)poseType] = pose.CPoseState;
            EnterEmote(poseType, enterCommand);
        }

        cyclingTarget = (pose, offset, anchor, silent);
        attempts = 0;
        nextAttemptTime = Environment.TickCount64 + (alreadyInThatEmote ? 150 : 500);
    }

    private const int CposeAttemptDelayMs = 100;
    // About 2 seconds, enough headroom when the game is busy loading.
    private const int MaxCposeAttempts = 20;

    /// Steps "/cpose" until the target variant is reached, then applies the offset.
    public void Tick()
    {
        TickPartnerTracking();
        TickIdleHold();
        TickIdleCycle();

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

    // Standing /cpose plays a transition first; Oathbound found the idle state dependable after this.
    private const int IdleStepDelayMs = 700;
    // One full cycle of the 7 standing idles.
    private const int MaxIdleSteps = 7;
    private const int StandUpTimeoutMs = 3000;
    // Walking moves the real position; draw offsets don't, so partner tracking never trips this.
    private const float IdleHoldMoveThreshold = 0.1f;

    /// Stands up from a seat pose if needed, then steps "/cpose" until the target idle is active.
    private void TickIdleCycle()
    {
        if (idleCycle is not { } cycle || Environment.TickCount64 < nextIdleStepTime) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var character = (Character*)(localPlayer?.Address ?? 0);
        if (character == null)
        {
            idleCycle = null;
            return;
        }

        var name = cycle.Pose.DisplayName;
        var now = Environment.TickCount64;
        if (PoseIdentifier.FromCharacter(character) is { } loop)
        {
            if (loop.EmoteModeId is not (1 or 2 or 3))
            {
                FailIdle($"Stop the current emote first to play {name}.");
                return;
            }
            if (cycle.StandUpDeadline is not { } deadline)
            {
                // These emotes toggle: sending them again while in them stands the character up.
                ChatCommand.Execute(loop.EmoteModeId switch { 1 => "/groundsit", 2 => "/sit", _ => "/doze" });
                cycle.StandUpDeadline = now + StandUpTimeoutMs;
            }
            else if (now > deadline)
            {
                FailIdle($"Couldn't stand up to play {name}.");
                return;
            }
            nextIdleStepTime = now + CposeAttemptDelayMs;
            return;
        }

        if (character->Mode != CharacterModes.Normal)
        {
            // Getting up passes through other modes for a moment.
            if (cycle.StandUpDeadline is { } deadline && now <= deadline)
            {
                nextIdleStepTime = now + CposeAttemptDelayMs;
                return;
            }
            FailIdle($"Idles play only while standing, not mounted or busy. Couldn't play {name}.");
            return;
        }

        if (character->IsWeaponDrawn)
        {
            FailIdle($"Sheathe your weapon to play {name}.");
            return;
        }

        if (character->EmoteController.CPoseState == cycle.Pose.CPoseState)
        {
            if (!cycle.Checked && cycle.StandUpDeadline == null)
            {
                // Already on it: redraw so the newly selected option shows, then check again in case the
                // redraw reset the idle.
                cycle.Checked = true;
                ChatCommand.Execute("/penumbra redraw self");
                nextIdleStepTime = now + IdleStepDelayMs;
                return;
            }

            idleCycle = null;
            ApplyOffset(ResolveOffset(cycle.Offset, cycle.Anchor, cycle.Silent));
            idleHold = (cycle.Pose.CPoseState, localPlayer!.Position);
            return;
        }

        cycle.Checked = true;
        if (cycle.Steps >= MaxIdleSteps)
        {
            FailIdle($"Couldn't reach {name}: still on Idle {character->EmoteController.CPoseState} after a full /cpose cycle.");
            return;
        }

        ChatCommand.Execute("/cpose");
        cycle.Steps++;
        nextIdleStepTime = now + IdleStepDelayMs;
    }

    private void FailIdle(string message)
    {
        idleCycle = null;
        Plugin.ChatGui.PrintError($"[PoseKit] {message}");
    }

    /// Ends the hold once the character walks, changes idle or leaves standing; the plugin then clears the
    /// offset as it does when a pose ends.
    private void TickIdleHold()
    {
        if (idleHold is not { } hold) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var current = PoseIdentifier.StandingIdle((Character*)(localPlayer?.Address ?? 0));
        if (localPlayer == null || current?.CPoseState != hold.Idle ||
            Vector3.Distance(localPlayer.Position, hold.Position) > IdleHoldMoveThreshold)
            idleHold = null;
    }

    /// Adds the anchor correction. Computed when the offset is applied, not when triggered, since
    /// entering a pose can change the character's facing. A partner anchor starts live tracking.
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
                offset = ComposeOffset(offset, c);
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

    // Smaller changes are treated as network jitter.
    private const float PartnerTrackPositionThreshold = 0.02f;
    private const float PartnerTrackRotationThreshold = MathF.PI / 180f; // ~1°

    // Keeps a moving partner from sending a SimpleHeels command every frame.
    private const long BridgeReapplyIntervalMs = 250;

    // Covers a half that starts a beat later and a seat play's /cpose cycling (about 2 s).
    private const long PartnerSettleGraceMs = 5000;

    // Inside a looping pose the partner's real position stays put (their offsets only move where
    // they're drawn), so more than this means they walked off. Well above jitter, below a step.
    private const float PartnerSettledMoveThreshold = 0.5f;

    /// Keeps the preset's own offset separate from the partner correction, so the correction can be
    /// recomputed as the partner moves.
    private sealed class PartnerTracking(PartnerAnchor anchor, PoseOffset baseOffset, bool silent)
    {
        public PartnerAnchor Anchor { get; } = anchor;
        public PoseOffset BaseOffset { get; set; } = baseOffset;
        public bool Silent { get; } = silent;

        /// Zero when the partner isn't loaded or is out of range.
        public PoseOffset LastCorrection { get; set; } = PoseOffset.Zero;

        public (Vector3 PartnerPosition, float PartnerRotation, Vector3 OwnPosition, float OwnRotation)? LastTransforms { get; set; }

        /// True once a correction has been applied, so losing range later freezes rather than
        /// dropping back to the plain offset.
        public bool HasCorrection { get; set; }

        /// When the partner was first found, which starts the settle grace period. A partner who
        /// loads late still gets the full period.
        public long? FoundTick { get; set; }

        /// The partner's real position once seen still in a looping pose; null while settling.
        public Vector3? SettledPosition { get; set; }

        /// Set when the partner leaves their pose or walks off. The last correction stays applied
        /// and is never recomputed for this play: the player stays where they're drawn instead of
        /// following. Kept rather than discarded so preset updates can still subtract it.
        public bool Frozen { get; set; }

        /// At most one fallback notice per play.
        public bool NoticeShown { get; set; }
        public long LastApplyTick { get; set; }
    }

    /// Starts tracking even if the partner isn't found yet, so the correction lands once they are.
    private PoseOffset StartPartnerTracking(PartnerAnchor anchor, PoseOffset baseOffset, bool silent)
    {
        var tracking = new PartnerTracking(anchor, baseOffset, silent);
        partnerTracking = tracking;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var partner = PartnerAnchor.TryFindLive(anchor.Partner);
        if (localPlayer == null || partner == null)
        {
            NotifyPartnerAnchorFallback(tracking, $"Can't find {anchor.Partner.Name} nearby to line you up with them.");
            return baseOffset;
        }

        RecomputePartnerCorrection(tracking, localPlayer, partner);
        tracking.LastApplyTick = Environment.TickCount64;
        return ComposeOffset(tracking.BaseOffset, tracking.LastCorrection);
    }

    /// Re-applies the partner correction when either character moves.
    private void TickPartnerTracking()
    {
        if (partnerTracking is not { } tracking || tracking.Frozen || PartnerTrackingPaused) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;
        var partner = PartnerAnchor.TryFindLive(tracking.Anchor.Partner);
        if (partner == null) return;

        // Checked before recomputing, so the frame the partner starts walking doesn't drag us along.
        if (PartnerLeftPose(tracking, partner))
        {
            tracking.Frozen = true;
            return;
        }

        if (tracking.LastTransforms is { } last &&
            Vector3.Distance(partner.Position, last.PartnerPosition) <= PartnerTrackPositionThreshold &&
            Vector3.Distance(localPlayer.Position, last.OwnPosition) <= PartnerTrackPositionThreshold &&
            MathF.Abs(MathF.IEEERemainder(partner.Rotation - last.PartnerRotation, MathF.Tau)) <= PartnerTrackRotationThreshold &&
            MathF.Abs(MathF.IEEERemainder(localPlayer.Rotation - last.OwnRotation, MathF.Tau)) <= PartnerTrackRotationThreshold)
            return;

        var bridging = configuration.BridgeOffsetToSimpleHeels && simpleHeelsBridge.IsLoaded;
        if (bridging && Environment.TickCount64 - tracking.LastApplyTick < BridgeReapplyIntervalMs) return;

        RecomputePartnerCorrection(tracking, localPlayer, partner);
        var offset = ComposeOffset(ComposeOffset(tracking.BaseOffset, tracking.LastCorrection), boneCorrection);

        // Skip re-applying an unchanged result.
        var current = offsetEngine.DesiredOffset;
        if (Vector3.Distance(current.Position, offset.Position) <= 0.001f && MathF.Abs(current.Rotation - offset.Rotation) <= 0.0001f)
            return;

        ApplyOffset(offset);
        tracking.LastApplyTick = Environment.TickCount64;
    }

    /// Follows the partner freely while they settle into their pose (a seat snap moves their real
    /// position), then reports true once they leave it or walk off. A partner never seen in a loop
    /// within the grace period counts as left: their half isn't a looping pose to stay matched to.
    private static bool PartnerLeftPose(PartnerTracking tracking, IPlayerCharacter partner)
    {
        var now = Environment.TickCount64;
        tracking.FoundTick ??= now;
        var inLoop = PoseIdentifier.FromCharacter(partner) != null;

        if (tracking.SettledPosition is { } settled)
            return !inLoop || Vector3.Distance(partner.Position, settled) > PartnerSettledMoveThreshold;

        // Settle only once they're also still, so a slide into a seat lands before the spot is taken.
        var still = tracking.LastTransforms is { } last &&
                    Vector3.Distance(partner.Position, last.PartnerPosition) <= PartnerTrackPositionThreshold;
        if (inLoop && still)
        {
            tracking.SettledPosition = partner.Position;
            return false;
        }

        return now - tracking.FoundTick.Value >= PartnerSettleGraceMs;
    }

    private void RecomputePartnerCorrection(PartnerTracking tracking, IPlayerCharacter localPlayer, IPlayerCharacter partner)
    {
        tracking.LastTransforms = (partner.Position, partner.Rotation, localPlayer.Position, localPlayer.Rotation);

        var correction = tracking.Anchor.TryComputeCorrection(localPlayer, partner, tracking.BaseOffset.Rotation, offsetEngine.RotationHookResolved);
        if (correction is { } c)
        {
            tracking.LastCorrection = c;
            tracking.HasCorrection = true;
            return;
        }

        // Out of range after lining up: keep the drawn spot rather than snapping back to the real one.
        if (tracking.HasCorrection)
        {
            tracking.Frozen = true;
            NotifyPartnerAnchorFallback(tracking,
                $"{tracking.Anchor.Partner.Name} moved too far away to keep you lined up with them.", "Staying where you are.");
            return;
        }

        tracking.LastCorrection = PoseOffset.Zero;
        NotifyPartnerAnchorFallback(tracking,
            $"Too far from {tracking.Anchor.Partner.Name} to line you up with them — move closer.");
    }

    private static void NotifyPartnerAnchorFallback(PartnerTracking tracking, string reason, string outcome = "Playing with just the offset.")
    {
        if (tracking.Silent || tracking.NoticeShown) return;
        tracking.NoticeShown = true;
        Plugin.ChatGui.PrintError($"[PoseKit] {reason} {outcome}");
    }

    /// Field-wise, so Subtract is its exact inverse.
    public static PoseOffset ComposeOffset(PoseOffset baseOffset, PoseOffset correction) =>
        new() { Position = baseOffset.Position + correction.Position, Rotation = baseOffset.Rotation + correction.Rotation };

    public static PoseOffset Subtract(PoseOffset offset, PoseOffset correction) =>
        new() { Position = offset.Position - correction.Position, Rotation = offset.Rotation - correction.Rotation };
}
