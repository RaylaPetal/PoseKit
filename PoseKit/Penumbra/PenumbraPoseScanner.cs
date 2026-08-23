using System.Collections.Generic;
using System.IO;

namespace PoseKit.Penumbra;

public sealed record DiscoveredPose(string ModName, string FilePath, PoseIdentifier Identifier);

/// <summary>
/// For every mod enabled in the local player's active collection, recursively scans the whole mod
/// folder for .pap files (not limited to a folder literally named "Animations" — real pose mods
/// don't reliably follow that convention; see Synastry-main/EmoteLink/AnimationManifestScanner.cs,
/// which scans the entire mod directory) and maps recognized ones to a PoseIdentifier via
/// PoseNameHeuristics.
/// </summary>
public sealed class PenumbraPoseScanner(PenumbraIpc ipc)
{
    public List<DiscoveredPose> Scan()
    {
        var results = new List<DiscoveredPose>();

        var modList = ipc.TryGetModList();
        var modRoot = ipc.TryGetModDirectory();
        if (modList == null || modRoot == null) return results;

        var collectionId = ipc.TryGetLocalPlayerCollectionId();

        foreach (var (modDirectory, modName) in modList)
        {
            if (collectionId is { } cid && ipc.TryGetEnabledOptions(cid, modDirectory) == null)
                continue; // mod disabled (or not configured) in the active collection

            var modPath = Path.Combine(modRoot, modDirectory);
            if (!Directory.Exists(modPath)) continue;

            foreach (var file in Directory.EnumerateFiles(modPath, "*.pap", SearchOption.AllDirectories))
            {
                var identifier = PoseNameHeuristics.Identify(file);
                if (identifier is { } id)
                    results.Add(new DiscoveredPose(modName, file, id));
            }
        }

        return results;
    }
}
