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
    private (PoseIdentifier Pose, PoseOffset Offset)? cyclingTarget;
    private int attempts;
    private long nextAttemptTime;

    /// True whenever an offset is currently applied through *either* path (OffsetEngine's own hook or
    /// the SimpleHeels bridge) — OffsetEngine.Active alone isn't enough to tell, since bridging
    /// deliberately leaves it false to avoid double-applying the offset.
    public bool HasAppliedOffset { get; private set; }

    public void Trigger(NamedPose pose) => Trigger(pose.Pose, pose.Offset);

    public void Trigger(PoseIdentifier pose, PoseOffset offset)
    {
        switch (pose.EmoteModeId)
        {
            case 1: EnterPoseCycle(pose, offset, EmoteController.PoseType.GroundSit, "/groundsit"); break;
            case 2: EnterPoseCycle(pose, offset, EmoteController.PoseType.Sit, "/sit"); break;
            case 3: EnterPoseCycle(pose, offset, EmoteController.PoseType.Doze, "/doze"); break;
            default:
                cyclingTarget = null;
                if (pose.SlashCommand is not { } command) break; // no resolvable trigger — don't fake one
                ChatCommand.Execute($"/{command} motion");
                ApplyOffset(offset);
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

    private void EnterPoseCycle(PoseIdentifier pose, PoseOffset offset, EmoteController.PoseType poseType, string enterCommand)
    {
        var currentPose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        var alreadyInThatEmote = currentPose is { } c && c.EmoteModeId == pose.EmoteModeId;

        if (!alreadyInThatEmote)
        {
            var state = PlayerState.Instance();
            if (state != null) state->SelectedPoses[(int)poseType] = pose.CPoseState;
            ChatCommand.Execute(enterCommand);
        }

        cyclingTarget = (pose, offset);
        attempts = 0;
        nextAttemptTime = Environment.TickCount64 + (alreadyInThatEmote ? 0 : 500);
    }

    /// Called every framework tick from Plugin; steps "/cpose" until the target CPoseState is
    /// reached, then hands the offset to OffsetEngine.
    public void Tick()
    {
        if (cyclingTarget is not { } target || Environment.TickCount64 < nextAttemptTime) return;

        var current = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        if (current is not { } c || c.EmoteModeId != target.Pose.EmoteModeId)
        {
            if (++attempts >= 8) cyclingTarget = null;
            else nextAttemptTime = Environment.TickCount64 + 100;
            return;
        }

        if (c.CPoseState == target.Pose.CPoseState)
        {
            ApplyOffset(target.Offset);
            cyclingTarget = null;
            return;
        }

        ChatCommand.Execute("/cpose");
        if (++attempts >= 8) cyclingTarget = null;
        else nextAttemptTime = Environment.TickCount64 + 100;
    }
}
