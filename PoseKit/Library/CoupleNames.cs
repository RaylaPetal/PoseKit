using System.Linq;
using System.Text.RegularExpressions;

namespace PoseKit.Library;

/// <summary>
/// Spots names that declare two or more roles, following the Nightlife+/GoonersLife+ conventions:
/// "[Dom&amp;Sub]", "[Sub&amp;Dog]", "(Dom-Csit1/Sub-Csit2)", "[1-∞]", "Threesome".
/// </summary>
public static class CoupleNames
{
    private static readonly Regex[] Markers =
    [
        new(@"\bf?dom\d*\s*&\s*sub", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\[[^\]]*\w\s*&\s*\w[^\]]*\]", RegexOptions.Compiled),
        new(@"\bf?dom\d*\s*-[^/)]*/\s*sub", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Standalone mods: "[Sub-Gdance, Dom-Hdance]", "[Dom-Doze1, Sub-Doze2]".
        new(@"\b(f?dom|sub)\d*\s*-[^,\])/]*[,/]\s*(f?dom|sub)\d*\s*-", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\[1-∞\]", RegexOptions.Compiled),
        new(@"\b(threesome|3sum|foursome|4some|spitroast|bukkake)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <param name="modName">Only for a mod whose sole option is its default files, where the mod's
    /// name is the only one that says anything.</param>
    public static bool IsCouple(string groupName, string optionName, string modName = "") =>
        Markers.Any(marker => marker.IsMatch(optionName) || marker.IsMatch(groupName) ||
                              (modName.Length > 0 && marker.IsMatch(modName)));
}
