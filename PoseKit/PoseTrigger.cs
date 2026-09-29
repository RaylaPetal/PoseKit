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
/// Plays a pose and applies its offset once it's active. Sit/groundsit/doze set the selected pose
/// and cycle "/cpose" until the right variant is reached; other emotes use their normal command,
/// so emote unlocks and server checks still apply.
/// </summary>
public sealed unsafe class PoseTrigger(Configuration configuration, OffsetEngine offsetEngine, SimpleHeelsBridge simpleHeelsBridge)
{
    private readonly FurnitureScanner furnitureScanner = new();

    private (PoseIdentifier Pose, PoseOffset Offset, PresetAnchor? Anchor, bool Silent)? cyclingTarget;
    private int attempts;
    private long nextAttemptTime;

    /// Null unless the applied offset is partner-anchored.
    private PartnerTracking? partnerTracking;

    /// The outermost layer of the applied offset: base + partner correction + this.
    private PoseOffset boneCorrection = PoseOffset.Zero;

    public PoseOffset BoneCorrection => boneCorrection;

    /// Bumped whenever the offset is reset, so an in-progress Bone Align knows to cancel.
    public int OffsetGeneration { get; private set; }

    /// True when an offset is applied through either OffsetEngine or the SimpleHeels bridge.
    public bool HasAppliedOffset { get; private set; }

    /// True while cycling into a sit/groundsit/doze variant.
    public bool IsCycling => cyclingTarget != null;

    public void Trigger(NamedPose pose) => Trigger(pose.Pose, pose.Offset, pose.Anchor);

    /// <param name="silent">Suppresses the anchor-not-found notice, for a partner's relayed half.</param>
    public void Trigger(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor = null, bool silent = false) =>
        TriggerNow(pose, offset, anchor, silent);

    private void TriggerNow(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, bool silent)
    {
        partnerTracking = null;
        boneCorrection = PoseOffset.Zero;
        OffsetGeneration++;
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
    }

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
        partnerTracking = null;
        boneCorrection = PoseOffset.Zero;
        OffsetGeneration++;
    }

    /// Plays an emote command with no offset.
    public void TriggerCommand(string emoteCommand)
    {
        cyclingTarget = null;
        partnerTracking = null;
        boneCorrection = PoseOffset.Zero;
        OffsetGeneration++;
        ChatCommand.Execute($"/{emoteCommand} motion");
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
        ChatCommand.Execute(t.Command);
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
            ChatCommand.Execute(enterCommand);
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

    // Smaller changes are treated as network jitter.
    private const float PartnerTrackPositionThreshold = 0.02f;
    private const float PartnerTrackRotationThreshold = MathF.PI / 180f; // ~1°

    // Keeps a moving partner from sending a SimpleHeels command every frame.
    private const long BridgeReapplyIntervalMs = 250;

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
            NotifyPartnerAnchorFallback(tracking, $"Can't find {anchor.Partner.Name} nearby to position this preset around them.");
            return baseOffset;
        }

        RecomputePartnerCorrection(tracking, localPlayer, partner);
        tracking.LastApplyTick = Environment.TickCount64;
        return ComposeOffset(tracking.BaseOffset, tracking.LastCorrection);
    }

    /// Re-applies the partner correction when either character moves.
    private void TickPartnerTracking()
    {
        if (partnerTracking is not { } tracking || PartnerTrackingPaused) return;

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
        var offset = ComposeOffset(ComposeOffset(tracking.BaseOffset, tracking.LastCorrection), boneCorrection);

        // Skip re-applying an unchanged result.
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

    /// Field-wise, so Subtract is its exact inverse.
    private static PoseOffset ComposeOffset(PoseOffset baseOffset, PoseOffset correction) =>
        new() { Position = baseOffset.Position + correction.Position, Rotation = baseOffset.Rotation + correction.Rotation };

    private static PoseOffset Subtract(PoseOffset offset, PoseOffset correction) =>
        new() { Position = offset.Position - correction.Position, Rotation = offset.Rotation - correction.Rotation };
}
