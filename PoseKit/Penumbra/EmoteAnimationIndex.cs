using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace PoseKit.Penumbra;

/// <summary>
/// Reverse-maps a redirected game animation path to the slash command that naturally plays it, by
/// indexing Lumina's Emote sheet (Emote.TextCommand -> Emote.ActionTimeline[].Key). Confirmed against
/// real game data: Emote "Confirm" (/confirm) -> ActionTimeline Key "emote/loop_emot20_loop"; "Shiver"
/// (/shiver) -> "emote/loop_emot18_loop"; "High Five" (/highfive) -> "emote/act_emot22" — each Key's
/// last path segment matches the .pap basename Penumbra mods redirect (e.g.
/// ".../bt_common/emote/act_emot22.pap"), so this is safe to key on for a fast reverse lookup.
/// </summary>
public static class EmoteAnimationIndex
{
    private static Dictionary<string, List<(string Key, string Command)>>? cache;

    private static Dictionary<string, List<(string, string)>> Build()
    {
        var dict = new Dictionary<string, List<(string, string)>>();
        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            var commandRow = emote.TextCommand;
            if (!commandRow.IsValid) continue;

            var command = commandRow.Value.Command.ExtractText().TrimStart('/');
            if (command.Length == 0) continue;

            foreach (var timelineRef in emote.ActionTimeline)
            {
                if (!timelineRef.IsValid) continue;

                var key = timelineRef.Value.Key.ExtractText();
                if (key.Length == 0) continue;

                var basename = key[(key.LastIndexOf('/') + 1)..];
                if (!dict.TryGetValue(basename, out var list))
                    dict[basename] = list = [];
                list.Add((key, command));
            }
        }

        return dict;
    }

    /// <param name="gamePathWithoutExtension">e.g. "chara/human/c0101/animation/a0001/bt_common/emote/act_emot22"</param>
    public static string? LookupCommand(string gamePathWithoutExtension)
    {
        cache ??= Build();

        var basename = gamePathWithoutExtension[(gamePathWithoutExtension.LastIndexOf('/') + 1)..];
        if (!cache.TryGetValue(basename, out var candidates)) return null;

        foreach (var (key, command) in candidates)
            if (gamePathWithoutExtension.EndsWith(key, System.StringComparison.OrdinalIgnoreCase))
                return command;

        return null;
    }
}
