namespace PoseKit.Pairing;

using System.Text;

/// <summary>Short FNV-1a hash of a mod directory for the pairing wire format. Must be stable across
/// processes, so string.GetHashCode() can't be used.</summary>
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
