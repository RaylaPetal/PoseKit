using System;
using System.Collections.Generic;
using System.Linq;
using PoseKit.Penumbra;

namespace PoseKit.Library;

/// <summary>
/// Checks that the files of a played animation are the ones Penumbra serves, so a loss is reported
/// instead of the wrong animation silently playing. Settings and redraws apply asynchronously, so it
/// waits first, and only the latest play is checked. When none of the files are served, the settings
/// are re-sent once and checked again before anything is reported.
/// </summary>
public sealed class PlayVerifier(PenumbraIpc ipc)
{
    private const long DelayMs = 1500;

    /// Reapply is null once it has been used.
    private (PoseModInfo Mod, PoseModOption Option, long At, Action? Reapply)? pending;

    /// <param name="reapply">Re-sends the option's settings, tried once if none of its files are served.</param>
    public void Queue(PoseModInfo mod, PoseModOption option, Action? reapply = null)
    {
        if (option.AnimationRedirects.Count > 0)
            pending = (mod, option, Environment.TickCount64 + DelayMs, reapply);
    }

    public void Tick()
    {
        if (pending is not { } play || Environment.TickCount64 < play.At) return;
        pending = null;

        if (ipc.TryGetModDirectory() is not { } root) return;
        var paths = play.Option.AnimationRedirects.Keys.ToArray();
        if (ipc.TryResolvePlayerPaths(paths) is not { } actual || actual.Length != paths.Length) return;

        var resolved = new Dictionary<string, string>(paths.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < paths.Length; i++) resolved[paths[i]] = actual[i];

        var losses = ExclusivePlay.Losses(root, play.Mod.ModDirectory, play.Option.AnimationRedirects, resolved);
        if (losses.Count == 0) return;

        // Every file still the game's own means the settings never took, not that another mod won.
        if (losses.Count == paths.Length && losses.All(loss => loss.Owner == null) && play.Reapply is { } reapply)
        {
            Plugin.Log.Debug($"[PoseKit] None of {play.Option.Name}'s files are served; re-sending its settings.");
            reapply();
            pending = (play.Mod, play.Option, Environment.TickCount64 + DelayMs, null);
            return;
        }

        var name = play.Option.Name is "" or "Default" ? play.Mod.ModName : SceneRoles.OfferTitle(play.Option.Name);
        var owners = losses.Select(loss => loss.Owner).Where(owner => owner != null).Distinct().ToList();
        Plugin.Log.Debug($"[PoseKit] \"{name}\" lost {losses.Count} of {paths.Length} files: {string.Join(", ", losses.Take(5).Select(loss => $"{loss.GamePath} to {loss.Owner ?? "the game"}"))}");
        Plugin.ChatGui.PrintError(owners.Count > 0
            ? $"[PoseKit] \"{name}\" may not play correctly: {losses.Count} of its {paths.Length} animation files come from {string.Join(", ", owners.Take(3))}."
            : play.Reapply == null
                ? $"[PoseKit] \"{name}\" may not play correctly: Penumbra still isn't using its animation files after PoseKit re-applied its settings. Another plugin may be resetting this mod's settings."
                : $"[PoseKit] \"{name}\" may not play correctly: {losses.Count} of its {paths.Length} animation files aren't being used yet. Try playing it again.");
    }
}
