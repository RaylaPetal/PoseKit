namespace PoseKit.Pairing;

using System.Text;

/// <summary>Stable hash for a Penumbra mod directory path, used to shrink it on the wire (see
/// PairingComposer.ComposeCapturedStateTail) instead of sending the literal path, which can be long
/// for deeply-nested mod packs. Must be deterministic across processes — unlike string.GetHashCode(),
/// which is randomized per process and would never match between two different game clients.</summary>
public static class ModDirectoryHash
{
    public static string Compute(string modDirectory)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(modDirectory))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash.ToString("x8");
    }
}
