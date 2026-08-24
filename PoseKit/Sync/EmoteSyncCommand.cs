using System;
using System.Globalization;
using System.Threading;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace PoseKit.Sync;

/// Resets the currently playing emote loop timer for every nearby rendered player character.
/// This is still client-side only: it changes how those actors are animated on this client and
/// does not send a synchronization request to the other players.
public sealed unsafe class EmoteSyncCommand : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        cancellationTokenSource.Dispose();
    }

    /// Parses "" or "delay &lt;seconds&gt;" (as split, whitespace-separated args) and triggers a sync.
    /// Returns an error message on invalid syntax, or null on success.
    public string? HandleArgs(string[] splitArgs)
    {
        var delay = 0f;
        for (var i = 0; i < splitArgs.Length; i++)
        {
            if (!string.Equals(splitArgs[i], "delay", StringComparison.OrdinalIgnoreCase))
                return $"Invalid argument: {splitArgs[i]}";

            if (splitArgs.Length < i + 2 || !float.TryParse(splitArgs[i + 1], CultureInfo.InvariantCulture, out delay))
                return "Invalid argument syntax: delay <seconds>";

            i++;
        }

        Sync(delay);
        return null;
    }

    public void Sync(float delaySeconds = 0)
    {
        if (delaySeconds <= 0)
        {
            Plugin.Framework.RunOnFrameworkThread(ResetNearbyPlayerEmoteLoops);
        }
        else
        {
            Plugin.Framework.RunOnTick(ResetNearbyPlayerEmoteLoops, TimeSpan.FromSeconds(delaySeconds),
                cancellationToken: cancellationTokenSource.Token);
        }
    }

    private static void ResetNearbyPlayerEmoteLoops()
    {
        foreach (var gameObject in Plugin.ObjectTable)
        {
            if (gameObject is IPlayerCharacter player)
                ResetEmoteLoop(player);
        }
    }

    private static void ResetEmoteLoop(IPlayerCharacter player)
    {
        var character = (Character*)player.Address;
        if (character == null) return;
        if (character->DrawObject == null) return;
        if (character->DrawObject->GetObjectType() != ObjectType.CharacterBase) return;
        if (((CharacterBase*)character->DrawObject)->GetModelType() != CharacterBase.ModelType.Human) return;

        var human = (Human*)character->DrawObject;
        var skeleton = human->Skeleton;
        if (skeleton == null) return;

        for (var i = 0; i < skeleton->PartialSkeletonCount && i < 1; ++i)
        {
            var partialSkeleton = &skeleton->PartialSkeletons[i];
            var animatedSkeleton = partialSkeleton->GetHavokAnimatedSkeleton(0);
            if (animatedSkeleton == null) continue;

            for (var animControl = 0; animControl < animatedSkeleton->AnimationControls.Length && animControl < 1; ++animControl)
            {
                var control = animatedSkeleton->AnimationControls[animControl].Value;
                if (control == null) continue;
                control->hkaAnimationControl.LocalTime = 0f;
            }
        }
    }
}
