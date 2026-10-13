using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PoseKit.Penumbra;

namespace PoseKit.Library;

/// <summary>One playable mod option in the quick library, flattened from the scan.</summary>
public sealed class LibraryEntry
{
    public required PoseModInfo Mod { get; init; }
    public required PoseModGroup Group { get; init; }
    public required PoseModOption Option { get; init; }

    /// Option, group and mod names, lower-cased, for search.
    public required string SearchText { get; init; }

    /// The names declare two or more roles. A display/filter hint only; never affects play.
    public required bool IsCouple { get; init; }

    /// A mod's always-active files have no option name worth showing.
    public string DisplayName => Option.Name is "" or "Default" ? Mod.ModName : Option.Name;

    /// Root menus this option is listed under. Set by CategoryResolver.
    public IReadOnlySet<string> Themes { get; set; } = new HashSet<string>();

    /// Submenus this option is listed under within its themes. Set by CategoryResolver.
    public IReadOnlySet<string> Acts { get; set; } = new HashSet<string>();

    /// Hidden by the player: listed only under Hidden.
    public bool Hidden { get; set; }

    private string? id;

    /// Stable across category edits, for ImGui IDs, so a row's popup stays with its option.
    public string Id => id ??= $"{Mod.ModDirectory}|{Group.Name}|{Option.Name}";

    private string? animationKey;

    /// What counts as one animation: the option group, whose options are its roles or variants, except
    /// where each option is an animation of its own (list groups, catalogue-numbered options).
    public string AnimationKey => animationKey ??=
        Categories.AutoCategories.IsSeparateAnimation(Group.Name, Option.Name) ? Id : $"{Mod.ModDirectory}|{Group.Name}";
}

public static class LibraryIndex
{
    // GroundSit/Sit/Doze, the pose-cycling emote modes (see PoseNameHeuristics).
    public const uint GroundSitMode = 1;
    public const uint ChairSitMode = 2;
    public const uint DozeMode = 3;

    public static bool IsGroundSit(PoseTriggerHint trigger) => trigger.PoseIdentifier?.EmoteModeId == GroundSitMode;
    public static bool IsChairSit(PoseTriggerHint trigger) => trigger.PoseIdentifier?.EmoteModeId == ChairSitMode;
    public static bool IsDoze(PoseTriggerHint trigger) => trigger.PoseIdentifier?.EmoteModeId == DozeMode;
    public static bool IsEmote(PoseTriggerHint trigger) => trigger.SlashCommand != null;

    /// Every option with at least one trigger, in scan order.
    public static List<LibraryEntry> Build(IEnumerable<PoseModInfo> mods)
    {
        var entries = new List<LibraryEntry>();
        foreach (var mod in mods)
        foreach (var group in mod.Groups)
        foreach (var option in group.Options)
        {
            if (option.Triggers.Count == 0) continue;
            entries.Add(new LibraryEntry
            {
                Mod = mod,
                Group = group,
                Option = option,
                SearchText = $"{option.Name}\n{group.Name}\n{mod.ModName}".ToLowerInvariant(),
                // A mod with only default files is named "Default"; its roles are in the mod's name.
                IsCouple = CoupleNames.IsCouple(group.Name, option.Name, group.IsImplicit ? mod.ModName : ""),
            });
        }
        return entries;
    }
}
