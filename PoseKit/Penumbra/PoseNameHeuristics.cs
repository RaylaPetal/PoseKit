using System.Text.RegularExpressions;

namespace PoseKit.Penumbra;

/// <summary>
/// Maps a resolved .pap file path to a PoseIdentifier, porting the regex heuristics from
/// Synastry-main/EmoteLink/AnimationManifestScanner.cs (DetectPoseTargets) rather than hand-building
/// a static Lumina-sheet lookup table — this mapping is already proven against real pose mods.
/// Only GroundSit/Sit/Doze are mapped; PoseKit's PoseIdentifier has no meaningful representation
/// for a generic "idle" pose (PoseIdentifier.FromCharacter only resolves during an active
/// pose/emote loop), so that branch of the original heuristic is dropped.
/// </summary>
public static class PoseNameHeuristics
{
    private static readonly (uint EmoteModeId, string Pattern)[] Candidates =
    [
        (1, @"j_pose(\d+)"), // GroundSit
        (2, @"s_pose(\d+)"), // Sit
        (3, @"l_pose(\d+)"), // Doze
    ];

    public static PoseIdentifier? Identify(string filePath)
    {
        var path = filePath.Replace('\\', '/').ToLowerInvariant();

        foreach (var (emoteModeId, pattern) in Candidates)
        {
            var match = Regex.Match(path, pattern, RegexOptions.IgnoreCase);
            if (match.Success && byte.TryParse(match.Groups[1].Value, out var index) && index <= 6)
                return new PoseIdentifier(emoteModeId, index);
        }

        uint? folderEmoteModeId = path.Contains("/jmn/") ? 1u
            : path.Contains("/sit/") ? 2u
            : path.Contains("/doze/") ? 3u
            : null;

        return folderEmoteModeId is { } id ? new PoseIdentifier(id, 0) : null;
    }
}
