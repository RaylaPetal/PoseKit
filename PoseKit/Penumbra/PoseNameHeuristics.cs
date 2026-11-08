using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PoseKit.Penumbra;

/// <summary>Either a direct slash-emote command to trigger, or a sit/groundsit/doze/standing-idle
/// PoseIdentifier to cycle to — never both.</summary>
public readonly record struct PoseTriggerHint(string? SlashCommand, PoseIdentifier? PoseIdentifier);

/// <summary>
/// Finds every way to trigger a mod option's animations (one option can replace several emotes).
///
/// Sources, in order:
/// 1. A "(/command)" hint in the option or group name.
/// 2. Sit/groundsit/doze file patterns (j_pose/s_pose/l_pose, plus jmn/sit/bed_liedown_loop for pose 0).
/// 3. Standing idles (emote/poseNN, plus resident/idle for idle 0), played as pose mode 0.
/// 4. A reverse lookup of the emote sheet for plain emotes.
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

    /// Pose 0 of each pose-cycling emote isn't a numbered file; the game uses these instead.
    private static readonly (string Suffix, PoseIdentifier Pose)[] BasePoseFiles =
    [
        ("/emote/jmn.pap", new PoseIdentifier(1, 0)),              // GroundSit
        ("/emote/sit.pap", new PoseIdentifier(2, 0)),              // Sit
        ("/emote/bed_liedown_loop.pap", new PoseIdentifier(3, 0)), // Doze
        ("/bt_common/resident/idle.pap", new PoseIdentifier(0, 0)), // Standing idle 0
    ];

    // Standing idles live under bt_common; weapon-stance folders hold battle idles, which /cpose doesn't
    // reach. The leading "/" keeps j_pose/s_pose/l_pose out.
    private static readonly Regex StandingIdleFile =
        new(@"/bt_common/emote/pose0([1-6])_(?:loop|start)\.pap$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

            // Pose 0 files skip the emote lookup, which would add a generic "/groundsit" or "/lounge"
            // button that plays whichever pose the character last used.
            var isBasePose = false;
            foreach (var (suffix, pose) in BasePoseFiles)
            {
                if (!normalized.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)) continue;
                AddPose(pose);
                isBasePose = true;
            }

            // Idle files skip the emote lookup too: the idle is only reachable through /cpose.
            if (StandingIdleFile.Match(normalized) is { Success: true } idle)
            {
                AddPose(new PoseIdentifier(0, byte.Parse(idle.Groups[1].Value)));
                isBasePose = true;
            }

            if (!isBasePose && normalized.EndsWith(".pap", System.StringComparison.OrdinalIgnoreCase))
            {
                var withoutExtension = normalized[..^4];
                if (EmoteAnimationIndex.LookupCommand(withoutExtension) is { } command)
                    AddCommand(command);
            }
        }

        return hits;
    }
}
