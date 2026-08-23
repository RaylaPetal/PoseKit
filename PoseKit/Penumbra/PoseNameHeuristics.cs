using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PoseKit.Penumbra;

/// <summary>Either a direct slash-emote command to trigger, or a sit/groundsit/doze PoseIdentifier to
/// cycle to — never both.</summary>
public readonly record struct PoseTriggerHint(string? SlashCommand, PoseIdentifier? PoseIdentifier);

/// <summary>
/// Detects every way to trigger a single Penumbra mod option's animation(s) — an option can bind more
/// than one real game emote at once (e.g. a two-person combined animation redirecting both /confirm's
/// and /shiver's files), so this returns every distinct trigger found rather than the first match.
///
/// Three sources, in order:
/// 1. An explicit "(/command)" hint some mod authors put directly in the option or group name
///    (e.g. "Buttslap - Hard (/highfive)", confirmed present in real GoonersLife+v3 group names).
/// 2. Known sit/groundsit/doze filename patterns (j_pose/s_pose/l_pose) in the option's own redirected
///    game paths — ported from Synastry-main/EmoteLink/AnimationManifestScanner.cs's DetectPoseTargets,
///    scoped to a single option's Files dict rather than a whole-mod recursive filesystem scan.
/// 3. EmoteAnimationIndex — a reverse lookup from Lumina's own Emote/ActionTimeline sheets, catching
///    plain one-shot emotes (e.g. /confirm, /shiver, /highfive) that neither of the above catch.
/// </summary>
public static class PoseNameHeuristics
{
    private static readonly Regex SlashCommandHint = new(@"\(/([a-zA-Z]+)\)", RegexOptions.Compiled);

    private static readonly (uint EmoteModeId, Regex Pattern)[] FilePatterns =
    [
        (1, new Regex(@"j_pose(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)), // GroundSit
        (2, new Regex(@"s_pose(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)), // Sit
        (3, new Regex(@"l_pose(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)), // Doze
    ];

    /// <param name="groupName">Checked for the "(/command)" hint too — some mods (e.g. simple
    /// single-option "Enable" toggles) put it on the group's own name rather than the option's.</param>
    public static List<PoseTriggerHint> Detect(string groupName, string optionName, IEnumerable<string> gamePaths)
    {
        var hits = new List<PoseTriggerHint>();
        var seenCommands = new HashSet<string>();
        var seenPoses = new HashSet<PoseIdentifier>();

        void AddCommand(string command)
        {
            if (seenCommands.Add(command)) hits.Add(new PoseTriggerHint(command, null));
        }

        void AddPose(PoseIdentifier pose)
        {
            if (seenPoses.Add(pose)) hits.Add(new PoseTriggerHint(null, pose));
        }

        var nameMatch = SlashCommandHint.Match(optionName);
        if (!nameMatch.Success) nameMatch = SlashCommandHint.Match(groupName);
        if (nameMatch.Success) AddCommand(nameMatch.Groups[1].Value);

        foreach (var path in gamePaths)
        {
            var normalized = path.Replace('\\', '/');

            foreach (var (emoteModeId, pattern) in FilePatterns)
            {
                var match = pattern.Match(normalized);
                if (match.Success && byte.TryParse(match.Groups[1].Value, out var index) && index <= 6)
                    AddPose(new PoseIdentifier(emoteModeId, index));
            }

            if (normalized.EndsWith(".pap", System.StringComparison.OrdinalIgnoreCase))
            {
                var withoutExtension = normalized[..^4];
                if (EmoteAnimationIndex.LookupCommand(withoutExtension) is { } command)
                    AddCommand(command);
            }
        }

        return hits;
    }
}
