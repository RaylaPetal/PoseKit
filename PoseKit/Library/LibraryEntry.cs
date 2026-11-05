using System.Collections.Generic;
using System.Linq;
using PoseKit.Penumbra;

namespace PoseKit.Library;

public enum PairingSource { Default, ReliableWords, Name, IdenticalAnimation, Author, ActWords, Alignment, Player }

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

    /// What this option's own description declares; null when it declares nothing.
    public required CouplePairing? DeclaredState { get; init; }

    /// A literal label in its own name, such as "FxF".
    public required CouplePairing? LiteralState { get; init; }

    /// What the words in its names say, so that it always has a tag.
    public required WordReading OwnWords { get; init; }

    /// The best of its own words and those on identical animations.
    public WordReading Words { get; private set; }

    /// The two roles its name states, when it states them clearly.
    public required Scene? Scene { get; init; }

    /// Its animation's identity, once hashed. See AnimationHasher.
    public string? AnimationKey { get; set; }

    public CouplePairing Pairing { get; private set; }

    /// The state's source, for the tag's tooltip.
    public PairingSource PairingSource { get; private set; }

    /// Nothing but a hint from its name decided the state.
    public bool PairingIsDefault => PairingSource == PairingSource.Default;

    /// The player's tag wins, then what aligning found, words naming the act, the role code, an
    /// identical animation's code, a literal label, other reliable words and finally whatever its name hints at. Words
    /// can't overrule an author's solo mark: a one-person pose can still be called "Bj Tease".
    public void ApplySources(CouplePairing? player, CouplePairing? contact, CouplePairing? inherited, WordReading? sharedWords = null)
    {
        Words = sharedWords is { } shared && shared.Confidence > OwnWords.Confidence ? shared : OwnWords;
        (Pairing, PairingSource) =
            player is { } set ? (set, PairingSource.Player)
            : contact is { } aligned ? (aligned, PairingSource.Alignment)
            : Words.Confidence == WordConfidence.Strong && DeclaredState != CouplePairing.Solo ? (Words.State, PairingSource.ActWords)
            : DeclaredState is { } declared ? (declared, PairingSource.Author)
            : inherited is { } identical ? (identical, PairingSource.IdenticalAnimation)
            : LiteralState is { } literal ? (literal, PairingSource.Name)
            : Words.Confidence == WordConfidence.Reliable ? (Words.State, PairingSource.ReliableWords)
            : (Words.State, PairingSource.Default);
    }

    /// A mod's always-active files have no option name worth showing.
    public string DisplayName => Option.Name is "" or "Default" ? Mod.ModName : Option.Name;

    private string? id;

    /// For ImGui IDs, so a row keeps its state when the list reorders.
    public string Id => id ??= $"{Mod.ModDirectory}|{Group.Name}|{Option.Name}";
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
        {
            var options = group.Options.Where(option => option.Triggers.Count > 0).ToList();
            var couples = options.Select(option =>
                CoupleNames.IsCouple(group.Name, option.Name, group.IsImplicit ? mod.ModName : "")).ToList();
            // A mod with only default files is named "Default"; everything is in the mod's name.
            var words = NameGuess.UnifyGroup(group.Name, options.Select((option, i) =>
                NameGuess.Read(group.IsImplicit ? mod.ModName : option.Name, group.Name, mod.ModName, couples[i])).ToList());

            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var entry = new LibraryEntry
                {
                    Mod = mod,
                    Group = group,
                    Option = option,
                    SearchText = $"{option.Name}\n{group.Name}\n{mod.ModName}".ToLowerInvariant(),
                    IsCouple = couples[i],
                    DeclaredState = DeclaredTags.FromDescription(option.Description),
                    LiteralState = DeclaredTags.FromName(group.IsImplicit ? mod.ModName : option.Name),
                    OwnWords = words[i],
                    Scene = SceneRoles.TryParse(option.Name, group.Name, mod.ModName, option.Triggers),
                };
                entry.ApplySources(null, null, null);
                entries.Add(entry);
            }
        }
        return entries;
    }
}
