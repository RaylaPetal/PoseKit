using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PoseKit.Library;

/// <summary>How far an animation's words can be trusted, which decides their rank against other evidence.</summary>
public enum WordConfidence
{
    /// Nothing tells, so the default applies: a couple scene is MxF unless something says otherwise.
    None,

    /// Words that only hint, or a list group's name.
    Weak,

    /// Words that are reliable where nothing better exists, such as a sex position or an idle. These
    /// fill gaps but don't overrule an author's code.
    Reliable,

    /// Words that name the act itself. "Throat Fuck" involves a penis whatever a code says, so these
    /// outrank author codes.
    Strong,
}

public readonly record struct WordReading(CouplePairing State, WordConfidence Confidence);

/// <summary>
/// Reads an animation's pairing from the words in its names, so that every animation has a tag. The
/// rule is simple: a couple scene is MxF unless its name shows no penis is needed, since most are
/// penetration under names that don't say so ("Hard Dog", "MinMaxing"). Only a named act such as feet,
/// fisting or kissing makes one M/F x F, and acts that exclude a penis (scissoring) are FxF. Word lists
/// come from the removed automatic categories.
/// </summary>
public static class NameGuess
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Credits and trigger emotes, which can read as acts ("Dom-Tdance", "(BreathControl)").
    private static readonly Regex Noise = new(@"\[[^\]]*\]|\([^)]*\)|^\s*\d+[.)]?\s", RegexOptions.Compiled);

    // A blowjob on one of these has no penis in it ("Forced Dildo BJ").
    private static readonly Regex PhallicToy = new(@"dildo|strap.?on", Options);

    private static readonly Regex PenisAct = new(
        @"blow ?job|\bbj\b|hand ?job|foot[- ]?job|thigh ?job|tit ?(job|fuck)|titty|boob ?job|paizuri|(deep ?)?throat(ed)?\b|throat ?fuck|face ?fuck|" +
        @"\bcock|\bdick|\bpenis|\bjerk|\bcbt\b|reach ?around|sword ?fight|chest ?grind|\bdp\b|double pen|\bwhore\b|\bcum\b|cum ?pump|frott|ball ?(kick|torture|bust)|cum ?shot|creampie|bukkake|spitroast|\broe ?job|giving head|" +
        @"on your knees|\bwhistle\b", Options);

    private static readonly Regex NoPenisAct = new(
        @"lesbian|sesbian|yuri|sapphic|girl ?on ?girl|scissor|\btrib\b|\w*\btrib\b|cunni|lingus|kitty ?lick|licking kitty|pussy ?(lick|eat)|" +
        @"eat(s|ing|en)? (out|pussy)|eaten out|dom eats\b", Options);

    private static readonly Regex Penetration = new(
        @"missionary|cowgirl|cowboy|dogg(y|ie)|\bfuck|\bsex\b|\bbreed|mating|nelson|\bpound|\bslam|pile ?driver|riding|\bride\b|\banal\b|" +
        @"plap|thrust|penetrat|congress|cow ?rid|\bfilled|\bwall bang|ramming|railing|vaginal|\bpinned", Options);

    // A man in the giving role of a sized option: "Giver (Hum): Big M", "Big M Giving".
    private static readonly Regex MaleGiver = new(
        @"\b(giv(er|ing)|\[?top\]?)\b.*\b(big|tall|standard|short|mid)\s+m\b|\b(big|tall|standard|short|mid)\s+m\b.*\bgiv(er|ing)\b", Options);

    // Packs mark creature scenes "[Sub&Horse]", and the creature's penis is the point of most.
    private static readonly Regex CreatureTag = new(
        @"\[sub&(dog|horse|monster|mimic|amalj|carbuncle|tentacle|pillory)", Options);

    // Signs, fixes and sound or voice packs: what's written on a sign says nothing about the animation.
    private static readonly Regex PropGroup = new(
        @"signboard|parasol|face fix|expressions?\b|character voice|thinking thoughts|music", Options);

    // Role options that say outright, in brackets that otherwise hold credits.
    private static readonly Regex PleasingPenis = new(@"partner'?s penis", Options);
    private static readonly Regex PleasingPussy = new(@"partner'?s pussy", Options);

    // Lists of sex scenes, whose entries are couples even without a role marker.
    private static readonly Regex SexGroup = new(
        @"nsfw-(standing|sitting|laying|threesome|hroth)|(standing|sitting|laying|threesome) animation list|hrothnroe", Options);

    // Lists where a couple's unnamed scene is as likely to be play as sex, so it isn't assumed MxF.
    private static readonly Regex NoPenisAssumed = new(@"cuddl|snuggl|teas|bdsm|kink|masturbat", Options);

    private static readonly Regex FaceRiding = new(@"face ?(sit|rid)", Options);

    private static readonly Regex Tease = new(@"\bteas", Options);

    private static readonly Regex HugRoles = new(@"\bhugg(er|ed)\b", Options);

    private static readonly Regex IdleGroup = new(@"\bidle|party pack|dirty.?danc", Options);

    // A role named in the name itself, which credits such as "[Avo&Ashara]" can't fake.
    private static readonly Regex NamedRole = new(@"\b(f?dom|sub|top|bot|hugg(er|ed))\d*\s*-", Options);

    // Two emotes for one animation: "[Gsit1_2]", "[Doze1_2]".
    private static readonly Regex PairedEmotes = new(@"\[[a-z]+\d_\d\]", Options);

    // One person bound to something, with no partner in the animation.
    private static readonly Regex BoundAlone = new(
        @"struggl|shackl|\btied\b|hogtie|frogtie|\bbound\b|bondage|pillory|gibbet|suspension|\bchained\b|cuffed|latex|\bprisoner\b|wooden horse|" +
        @"castigation|squatting pole|spread posts|signpost|scavenger|\bhooked\b|wall shackles|\bcross\b|brainwash|glory wall", Options);

    // Penetration by one of these has no penis in it.
    private static readonly Regex Device = new(@"dildo|strap.?on|\btoys?\b|machine|wooden horse|hitachi|vibrat", Options);

    // Roles named in the option, which make a couple even when no marker says so.
    private static readonly Regex RoleWords = new(
        @"\b(giv(er|ing)|receiv(er|ing)|hugg(er|ed)|finger(er|ed)|\[?top\]?|\[?bottom\]?|left chara|right chara)\b", Options);

    private static readonly Regex AnyCouple = new(
        @"cuddl|snuggl|(?<![a-z])hug|\bkiss|make ?out|making out|embrac|spoon|glomp|kabedon|\bfist|finger|paddl|spank|\btoys?\b|\bdildo|strap.?on|" +
        @"\btie (you|her|him|them|me) up\b|leash|\bchok|grind|grope|\b69\b|curl(s|ed)? up|belly rub|eat(s|ing)? ass|\brim(ming|job)?\b|face ?(sit|rid)|mage hand|human seat|foot ?(worship|work)|feets? (in|on|worship|licker|haver)|massag", Options);

    // Groups collecting many animations ("Oral Animation List* 285 - 307", "Nsfw-Oral"), whose names
    // describe the list. Any other group or mod name is the name of the animation itself.
    private static readonly Regex ListGroup = new(@"\blist\b|cuddlensleep|^nsfw-|\bpack\b|~|\banimations?\b|miscellaneous", Options);

    // More than two people: an act-free name can't say who's doing what, and a penis is the norm.
    private static readonly Regex Group = new(@"threesome|3 ?sum|4 ?some|foursome|gang ?bang|orgy|\bdom&sub-[3-9]", Options);

    private static readonly Regex SoloTag = new(@"\[(solo|[a-z]+[-_]solo|solo[-_][a-z]+|hands?|hand/toy|toys?|p\+hands)\]", Options);

    private static readonly Regex SoloWords = new(
        @"\bidle|\bdanc(e|ing)\b|\bsolo\b|\bteas|masturbat|\bpose ?pack|\bwalk\b|\bstance\b|\btwerk|\bsignboard|\bweapon skills?|\bexpression\b|\bteleport\b", Options);

    /// <param name="animation">The option's name, or the mod's for one with only default files.</param>
    /// <param name="group">The option group's name.</param>
    /// <param name="mod">The mod's name.</param>
    /// <param name="namedAsCouple">Its name declares two or more roles.</param>
    public static WordReading Read(string animation, string group, string mod, bool namedAsCouple)
    {
        if (PropGroup.IsMatch(group)) return new WordReading(CouplePairing.Solo, WordConfidence.Strong);

        // Packs tag one-person versions of couple acts: "[Solo-Sub] Double Handjob Cowgirl".
        if (SoloTag.IsMatch(animation)) return new WordReading(CouplePairing.Solo, WordConfidence.Strong);
        if (CreatureTag.IsMatch(animation)) return new WordReading(CouplePairing.MxF, WordConfidence.Reliable);
        if (MaleGiver.IsMatch(animation) || PleasingPenis.IsMatch(animation)) return new WordReading(CouplePairing.MxF, WordConfidence.Strong);
        if (PleasingPussy.IsMatch(animation)) return new WordReading(CouplePairing.FxF, WordConfidence.Strong);
        if (IdleGroup.IsMatch(group) && !NamedRole.IsMatch(animation)) return new WordReading(CouplePairing.Solo, WordConfidence.Reliable);

        var couple = namedAsCouple || RoleWords.IsMatch(animation) || PairedEmotes.IsMatch(animation) || SexGroup.IsMatch(group);
        var own = Noise.Replace(animation, " ");
        if (Named(own, couple) is { } fromOwn) return fromOwn;

        // A group or mod that isn't a list is this animation's own name, with its options as roles.
        var groupIsList = ListGroup.IsMatch(group);
        var groupText = Noise.Replace(group, " ");
        if (!groupIsList && Named(groupText, couple) is { } fromGroup) return fromGroup;
        var modText = Noise.Replace(mod, " ");
        if (Named(modText, couple) is { } fromMod) return fromMod;

        if (AnyCouple.IsMatch(own) || HugRoles.IsMatch(animation)) return new WordReading(CouplePairing.Either, WordConfidence.Weak);
        var parents = groupIsList ? $"{groupText} {modText}" : modText;
        if (Act(parents) is { } fromParents) return new WordReading(fromParents, WordConfidence.Weak);
        // A list's name says nothing about a one-person animation in it, in any pack.
        if ((couple || !groupIsList) && AnyCouple.IsMatch(parents + " " + groupText)) return new WordReading(CouplePairing.Either, WordConfidence.Weak);
        if (couple && Tease.IsMatch(own)) return new WordReading(CouplePairing.Either, WordConfidence.Weak);
        if (Penetration.IsMatch(parents)) return new WordReading(CouplePairing.MxF, WordConfidence.Weak);
        if (!couple && SoloWords.IsMatch(parents)) return new WordReading(CouplePairing.Solo, WordConfidence.Weak);
        if (!couple) return new WordReading(CouplePairing.Solo, WordConfidence.None);
        return new WordReading(NoPenisAssumed.IsMatch(group) ? CouplePairing.Either : CouplePairing.MxF, WordConfidence.None);
    }

    /// What one animation's name says, or null when it says nothing reliable.
    private static WordReading? Named(string text, bool couple)
    {
        // Before position words: a note can describe a bound pose as "doggystyle" with nobody else there.
        if (!couple && BoundAlone.IsMatch(text) && !PenisAct.IsMatch(text)) return new WordReading(CouplePairing.Solo, WordConfidence.Reliable);
        if (Group.IsMatch(text)) return new WordReading(CouplePairing.MxF, WordConfidence.Reliable);
        if (Act(text) is { } act) return new WordReading(act, WordConfidence.Strong);
        if (Penetration.IsMatch(text) && !FaceRiding.IsMatch(text))
            return Device.IsMatch(text)
                ? new WordReading(CouplePairing.Either, WordConfidence.Reliable)
                : new WordReading(CouplePairing.MxF, WordConfidence.Reliable);
        return !couple && SoloWords.IsMatch(text) ? new WordReading(CouplePairing.Solo, WordConfidence.Reliable) : null;
    }

    /// A group that isn't a list holds one animation's roles and variants, so they share a tag: an
    /// option with nothing to say ("Enable") takes what its siblings say.
    public static IReadOnlyList<WordReading> UnifyGroup(string group, IReadOnlyList<WordReading> options)
    {
        if (ListGroup.IsMatch(group) || options.Count < 2) return options;

        var best = options.OrderByDescending(reading => reading.Confidence)
            .ThenByDescending(reading => reading.State != CouplePairing.Solo).First();
        return options.Select(reading => reading.Confidence < WordConfidence.Reliable ? best : reading).ToList();
    }

    /// The best reading of each animation among identical files, so a note on one pack's copy
    /// ("doggystyle") reaches another's. Strong readings that disagree cancel out.
    public static Dictionary<string, WordReading> SharedByFiles(IEnumerable<(string? Key, WordReading Reading)> options)
    {
        var shared = new Dictionary<string, WordReading>(StringComparer.Ordinal);
        var cancelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, reading) in options)
        {
            if (key == null || !key.StartsWith(AnimationHasher.FilesPrefix, StringComparison.Ordinal)) continue;
            if (!shared.TryGetValue(key, out var existing) || reading.Confidence > existing.Confidence)
                shared[key] = reading;
            else if (reading.Confidence == existing.Confidence && reading.State != existing.State && reading.Confidence == WordConfidence.Strong)
                cancelled.Add(key);
        }
        foreach (var key in cancelled) shared.Remove(key);
        return shared;
    }

    /// Words naming a penis act or an act without one. Names that mention both ("Dom eats ass and milks
    /// sub's cock") say nothing reliable.
    private static CouplePairing? Act(string text)
    {
        var noPenis = NoPenisAct.IsMatch(text);
        var penis = PenisAct.IsMatch(text) && !PhallicToy.IsMatch(text);
        return noPenis == penis ? null : noPenis ? CouplePairing.FxF : CouplePairing.MxF;
    }
}
