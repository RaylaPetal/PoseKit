using System;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using PoseKit.Presets;

namespace PoseKit;

/// <summary>
/// Triggers a saved or Penumbra-discovered pose so its offset can be applied once active.
/// Sit/GroundSit/Doze use the stable PlayerState.SelectedPoses + "/cpose" cycling path, mirroring
/// Synastry-main/EmoteLink/PoseService.cs and the ExecutePose/UpdatePoseCycling flow in
/// Synastry-main/EmoteLink/Plugin.cs. Any other pose falls back to a literal "/emotename motion"
/// chat command (per the design doc's stated fallback) — deliberately not porting Synastry's
/// AOB-hooked AnywherePoseService/ActionTimelinePlayback, which bypass emote-unlock/server checks.
/// </summary>
public sealed unsafe class PoseTrigger(OffsetEngine offsetEngine)
{
    private NamedPose? cyclingTarget;
    private int attempts;
    private long nextAttemptTime;

    public void Trigger(NamedPose pose)
    {
        switch (pose.Pose.EmoteModeId)
        {
            case 1: EnterPoseCycle(pose, EmoteController.PoseType.GroundSit, "/groundsit"); break;
            case 2: EnterPoseCycle(pose, EmoteController.PoseType.Sit, "/sit"); break;
            case 3: EnterPoseCycle(pose, EmoteController.PoseType.Doze, "/doze"); break;
            default:
                ExecuteCommand($"/{pose.Pose.EmoteName.ToLowerInvariant().Replace(" ", "")} motion");
                cyclingTarget = null;
                offsetEngine.DesiredOffset = pose.Offset;
                offsetEngine.Active = true;
                break;
        }
    }

    private void EnterPoseCycle(NamedPose pose, EmoteController.PoseType poseType, string enterCommand)
    {
        var currentPose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        var alreadyInThatEmote = currentPose is { } c && c.EmoteModeId == pose.Pose.EmoteModeId;

        if (!alreadyInThatEmote)
        {
            var state = PlayerState.Instance();
            if (state != null) state->SelectedPoses[(int)poseType] = pose.Pose.CPoseState;
            ExecuteCommand(enterCommand);
        }

        cyclingTarget = pose;
        attempts = 0;
        nextAttemptTime = Environment.TickCount64 + (alreadyInThatEmote ? 0 : 500);
    }

    /// Called every framework tick from Plugin; steps "/cpose" until the target CPoseState is
    /// reached, then hands the offset to OffsetEngine.
    public void Tick()
    {
        if (cyclingTarget == null || Environment.TickCount64 < nextAttemptTime) return;

        var current = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        if (current is not { } c || c.EmoteModeId != cyclingTarget.Pose.EmoteModeId)
        {
            if (++attempts >= 8) cyclingTarget = null;
            else nextAttemptTime = Environment.TickCount64 + 100;
            return;
        }

        if (c.CPoseState == cyclingTarget.Pose.CPoseState)
        {
            offsetEngine.DesiredOffset = cyclingTarget.Offset;
            offsetEngine.Active = true;
            cyclingTarget = null;
            return;
        }

        ExecuteCommand("/cpose");
        if (++attempts >= 8) cyclingTarget = null;
        else nextAttemptTime = Environment.TickCount64 + 100;
    }

    private static void ExecuteCommand(string command)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        using var text = new Utf8String(command);
        ui->ProcessChatBoxEntry(&text);
    }
}
