using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PoseKit.Library.Categories;

/// <summary>What automatic categories are worked out from: names and the mod author's descriptions.</summary>
public readonly record struct AutoCategoryInput(
    string ModName, string GroupName, string OptionName, string GroupDescription, string OptionDescription, bool IsCouple);

/// <summary>
/// Themes and acts PoseKit works out by itself, so most animations land in the right place without
/// the player tagging them. Tuned and scored against hand-reviewed Nightlife+ and GoonersLife+
/// categories; the player's categories.json still wins over all of this.
///
/// It reads the mod, group and option names plus the option's description (group descriptions are
/// flavor text for a whole list, so they'd tag everything in it). Stock description lines
/// ("intensity: …", "by …", links) are dropped first. Mod authors' gender notation (♀/♂ per role,
/// including the game-font glyphs Nightlife+ uses) decides Lesbian and Gay where present.
///
/// Option names embed their trigger emotes ("Dom-Tdance/Sub-Beesknees", "(BreathControl)"), so
/// patterns that could hit an emote name ("dance", "lick", "press") only match at the start of a word.
/// </summary>
public static class AutoCategories
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private const string Solo = "Solo", Vanilla = "Vanilla", Lesbian = "Lesbian", Gay = "Gay", Straight = "Straight", Futa = "Futa";
    private const string Idles = "Idles", Dancing = "Dancing", Cuddles = "Cuddles";

    /// The library's top level, in display order. The pairing roots hold the other themes (categories);
    /// Cuddles, Solo, Idles and Dancing list their animations directly.
    public static readonly IReadOnlyList<string> Roots = [Futa, Gay, Straight, Lesbian, Cuddles, Solo, Idles, Dancing];

    public static readonly IReadOnlySet<string> PairingRoots =
        new HashSet<string>([Futa, Gay, Straight, Lesbian], StringComparer.OrdinalIgnoreCase);

    public static bool IsRoot(string theme) => Roots.Contains(theme, StringComparer.OrdinalIgnoreCase);

    // Categories that don't make an animation a pairing (a signboard is nobody's straight scene).
    private static readonly HashSet<string> NonPairingCategories = new(StringComparer.OrdinalIgnoreCase) { "Props" };
    private const string Oral = "Oral", Anal = "Anal", Vaginal = "Vaginal";

    private static readonly (string Name, Regex Pattern)[] ThemeRules =
    [
        ("BDSM", new(@"bdsm|kink|bondage|shackle|cuff|(?<![a-z])tied|leash|struggle|pillory|choke|spank|prisoner|captive|whip|collar|slave|dungeon|tortur|\bcbt\b|humiliat|cuckold|restrain|\bgag(s|ged|ging)?\b|paddl|\btie (you|her|him|them|me) up\b|blindfold|human (seat|chair|furniture|table)|step on me|ball ?kick|cock ?slap|pet ?play|amborella", Options)),
        ("Rough", new(@"choke|slam|pound|harder|rough|brutal|throat ?fuck|face ?fuck|wall bang|ramming|railing|forced|(mating|breeding) press|abus|pinned|pile ?driver|deep ?(fuck|throat)|throated|claimed|gripped|gang ?bang|(?<![a-z])fist(ing|ed)?\b|hard dog|pocket pussy|face down(?! ass up, jack)", Options)),
        ("Romantic", new(@"cuddle|snuggle|kiss|make ?out|hug|embrac|sweetheart|(?<![a-z])lov(e|ing)|sleep|wholesome|tender|gentle|proposal|marry|lap pillow|spoon|glomp|kabedon|belly rub", Options)),
        ("Group", new(@"threesome|3 ?sum|4 ?some|5 ?some|foursome|gang ?bang|orgy|spitroast|fxfxf|mxfxm|dom&sub-[3-9]|dom-[2-9]&sub|\bdom\d|bukkake|\[1-|double (bj|blowjob|handjob)|sandwich|\btrain\b", Options)),
        ("Beast & Monster", new(@"beast|monster|tentacle|sub&(dog|horse|monster|amalj|mimic|machine)|carbuncle|golem|amalj", Options)),
        ("Big Races", new(@"hroth|\broe\b|roegadyn|bigx|\bbig ?(m|male|x)\b|m big|big male", Options)),
        ("Femdom", new(@"\[f ?dom&|\(f ?dom-(?![^)]*\bsub\b[^)]*\(f ?dom)|femdom|mistress|domme|dominatrix|step on me|ball ?kick|cock ?slap|footworks", Options)),
        (Futa, new(@"futa|dickgirl|fxfxf|\bf\b[^\n]*\b(giving|giver)\b|\b(giving|giver)\b[^\n]*\bf\b", Options)),
        ("Masturbation", new(@"masturbat|jerk ?off|self[ -]?(care|love|pleasure|suck)|\[hands?\]|\[toys?\]|dildo|fuck machine|anal beads|pussy ?rub|flicking the bean", Options)),
        (Dancing, new(@"(?<![a-z])danc(e|ing)\b(?!\))|twerk|pole ?danc|lap ?dance|strip ?tease|dirty.?danc", Options)),
        (Idles, new(@"\bidle\b(?! ?\d)|walk cycle|stance|pose pack", Options)),
        ("Props", new(@"signboard|parasol", Options)),
        (Straight, new(@"\b(m ?[x/] ?f|f ?[x/] ?m)\b|straight|hetero", Options)),
        (Gay, new(@"\bm ?[x/&] ?m\b|\bgay\b|yaoi", Options)),
        (Lesbian, new(@"\bf(\s+(big|small|tall|standard|short))? ?[x/&] ?f\b(?! ?x)|lesbian|yuri|sapphic|girl ?on ?girl|scissor|\btrib|kitty licking kitty", Options)),
    ];

    // Themes that already say what kind of scene it is; Vanilla is for sex animations with none of these.
    private static readonly HashSet<string> FlavorThemes = new(StringComparer.OrdinalIgnoreCase)
        { "BDSM", "Rough", "Romantic", "Group", "Beast & Monster", "Big Races", "Femdom", "Futa", "Masturbation", Solo,
          "Dancing", "Idles", "Props" };

    private static readonly Regex SoloPattern = new(@"\bsolo\b|masturbat|self[ -]?(care|love|pleasure)|\[hands?\]|\[toys?\]", Options);

    private static readonly (string Name, Regex Pattern)[] ActRules =
    [
        (Oral, new(@"oral|bukkake|on (your|her|his|my) knees|blow ?job|\bbj\b|cunni|lingus|(?<![a-z])lick|throat|face ?fuck|face ?rid|face ?sit|suck|\b69\b|giving head|head female|eat(s|ing|en)? (out|ass|pussy)|eaten out|(?<![a-z])rim(ming|job)?\b|tongue|dinner time|worship", Options)),
        (Anal, new(@"\banal\b|\bin (the |her |his |your |my )?ass\b|ass ?fuck|butt ?fuck|anus|eat(s|ing)? ass|(?<![a-z])rim(ming|job)?\b|\[anal\]|ass 2 ass", Options)),
        ("Handjob & Fingering", new(@"hand ?job|finger|jerk|strok|milk(ing)?\b|grope|hitachi|pussy ?rub|panty rub|mage hand|pleasing my partner", Options)),
        ("Toys", new(@"dildo|\btoys?\b|machine|vibrat|hitachi|beads|\bplug\b|strap.?on|pocket pussy|fleshlight", Options)),
        ("Foot & Titjob", new(@"foot ?(job|work)|feet|boob ?job|titty|tit ?(job|fuck)|paizuri|thigh ?job|step on me", Options)),
        ("Kissing & Cuddling", new(@"kiss|make ?out|cuddle|snuggle|(?<![a-z])hug(?!ger)|spoon|lap pillow|lovey|glomp", Options)),
        ("Teasing", new(@"teas|show ?off|spread|grind|twerk|strip|booty|butt (shake|sway)|wiggle|jiggle|bounc|sway|flash|yoga|gib milk|pole", Options)),
        ("DP", new(@"\bdp\b|double pen|sandwich", Options)),
        ("Bondage", new(@"bondage|struggl|\btie (you|her|him|them|me) up\b|\bbound\b|\bgag|latex|glory wall|brainwash|(?<![a-z])tied|rope|shackle|cuff|chain|pillory|gibbet|suspension|leash|collar|\bcage|stocks|restrain|hogtie|shibari|wooden horse|spread posts|castigation|squatting pole|signpost|hooked|scavenger|\bcross\b", Options)),
        ("Humiliation", new(@"humiliat|cuckold|human (seat|chair|furniture|table)|step on me|degrad|\bcbt\b|ball ?kick|cock ?slap|slave", Options)),
        ("Spanking", new(@"spank|paddl|whip|(?<![a-z])slap|flog|caning", Options)),
        ("Submission", new(@"\bbeg(ging)?\b|submi(t|ssive|ssion)|pet ?play", Options)),
        ("Watersports", new(@"\bpee\b|piss|watersport|golden shower", Options)),
        ("Scissoring", new(@"scissor|\btrib", Options)),
        ("Fisting", new(@"(?<![a-z])fist(ing|ed)?\b", Options)),
    ];

    // A sex position counts as vaginal unless the names say oral or anal.
    private static readonly Regex VaginalPattern =
        new(@"vaginal|pussy(?! ?(lick|eat|play|rub))|clit|flicking the bean|missionary|cowgirl|cowboy|dogg(y|ie)|riding|\bride|nelson|plap|pound|\bsex\b|breed|\bpress|pinned|slam|mating|ramming|railing|filled|\bfuck(ing|ed)?\b|thrust|penetrat|creampie|cum pump|lotus(?! ?cuddl)|amazon|\[pussy\]|bench sex", Options);

    // The mods' position groups hold sex animations; oral has its own group.
    private static readonly Regex SexPositionGroup =
        new(@"nsfw-(standing|sitting|laying)|(standing|sitting|laying) animation list", Options);

    // Two or more roles named in the option ("Giver"/"Receiver", "[Top]", "Hugger"/"Hugged").
    private static readonly Regex RoleWords =
        new(@"\b(giv(er|ing)|receiv(er|ing)|hugg(er|ed)|finger(er|ed)|\[?top\]?|\[?bottom\]?|left chara|right chara|blowjob(ber|bed)|boobjob(ber|bed)|fucking|fucked|69)\b", Options);

    // Acts two women can play: oral on a partner, fingering, scissoring, kissing; cuddles outside sex
    // scenes. Only counted when nothing names a penis or a penetrative position.
    private static readonly Regex LesbianActs =
        new(@"cunni|lingus|kitty lick|eat(s|ing|en)? (out|ass|pussy)|eaten out|face ?sit|face ?rid|\b69\b|scissor|\btrib|finger|partner'?s pussy|holding pussy|pussy (lick|play)|hitachi|kiss|make ?out|booba|milking|strap.?on|dinner time|worship", Options);

    private static readonly Regex LesbianCuddles =
        new(@"cuddl|snuggl|(?<![a-z])hug|embrac|lap pillow|kabedon", Options);

    private static readonly Regex Penetration =
        new(@"missionary|cowgirl|cowboy|dogg(y|ie)|riding|\bride|plap|pound|\bsex\b|breed|\bpress|filled|\bfuck|thrust|penetrat|creampie|lotus(?! ?cuddl)|\bdp\b", Options);

    // Acts that take two people, so they make a couple even when the roles aren't named.
    private static readonly Regex TwoPersonActs =
        new(@"face ?(sit|rid)|\b69\b|scissor|\btrib|kiss|make ?out|cuddl|snuggl|(?<![a-z])hug|embrac|kabedon|proposal|spoon|grind(ed|ing) on|lap grind", Options);

    // Groups that collect many animations ("Oral Animation List* 285 - 307", "~CuddleNsleep~", "Nsfw-Oral").
    private static readonly Regex ListGroup = new(@"\blist\b|cuddlensleep|^nsfw-|\bpack\b|~", Options);

    /// A group collecting many separate animations, rather than one animation's roles or variants.
    public static bool IsListGroup(string groupName) => ListGroup.IsMatch(groupName);

    // Nightlife+-style catalogue numbers ("331 [NFLB] …"); short numbers ("1 Mid pose") are variants.
    private static readonly Regex CatalogueNumber = new(@"^\s*\d{3}[.)]?\s", Options);

    /// Whether this option is an animation of its own rather than a role or variant of its group's.
    public static bool IsSeparateAnimation(string groupName, string optionName) =>
        ListGroup.IsMatch(groupName) || CatalogueNumber.IsMatch(optionName);

    // Packs tag one-person animations "[Solo-Sub]", "[Dom-Solo]", "[Hands]", "[Hand/Toy]".
    private static readonly Regex SoloTag = new(@"\[(solo|[a-z]+-solo|solo-[a-z]+|hands?|hand/toy|toys?|p\+hands)\]", Options);

    // "176. ", "121) " list numbers, so "69. Anal Buddy" isn't read as a 69.
    private static readonly Regex ListNumber = new(@"^\s*\d+[.)]?\s+", Options);

    private static readonly Regex Penis =
        new(@"cock|dick|penis|\bbj\b|blow ?job|hand ?job|futa|throat|jerk|strok|cum pump|thigh ?job|paizuri|titty|boob ?job|deepthroat|balls\b|\bmasc\b|cuckold|condom|\bmdom\b", Options);

    // Description lines every option of some packs repeats; they say nothing about the animation.
    private static readonly Regex StockLine =
        new(@"^\s*(intensity\s*:|similar height|by\s|note from mod|please check this link|-?\s*https?://|solo\s*$)", Options);

    // Mod authors' gender marks: ♀/♂, and the game-font glyphs Nightlife+ uses for them.
    private const char Female = '', Male = '';

    /// Display order for automatic names; other names follow alphabetically.
    public static readonly IReadOnlyList<string> ThemeOrder =
        [.. Roots, Vanilla, "Romantic", "BDSM", "Rough", "Femdom", "Masturbation", "Group", "Beast & Monster", "Big Races",
         "Props"];

    public static readonly IReadOnlyList<string> ActOrder =
        [Oral, Vaginal, Anal, "Handjob & Fingering", "Toys", "Foot & Titjob", "DP", "Fisting", "Scissoring",
         "Kissing & Cuddling", "Teasing", "Bondage", "Spanking", "Submission", "Humiliation", "Watersports"];

    /// Mod, group and option names plus the option's own description, without stock lines.
    private static string Text(in AutoCategoryInput input)
    {
        var description = string.Join('\n', input.OptionDescription.Replace("\r", "").Split('\n')
            .Where(line => !StockLine.IsMatch(line) && !HasGenderMarks(line)));
        return $"{input.ModName}\n{input.GroupName}\n{ListNumber.Replace(input.OptionName, "")}\n{description}";
    }

    /// Adds the automatic themes and acts for one option.
    public static void Apply(in AutoCategoryInput input, ISet<string> themes, ISet<string> acts)
    {
        var optionName = ListNumber.Replace(input.OptionName, "");
        var text = Text(input);
        var notation = GenderNotation.Parse(input.OptionDescription);
        var soloByAuthor = input.OptionDescription.TrimStart().StartsWith("solo", StringComparison.OrdinalIgnoreCase);
        // A list group's name describes the list, not this option.
        var names = ListGroup.IsMatch(input.GroupName) ? optionName : $"{input.GroupName}\n{optionName}";
        var namedTwo = input.IsCouple || RoleWords.IsMatch(optionName) || TwoPersonActs.IsMatch(names);
        var couple = namedTwo || notation.IsCouple;

        foreach (var (name, pattern) in ThemeRules)
            if (pattern.IsMatch(text)) themes.Add(name);
        // Dances and idles are their own theme, though their authors mark them solo too.
        if (!couple && (soloByAuthor || SoloPattern.IsMatch(text)) && !themes.Contains("Dancing") && !themes.Contains("Idles"))
            themes.Add(Solo);

        foreach (var (name, pattern) in ActRules)
            if (pattern.IsMatch(text)) acts.Add(name);
        if (!acts.Contains(Oral) && !acts.Contains(Anal) &&
            (VaginalPattern.IsMatch(text) || SexPositionGroup.IsMatch(input.GroupName)))
            acts.Add(Vaginal);

        // The authors' own marks beat guessing from the names, on couple animations (solo poses and
        // dances carry the marks too). "♂♂" pairings are marked "may work" on most blowjobs, so Gay
        // needs "made for".
        // Solo poses carry marks too, so the names must show two roles.
        var namedCouple = namedTwo && !themes.Contains(Solo) &&
                          !themes.Contains("Dancing") && !SoloTag.IsMatch(optionName);
        var twoWomen = namedCouple && !themes.Contains("Group") && !themes.Contains("Beast & Monster") && !Penis.IsMatch(text);
        if (twoWomen && notation.FemaleFemale) themes.Add(Lesbian);
        if (namedCouple && notation.MaleMaleMadeFor) themes.Add(Gay);
        // Only guessed from acts two women can do, not marked as lesbian, so it suits a man and a
        // woman just as well.
        if (twoWomen && !themes.Contains(Lesbian) && !Penetration.IsMatch(text) &&
            (LesbianActs.IsMatch(text) || (LesbianCuddles.IsMatch(text) && !acts.Any(IsSexAct))))
        {
            themes.Add(Lesbian);
            themes.Add(Straight);
        }

        // A couple's sex scene with no act named is most likely penetrative.
        if (couple && acts.Count == 0 && (themes.Contains("Beast & Monster") || SexPositionGroup.IsMatch(input.GroupName)))
            acts.Add(Vaginal);

        // A dance with nothing more specific named is a tease.
        if (themes.Contains("Dancing") && acts.Count == 0) acts.Add("Teasing");

        // The packs rate many options "intensity: normal / comfy / hardcore". For a sex scene nothing else
        // flavors, hardcore means rough rather than vanilla; on solo, femdom or dance options it only
        // means explicit, so it's not applied there.
        if (acts.Any(IsSexAct) && !themes.Any(FlavorThemes.Contains))
            themes.Add(HardcoreIntensity.IsMatch(input.OptionDescription) ? "Rough" : Vanilla);

        ResolveCuddles(input, themes, acts);
    }

    /// Cuddles, kisses, lap-sitting and cozy rest are their own root, read from the option's own
    /// text so it works for any mod (a list group's name, like "CuddleNsleep Animation List",
    /// describes the list and isn't used). With no sex act it leaves the pairings and Solo, since it
    /// suits anyone. A light act (a handjob or fingering while cuddling) is still a cuddle and keeps its
    /// pairing too; penetration, oral, feet or masturbation never are ("Mission Romance").
    private static void ResolveCuddles(in AutoCategoryInput input, ISet<string> themes, ICollection<string> acts)
    {
        if (themes.Contains("Masturbation") || themes.Contains("Props") ||
            acts.Any(a => IsSexAct(a) && a != "Handjob & Fingering")) return;

        var names = ListGroup.IsMatch(input.GroupName) ? input.OptionName : $"{input.GroupName}\n{input.OptionName}";
        // A default-only mod is named only by the mod.
        if (input.GroupName == "Default") names = $"{input.ModName}\n{names}";
        var description = string.Join('\n', input.OptionDescription.Replace("\r", "").Split('\n')
            .Where(line => !StockLine.IsMatch(line)));
        // Sharing a seat is only a cuddle when nothing says it's a tease, bondage or a rough scene.
        var lapCuddle = LapSit.IsMatch(names) && !themes.Any(LapSitExclusions.Contains) &&
                        !acts.Any(a => a is "Teasing" or "Bondage" or "Spanking" or "Humiliation");
        if (!CuddleSignal.IsMatch($"{names}\n{description}") && !lapCuddle) return;

        themes.Add(Cuddles);
        if (!acts.Any(IsSexAct))
            foreach (var root in PairingRoots.Append(Solo)) themes.Remove(root);
    }

    private static readonly Regex CuddleSignal = new(
        @"cuddl|snuggl|(?<![a-z])hug|embrac|spoon|glomp|nuzzl|head ?pat|belly ?rub|kabedon|kiss|make ?out|lovey|" +
        @"propos(al|e)|marry|massag|\bbath|shower|lounge|\bchill|\bpurr|\bsleep|curl(s|ed|ing)? up|butt ?grab|" +
        @"lap ?(pillow|dog|cat|sit)|sits? on [\w'\s]{0,20}lap\b", Options);

    private static readonly HashSet<string> LapSitExclusions = new(StringComparer.OrdinalIgnoreCase)
        { "BDSM", "Rough", "Big Races", "Group", "Beast & Monster" };

    // A couple where both roles are chair sits shares one seat: one sits on the other's lap.
    private static readonly Regex LapSit = new(@"\b(f?dom|sub)\d*-c(hair)?sit\w*[^/)]*/\s*(f?dom|sub)\d*-c(hair)?sit", Options);

    private static readonly Regex HardcoreIntensity = new(@"intensity\s*:[^\n]*\bhardcore\b", Options);

    private static readonly Regex StrapOn = new(@"strap.?on", Options);

    /// Pairings filled in before the player's own file, so a player's removal sticks: Straight by
    /// default, and Lesbian too for a Straight animation that doesn't need a penis (a bound sub
    /// struggling, foot worship).
    public static void AddPairingFallbacks(in AutoCategoryInput input, ISet<string> themes, IReadOnlySet<string> acts)
    {
        AddDefaultRoot(input, themes, acts);
        if (!themes.Contains(Straight) || themes.Contains(Lesbian) || themes.Contains(Gay) || themes.Contains(Futa)) return;
        if (themes.Contains(Solo) || themes.Contains(Idles) || themes.Contains(Dancing)) return;
        if (themes.Contains("Group") || themes.Contains("Beast & Monster") || themes.Contains("Big Races")) return;
        if (acts.Any(a => a is Vaginal or Anal or "DP")) return;

        var text = Text(input);
        if (Penis.IsMatch(text) || MaleRole.IsMatch(text) || (Penetration.IsMatch(text) && !StrapOn.IsMatch(text))) return;
        // Unnamed oral ("On Your Knees") is a blowjob far more often than not.
        if (acts.Contains(Oral) && !LesbianActs.IsMatch(text)) return;
        themes.Add(Lesbian);
    }

    // A man named in a role or size option: "Tall Males", "Standard M", "MxF".
    private static readonly Regex MaleRole =
        new(@"\bmales?\b|\b(standard|tall|big|small|mid|short)\s+m\b|\bm\s*[x&/]\s*[fm]\b|\bf\s*[x&/]\s*m\b", Options);

    /// Every animation gets a root, so nothing is reachable only through "All animations". Most couple
    /// animations are written for a man and a woman without saying so, so a categorized or two-person
    /// one is Straight; a one-person tease is Solo; anything with no categories at all (party emotes,
    /// lounging) is an idle.
    private static void AddDefaultRoot(in AutoCategoryInput input, ISet<string> themes, IReadOnlySet<string> acts)
    {
        // A sex scene that's also a cuddle still needs its pairing; a plain cuddle doesn't.
        if (themes.Contains(Cuddles) && !acts.Any(IsSexAct)) return;
        if (themes.Any(t => IsRoot(t) && !string.Equals(t, Cuddles, StringComparison.OrdinalIgnoreCase))) return;
        if (themes.Any(t => !NonPairingCategories.Contains(t)) || IsTwoPerson(input)) themes.Add(Straight);
        else if (acts.Contains("Teasing")) themes.Add(Solo);
        else if (acts.Count == 0) themes.Add(Idles);
    }

    private static bool IsTwoPerson(in AutoCategoryInput input)
    {
        var optionName = ListNumber.Replace(input.OptionName, "");
        var names = ListGroup.IsMatch(input.GroupName) ? optionName : $"{input.GroupName}\n{optionName}";
        return input.IsCouple || RoleWords.IsMatch(optionName) || TwoPersonActs.IsMatch(names) ||
               GenderNotation.Parse(input.OptionDescription).IsCouple;
    }

    private static bool IsSexAct(string act) =>
        act is Oral or Vaginal or Anal or "Handjob & Fingering" or "Foot & Titjob" or "DP" or "Fisting" or "Scissoring";

    private static bool HasGenderMarks(string line) =>
        line.IndexOfAny([Female, Male, '♀', '♂']) >= 0;

    /// <summary>
    /// The per-role gender line some packs put first in each description: a pairing part ("♀♀?♂♂?",
    /// which same-sex pairings work) then each role ("♀↑!" female top, made for it; "♂↓?" male
    /// bottom, may work). Game-font glyphs stand in for ♀/♂ in Nightlife+.
    /// </summary>
    private readonly record struct GenderNotation(bool Known, bool FemaleFemale, bool MaleMaleMadeFor, bool IsCouple)
    {
        private static readonly Regex Pair = new(@"([♀♂])\1[!?]", RegexOptions.Compiled);
        private static readonly Regex Role = new(@"([♀♂])\w?([↑↓])[!?]", RegexOptions.Compiled);

        public static GenderNotation Parse(string description)
        {
            var line = description.Replace("\r", "").Split('\n').FirstOrDefault(HasGenderMarks);
            if (line == null) return default;

            var pairs = Pair.Matches(line).Select(m => (Female: IsFemale(m.Groups[1].Value[0]), MadeFor: m.Value.EndsWith('!'))).ToList();
            var roles = Role.Matches(line).Select(m => (Female: IsFemale(m.Groups[1].Value[0]), Top: m.Groups[2].Value == "↑")).ToList();
            bool Has(bool female, bool top) => roles.Any(r => r.Female == female && r.Top == top);

            var femaleFemale = pairs.Any(p => p.Female) || (Has(true, true) && Has(true, false));
            var maleMaleMadeFor = pairs.Any(p => !p.Female && p.MadeFor);
            return new GenderNotation(true, femaleFemale, maleMaleMadeFor, roles.Any(r => r.Top) && roles.Any(r => !r.Top));
        }

        private static bool IsFemale(char c) => c is Female or '♀';
    }
}
