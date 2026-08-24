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
///    scoped to a single option's Files dict rather than a whole-mod recursive filesystem scan. Also
///    catches "jmn.pap" directly, which several races use as GroundSit's shared base-pose file instead
///    of a numbered j_poseNN variant — confirmed against real installed mods (e.g. GoonersLife+v3's
///    "Idle Animation Pack" group), where every option redirecting it is labelled GroundSit's first
///    stage ("GroundSit0"/"GroundSit1"/"Gsit0" depending on the author). Synastry's own equivalent
///    fallback only matches a "/jmn/" *directory* segment, so it misses this exact file too.
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

            // jmn.pap is also the exact file Lumina's own Emote sheet reverse-maps to the "/groundsit"
            // command (it's what plays when you type /groundsit fresh — CPoseState 0). Without this
            // guard, the EmoteAnimationIndex lookup below independently rediscovers the same file as a
            // *command* hit, so the option ends up offering both "Sit on Ground Pose 1" and "/groundsit"
            // as separate buttons for what is actually one and the same trigger.
            var isGroundSitBasePose = normalized.EndsWith("/jmn.pap", System.StringComparison.OrdinalIgnoreCase);
            if (isGroundSitBasePose)
                AddPose(new PoseIdentifier(1, 0)); // GroundSit's shared base-pose file — see class doc

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
