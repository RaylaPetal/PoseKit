namespace PoseKit.Library;

public enum LibraryBucket { NotListed, Fits, DoesntFit }

/// <summary>
/// Sorts an option under the active filter. A wrong tag must never make an animation unreachable, so
/// anything contradicting the filter stays a click away under Doesn't fit. Only a solo the mod's
/// words actually state leaves the list; one that's merely the default for a name with no couple
/// markers might be wrong, so it stays reachable too.
/// </summary>
public static class LibraryBuckets
{
    /// The filter sorts the library into sections rather than just narrowing its tree.
    public static bool SectionsActive(LibraryTypeFilter filter) => filter is LibraryTypeFilter.FxF or LibraryTypeFilter.MxF;

    public static LibraryBucket Classify(LibraryTypeFilter filter, CouplePairing pairing, bool isDefault, bool namedAsCouple)
    {
        if (filter is LibraryTypeFilter.Solo or LibraryTypeFilter.Couple)
            return IsCouple(pairing, namedAsCouple) == (filter == LibraryTypeFilter.Couple) ? LibraryBucket.Fits : LibraryBucket.NotListed;
        if (!SectionsActive(filter)) return LibraryBucket.Fits;

        if (pairing == CouplePairing.Solo) return isDefault ? LibraryBucket.DoesntFit : LibraryBucket.NotListed;
        var fits = pairing == CouplePairing.Either || pairing == (filter == LibraryTypeFilter.FxF ? CouplePairing.FxF : CouplePairing.MxF);
        return fits ? LibraryBucket.Fits : LibraryBucket.DoesntFit;
    }

    /// Declared states decide; names are only consulted when nothing is declared.
    private static bool IsCouple(CouplePairing pairing, bool namedAsCouple) => pairing switch
    {
        CouplePairing.Solo => false,
        CouplePairing.Unknown => namedAsCouple,
        _ => true,
    };
}
