using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
    private (PoseIdentifier Pose, PoseOffset Offset, LocationAnchor? Anchor)? cyclingTarget;
    private int attempts;
    private long nextAttemptTime;

    /// True whenever an offset is currently applied through *either* path (OffsetEngine's own hook or
    /// the SimpleHeels bridge) — OffsetEngine.Active alone isn't enough to tell, since bridging
    /// deliberately leaves it false to avoid double-applying the offset.
    public bool HasAppliedOffset { get; private set; }

    public void Trigger(NamedPose pose) => Trigger(pose.Pose, pose.Offset, pose.Anchor);

    public void Trigger(PoseIdentifier pose, PoseOffset offset, LocationAnchor? anchor = null)
    {
        switch (pose.EmoteModeId)
        {
            case 1: EnterPoseCycle(pose, offset, anchor, EmoteController.PoseType.GroundSit, "/groundsit"); break;
            case 2: EnterPoseCycle(pose, offset, anchor, EmoteController.PoseType.Sit, "/sit"); break;
            case 3: EnterPoseCycle(pose, offset, anchor, EmoteController.PoseType.Doze, "/doze"); break;
            default:
                cyclingTarget = null;
                if (pose.SlashCommand is not { } command) break; // no resolvable trigger — don't fake one
                ChatCommand.Execute($"/{command} motion");
                ApplyOffset(ResolveOffset(offset, anchor));
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

    /// Clears whichever path is currently applying the offset. Safe to call unconditionally — both
    /// OffsetEngine.Reset and SimpleHeelsBridge.Clear are no-ops if nothing was applied.
    public void ClearOffset(IPlayerCharacter? localPlayer)
    {
        offsetEngine.Reset(localPlayer);
        simpleHeelsBridge.Clear();
        HasAppliedOffset = false;
    }

    /// Directly issues a known slash-emote command (e.g. from a Penumbra option's explicit
    /// "(/command)" naming hint) — no pose-cycling, no offset; the mod's own redirect handles the visual.
    public void TriggerCommand(string emoteCommand)
    {
        cyclingTarget = null;
        ChatCommand.Execute($"/{emoteCommand} motion");
    }

    private void EnterPoseCycle(PoseIdentifier pose, PoseOffset offset, LocationAnchor? anchor, EmoteController.PoseType poseType, string enterCommand)
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

        cyclingTarget = (pose, offset, anchor);
        attempts = 0;
        // 150ms/500ms initial settle delay and the 100ms/8-attempt cycling budget below both match
        // Synastry-main/EmoteLink/Plugin.cs's UpdatePoseCycling exactly, rather than guessing at
        // different timing — that implementation is a real, field-tested reference for this same
        // "/cpose" polling mechanism.
        nextAttemptTime = Environment.TickCount64 + (alreadyInThatEmote ? 150 : 500);
    }

    private const int CposeAttemptDelayMs = 100;
    private const int MaxCposeAttempts = 8;

    /// Called every framework tick from Plugin; steps "/cpose" until the target CPoseState is
    /// reached, then hands the offset to OffsetEngine.
    public void Tick()
    {
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
            ApplyOffset(ResolveOffset(target.Offset, target.Anchor));
            cyclingTarget = null;
            return;
        }

        ChatCommand.Execute("/cpose");
        if (++attempts >= MaxCposeAttempts) cyclingTarget = null;
        else nextAttemptTime = Environment.TickCount64 + CposeAttemptDelayMs;
    }

    /// Folds a location anchor's correction into the base offset using the position/rotation at
    /// the moment the offset is actually about to be applied — not whenever Trigger() was first
    /// called. Sit/GroundSit/Doze poses can take several frames to actually settle into place
    /// (EnterPoseCycle waits on CPoseState via Tick), and entering the pose can itself change the
    /// character's facing (e.g. sitting snapping/settling rotation) before it's fully active — an
    /// eagerly-computed correction would use stale rotation and land wrong.
    private PoseOffset ResolveOffset(PoseOffset baseOffset, LocationAnchor? anchor)
    {
        if (anchor == null) return baseOffset;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var correction = localPlayer != null
            ? anchor.TryComputeCorrection(localPlayer, Plugin.ClientState.TerritoryType, baseOffset.Rotation)
            : null;
        if (correction is not { } c)
        {
            Plugin.ChatGui.PrintError("[PoseKit] Can't restore this preset's saved spot — different zone or too far away. Playing with just the offset.");
            return baseOffset;
        }

        return new PoseOffset
        {
            Position = baseOffset.Position + c.Position,
            Rotation = baseOffset.Rotation + c.Rotation,
        };
    }
}
