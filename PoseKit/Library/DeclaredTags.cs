using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PoseKit.Library;

/// <summary>Which couples an animation is for. Only declared evidence sets one; see DeclaredTags.</summary>
public enum CouplePairing
{
    Unknown,
    FxF,
    MxF,

    /// Works for either couple, shown as "M/F x F".
    Either,
    Solo,
}

/// <summary>
/// Reads what mod authors declared, and nothing else. Automatic categories were removed for guessing
/// from act words and group names, so guesses live apart, in NameGuess.
///
/// Nightlife+ writes role codes in option descriptions: a gender glyph, an arrow for the slot and
/// "?" or "!", e.g. "M↑! F↓? M↓?" (top must be male, bottom either). Both marks mean the gender is
/// allowed in that slot, so only the slots' allowed genders matter.
/// </summary>
public static class DeclaredTags
{
    private const char MaleGlyph = '';
    private const char FemaleGlyph = '';

    private static readonly Regex RoleToken = new("([])([↑↓])[?!]", RegexOptions.Compiled);
    private static readonly Regex SoloWord = new(@"\bsolo\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "FxF", "MxF", "FXFXF"; set apart so it can't match inside a word such as "BigxF".
    private static readonly Regex LiteralLabel = new("(?<![A-Za-z0-9])[fm](?:x[fm])+(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// The state an option's description declares, or null when it declares none. The solo mark wins,
    /// since a few solo options also carry a code.
    public static CouplePairing? FromDescription(string description)
    {
        if (SoloWord.IsMatch(description)) return CouplePairing.Solo;

        var matches = RoleToken.Matches(description);
        if (matches.Count == 0) return null;

        bool topMale = false, topFemale = false, bottomMale = false, bottomFemale = false;
        foreach (Match match in matches)
        {
            var male = match.Groups[1].Value[0] == MaleGlyph;
            if (match.Groups[2].Value == "↑")
            {
                topMale |= male;
                topFemale |= !male;
            }
            else
            {
                bottomMale |= male;
                bottomFemale |= !male;
            }
        }

        var fitsFemales = topFemale && bottomFemale;
        var fitsMixed = (topMale && bottomFemale) || (topFemale && bottomMale);
        if (fitsFemales && fitsMixed) return CouplePairing.Either;
        if (fitsFemales) return CouplePairing.FxF;

        // Tags split on whether a penis is involved, so a male-only scene counts as MxF. A code naming
        // only a female in one slot says nothing about the other, so it declares nothing.
        return fitsMixed || topMale || bottomMale ? CouplePairing.MxF : null;
    }

    /// The state a literal label in a name declares, or null. Only an animation's own name counts: a
    /// group such as "Spitroast FXFXF" also holds options for male partners.
    public static CouplePairing? FromName(string name)
    {
        var match = LiteralLabel.Match(name);
        if (!match.Success) return null;

        var label = match.Value.ToLowerInvariant();
        return label.Contains('m') ? CouplePairing.MxF : CouplePairing.FxF;
    }

    /// The chip's text.
    public static string TagText(this CouplePairing state) => state switch
    {
        CouplePairing.FxF => "FxF",
        CouplePairing.MxF => "MxF",
        CouplePairing.Either => "M/F x F",
        CouplePairing.Solo => "Solo",
        _ => "?",
    };

    /// One animation's identity for sharing declarations: identical files mean the same animation,
    /// which names can't promise, since packs ship several variants under one name. Other than file
    /// identities, keys never inherit.
    public static Dictionary<string, CouplePairing> SharedByFiles(IEnumerable<(string? Key, CouplePairing? Declared)> options)
    {
        var shared = new Dictionary<string, CouplePairing>(StringComparer.Ordinal);
        foreach (var (key, declared) in options)
        {
            if (declared is not { } state || key == null || !key.StartsWith(AnimationHasher.FilesPrefix, StringComparison.Ordinal))
                continue;

            // Options that disagree cancel out, so the animation stays Unknown.
            shared[key] = shared.TryGetValue(key, out var existing) && existing != state ? CouplePairing.Unknown : state;
        }
        return shared;
    }
}
