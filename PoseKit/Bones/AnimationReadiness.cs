using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace PoseKit.Bones;

/// <summary>
/// Tells when a character is actually playing its animation's loop, not still getting into it.
/// Entering a pose or emote flips the game's pose state straight away, but the character first plays
/// an intro (sitting down, kneeling) and may be redrawn when a mod's files are applied: by Penumbra
/// on this side, by Mare/Snowcloak for the partner. Aligning during either measures the wrong body
/// position.
///
/// Reads the base slot of the character's animation timeline and names it through the ActionTimeline
/// sheet (e.g. "emote/j_pose03_loop"). Ready means that timeline is not an intro or outro
/// ("start"/"end" in its name), and neither it nor the drawn model has changed for a short while:
/// longer for a timeline that isn't named as a loop, since an unfamiliar one might still be a
/// transition. Penumbra replaces animation files, not timeline names, so this works for modded
/// animations too.
/// </summary>
public sealed unsafe class AnimationReadiness
{
    // How long a loop timeline and the drawn model must hold before measuring.
    private const long LoopSettleMs = 600;

    // The same for a timeline not named as a loop.
    private const long OtherSettleMs = 1500;

    private static Dictionary<uint, string>? timelineKeys;

    private readonly Dictionary<ulong, (uint Timeline, nint DrawObject, long Since)> seen = new();

    /// True once <paramref name="character"/>'s current animation has settled into its loop.
    public bool IsReady(IPlayerCharacter character)
    {
        var native = (Character*)character.Address;
        if (native == null || native->DrawObject == null) return false;

        var timeline = (uint)native->Timeline.TimelineSequencer.TimelineIds[0];
        var drawObject = (nint)native->DrawObject;
        var now = Environment.TickCount64;

        if (!seen.TryGetValue(character.GameObjectId, out var last) || last.Timeline != timeline || last.DrawObject != drawObject)
        {
            seen[character.GameObjectId] = (timeline, drawObject, now);
            return false;
        }

        var key = TimelineKey(timeline);
        var name = key[(key.LastIndexOf('/') + 1)..];
        if (name.Contains("_start", StringComparison.OrdinalIgnoreCase) || name.Contains("_end", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("start", StringComparison.OrdinalIgnoreCase) || name.StartsWith("end", StringComparison.OrdinalIgnoreCase))
            return false;

        var settle = key.EndsWith("_loop", StringComparison.OrdinalIgnoreCase) ? LoopSettleMs : OtherSettleMs;
        return now - last.Since >= settle;
    }

    /// Forgets every character, so the next play starts measuring stability afresh.
    public void Reset() => seen.Clear();

    /// Every non-empty timeline slot with its name, for diagnostics ("/posekit bones") — slot 0 is the
    /// one IsReady reads.
    public static string DescribeTimelines(IPlayerCharacter character)
    {
        var native = (Character*)character.Address;
        if (native == null) return "none";
        var ids = native->Timeline.TimelineSequencer.TimelineIds;
        var parts = new List<string>();
        for (var slot = 0; slot < ids.Length; slot++)
        {
            if (ids[slot] != 0)
                parts.Add($"slot {slot}: {ids[slot]} \"{TimelineKey(ids[slot])}\"");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }

    private static string TimelineKey(uint timeline)
    {
        if (timelineKeys == null)
        {
            timelineKeys = new Dictionary<uint, string>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<ActionTimeline>())
                timelineKeys[row.RowId] = row.Key.ExtractText();
        }
        return timelineKeys.GetValueOrDefault(timeline, "");
    }
}
