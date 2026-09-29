using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PoseKit.Penumbra;

/// <summary>Either a direct slash-emote command to trigger, or a sit/groundsit/doze PoseIdentifier to
/// cycle to — never both.</summary>
public readonly record struct PoseTriggerHint(string? SlashCommand, PoseIdentifier? PoseIdentifier);

/// <summary>
/// Finds every way to trigger a mod option's animations (one option can replace several emotes).
///
/// Sources, in order:
/// 1. A "(/command)" hint in the option or group name.
/// 2. Sit/groundsit/doze file patterns (j_pose/s_pose/l_pose, and jmn.pap for the groundsit base pose).
/// 3. A reverse lookup of the emote sheet for plain emotes.
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

    /// <param name="groupName">Also checked for the "(/command)" hint.</param>
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
                // The file number is the CPoseState itself, not 1-based.
                if (match.Success && byte.TryParse(match.Groups[1].Value, out var index) && index <= 6)
                    AddPose(new PoseIdentifier(emoteModeId, index));
            }

            // jmn.pap is groundsit pose 0. Skip the emote lookup for it, which would add a duplicate
            // "/groundsit" button.
            var isGroundSitBasePose = normalized.EndsWith("/jmn.pap", System.StringComparison.OrdinalIgnoreCase);
            if (isGroundSitBasePose)
                AddPose(new PoseIdentifier(1, 0));

            if (!isGroundSitBasePose && normalized.EndsWith(".pap", System.StringComparison.OrdinalIgnoreCase))
            {
                var withoutExtension = normalized[..^4];
                if (EmoteAnimationIndex.LookupCommand(withoutExtension) is { } command)
                    AddCommand(command);
            }
        }

        return hits;
    }
}
