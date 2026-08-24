using System;
using System.Globalization;
using System.Linq;

namespace PoseKit.Sync;

/// <summary>
/// Bridges PoseKit's offset onto the local player through SimpleHeels' own "/heels temp set" chat
/// command, rather than SimpleHeels.RegisterPlayer IPC (tried first — dead end, see below).
///
/// GetLocalPlayer — the only method Mare/Snowcloak/Lightless/PlayerSync/etc. call to find out what to
/// upload about the local player — builds an IpcCharacterConfig that reads Plugin.TempOffsets[0]
/// directly (SimpleHeels-master/Plugin.cs's IpcCharacterConfig constructor). It does NOT consult
/// Plugin.IpcAssignedData, the dictionary RegisterPlayer writes into. So registering an offset via
/// IPC on object index 0 applies visually to the local player (TryGetCharacterConfig checks
/// IpcAssignedData first when deciding what to render) but is invisible to every sync tool, since
/// none of them read the array RegisterPlayer actually reached. Confirmed against SimpleHeels'
/// current live source (github.com/Caraxi/SimpleHeels) — not just this repo's vendored copy.
///
/// "/heels temp set" (SimpleHeels-master/Plugin.cs's "temp" command) writes directly to
/// TempOffsets[0] — the one GetLocalPlayer actually reads — so driving it via chat command (the same
/// trick PoseTrigger already uses for emotes/cpose) is indistinguishable from the user setting it
/// through SimpleHeels' own UI, which is confirmed to sync correctly.
///
/// Sign/unit conventions below are read directly off SimpleHeels' own command parser in "set" mode:
/// height/left/forward assign X/Y/Z directly (no inversion), rotate takes degrees and converts to
/// radians internally. All four are always sent together (not "add" mode) so a value left at 0 in
/// PoseKit's offset actually clears any stale value from a previous, different pose.
///
/// Mutually exclusive with OffsetEngine's own SetDrawOffset hook for the same reason as the abandoned
/// IPC approach: both would patch the same native function. Callers must leave OffsetEngine inactive
/// while bridging.
///
/// One real gap: SimpleHeels' command handler discards everything but height unless it detects the
/// local player is already in a looping emote (its own EmoteIdentifier.Get). PoseKit only calls this
/// once a pose is confirmed active (PoseTrigger.Tick already waits for the target CPoseState, or the
/// slash-command path fires immediately after issuing the emote command), so this should align in
/// practice, but a slash-command emote whose animation hasn't visually started yet by the time this
/// fires could still hit that gap.
/// </summary>
public sealed class SimpleHeelsBridge
{
    public bool IsLoaded => Plugin.PluginInterface.InstalledPlugins
        .Any(p => p is { IsLoaded: true, InternalName: "SimpleHeels" });

    public void Apply(PoseOffset offset)
    {
        if (!IsLoaded) return;

        var degrees = offset.Rotation * (180f / MathF.PI);
        var command = string.Create(CultureInfo.InvariantCulture,
            $"/heels temp set height {offset.Position.Y:0.####} left {offset.Position.X:0.####} forward {offset.Position.Z:0.####} rotate {degrees:0.####} silent");
        ChatCommand.Execute(command);
    }

    /// Safe to call unconditionally, whether or not anything was ever applied.
    public void Clear()
    {
        if (!IsLoaded) return;
        ChatCommand.Execute("/heels temp reset");
    }
}
