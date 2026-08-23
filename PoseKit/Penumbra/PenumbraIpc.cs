using System;
using System.Collections.Generic;
using Penumbra.Api.IpcSubscribers;

namespace PoseKit.Penumbra;

/// <summary>
/// Thin wrapper over the Penumbra.Api IPC subscribers needed for pose discovery, referencing the
/// published Penumbra.Api NuGet package (not the Penumbra.Api submodule checked into this repo,
/// which is reference-only material and is never built — see PoseKit.csproj).
/// Every call is guarded so Feature 3 degrades gracefully when Penumbra isn't installed/running,
/// per the design doc's "hard dependency ... must degrade gracefully" note.
/// </summary>
public sealed class PenumbraIpc
{
    private readonly GetModList getModList = new(Plugin.PluginInterface);
    private readonly GetModDirectory getModDirectory = new(Plugin.PluginInterface);
    private readonly GetCollectionForObject getCollectionForObject = new(Plugin.PluginInterface);
    private readonly GetCurrentModSettings getCurrentModSettings = new(Plugin.PluginInterface);

    public bool IsAvailable
    {
        get
        {
            try
            {
                getModDirectory.Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// Mod directory -> display name, for every mod in the currently active collection (not
    /// filtered by enabled state; use TryGetEnabledOptions to check a specific mod).
    public Dictionary<string, string>? TryGetModList()
    {
        try { return getModList.Invoke(); }
        catch { return null; }
    }

    public string? TryGetModDirectory()
    {
        try { return getModDirectory.Invoke(); }
        catch { return null; }
    }

    public Guid? TryGetLocalPlayerCollectionId()
    {
        try
        {
            var (objectValid, _, effective) = getCollectionForObject.Invoke(0);
            return objectValid ? effective.Id : null;
        }
        catch { return null; }
    }

    /// Null if the mod is disabled in this collection (or the call failed); otherwise the set of
    /// currently-selected option names across all of the mod's option groups.
    public HashSet<string>? TryGetEnabledOptions(Guid collectionId, string modDirectory)
    {
        try
        {
            var (_, settings) = getCurrentModSettings.Invoke(collectionId, modDirectory);
            if (settings is not { } s) return null;
            var (enabled, _, selections, _) = s;
            if (!enabled) return null;

            var options = new HashSet<string>();
            foreach (var group in selections.Values)
                foreach (var option in group)
                    options.Add(option);
            return options;
        }
        catch { return null; }
    }
}
