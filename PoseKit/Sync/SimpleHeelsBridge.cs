using System;
using System.Globalization;
using System.Linq;

namespace PoseKit.Sync;

/// <summary>
/// Applies PoseKit's offset through SimpleHeels' "/heels temp set" command, so the offset is shared
/// the same way a SimpleHeels temp offset is. Its IPC registration isn't used because offsets set
/// that way are not shared.
///
/// All four values are always sent so a zero clears a stale value from a previous pose. Rotation is
/// in degrees. OffsetEngine must stay inactive while bridging, since both patch the same function.
/// SimpleHeels ignores everything but height until the player is in a looping emote.
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
