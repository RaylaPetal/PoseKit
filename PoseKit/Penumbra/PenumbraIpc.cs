using System;
using System.Collections.Generic;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace PoseKit.Penumbra;

/// <summary>
/// Wrapper over the Penumbra IPC calls PoseKit uses. Every call is guarded so PoseKit keeps working
/// without Penumbra.
///
/// Writes only use temporary mod settings, so playing an animation never changes the user's saved
/// Penumbra configuration.
/// </summary>
public sealed class PenumbraIpc : IDisposable
{
    private const string Source = "PoseKit";

    /// A mod was deleted in Penumbra (its directory).
    public event Action<string>? ModDeleted;

    /// A mod's directory was renamed or moved in Penumbra (old, new).
    public event Action<string, string>? ModMoved;

    private readonly IDisposable modDeletedSubscriber;
    private readonly IDisposable modMovedSubscriber;

    public PenumbraIpc()
    {
        modDeletedSubscriber = global::Penumbra.Api.IpcSubscribers.ModDeleted.Subscriber(Plugin.PluginInterface,
            modDirectory => ModDeleted?.Invoke(modDirectory));
        modMovedSubscriber = global::Penumbra.Api.IpcSubscribers.ModMoved.Subscriber(Plugin.PluginInterface,
            (oldDirectory, newDirectory) => ModMoved?.Invoke(oldDirectory, newDirectory));
    }

    public void Dispose()
    {
        modDeletedSubscriber.Dispose();
        modMovedSubscriber.Dispose();
    }

    private readonly GetModList getModList = new(Plugin.PluginInterface);
    private readonly GetModPath getModPath = new(Plugin.PluginInterface);
    private readonly GetModDirectory getModDirectory = new(Plugin.PluginInterface);
    private readonly GetCollectionForObject getCollectionForObject = new(Plugin.PluginInterface);
    private readonly GetCurrentModSettings getCurrentModSettings = new(Plugin.PluginInterface);
    private readonly GetAllModSettings getAllModSettings = new(Plugin.PluginInterface);
    private readonly SetTemporaryModSettings setTemporaryModSettings = new(Plugin.PluginInterface);
    private readonly RemoveTemporaryModSettings removeTemporaryModSettings = new(Plugin.PluginInterface);
    private readonly RedrawObject redrawObject = new(Plugin.PluginInterface);

    /// Mods PoseKit has set temporary settings on, so they can all be undone.
    private readonly HashSet<string> touchedModDirectories = new();

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

    /// Mod directory -> display name, for every mod known to Penumbra (not filtered by enabled state).
    public Dictionary<string, string>? TryGetModList()
    {
        try { return getModList.Invoke(); }
        catch (Exception ex) { Plugin.Log.Warning($"[PoseKit] GetModList IPC call failed: {ex}"); return null; }
    }

    public string? TryGetModDirectory()
    {
        try { return getModDirectory.Invoke(); }
        catch (Exception ex) { Plugin.Log.Warning($"[PoseKit] GetModDirectory IPC call failed: {ex}"); return null; }
    }

    /// The mod's path in Penumbra's sort-folder tree (e.g. "Animations/Idles/Mod"), not its directory.
    public string? TryGetModPath(string modDirectory, string modName)
    {
        try
        {
            var (ec, fullPath, _, _) = getModPath.Invoke(modDirectory, modName);
            return ec == PenumbraApiEc.Success ? fullPath : null;
        }
        catch (Exception ex) { Plugin.Log.Warning($"[PoseKit] GetModPath IPC call failed for {modDirectory}: {ex}"); return null; }
    }

    public Guid? TryGetLocalPlayerCollectionId()
    {
        try
        {
            var (objectValid, _, effective) = getCollectionForObject.Invoke(0);
            return objectValid ? effective.Id : null;
        }
        catch (Exception ex) { Plugin.Log.Warning($"[PoseKit] GetCollectionForObject IPC call failed: {ex}"); return null; }
    }

    /// The mod's effective settings. Pass the priority back into TrySetTemporarySettings, which has
    /// no way to leave it unchanged.
    public (bool Enabled, int Priority, Dictionary<string, List<string>>? Selections) TryGetCurrentSettings(Guid collectionId, string modDirectory)
    {
        try
        {
            var (_, settings) = getCurrentModSettings.Invoke(collectionId, modDirectory);
            if (settings is not { } s) return (false, 0, null);
            var (enabled, priority, selections, _) = s;
            return (enabled, priority, selections);
        }
        catch (Exception ex) { Plugin.Log.Warning($"[PoseKit] GetCurrentModSettings IPC call failed for {modDirectory}: {ex}"); return (false, 0, null); }
    }

    /// Every mod's effective settings in one call. A missing mod has never been configured.
    public Dictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Selections)>? TryGetAllSettings(Guid collectionId)
    {
        try
        {
            var (_, all) = getAllModSettings.Invoke(collectionId);
            if (all == null) return null;

            var result = new Dictionary<string, (bool, int, Dictionary<string, List<string>>)>();
            foreach (var (modDirectory, settings) in all)
            {
                var (enabled, priority, selections, _, _) = settings;
                result[modDirectory] = (enabled, priority, selections);
            }
            return result;
        }
        catch (Exception ex) { Plugin.Log.Warning($"[PoseKit] GetAllModSettings IPC call failed: {ex}"); return null; }
    }

    /// Replaces every group's selection at once, so pass all of them. Use the mod's current priority.
    public bool TrySetTemporarySettings(Guid collectionId, string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> allGroupSelections)
    {
        try
        {
            var ec = setTemporaryModSettings.Invoke(collectionId, modDirectory, inherit: false, enabled: enabled,
                priority: priority, settings: allGroupSelections, source: Source);
            if (ec == PenumbraApiEc.Success) touchedModDirectories.Add(modDirectory);
            return ec == PenumbraApiEc.Success;
        }
        catch { return false; }
    }

    public bool TryRemoveTemporarySettings(Guid collectionId, string modDirectory)
    {
        try { return removeTemporaryModSettings.Invoke(collectionId, modDirectory) == PenumbraApiEc.Success; }
        catch { return false; }
    }

    /// Whether PoseKit has temporary settings on this mod since the last reset.
    public bool HasTemporarySettings(string modDirectory) => touchedModDirectories.Contains(modDirectory);

    /// Undoes PoseKit's temporary settings for one mod. Separate from TryRemoveTemporarySettings,
    /// which ResetAllTemporarySettings calls while iterating the tracked set.
    public bool TryResetTemporarySettings(Guid collectionId, string modDirectory)
    {
        if (!TryRemoveTemporarySettings(collectionId, modDirectory)) return false;
        touchedModDirectories.Remove(modDirectory);
        return true;
    }

    /// Undoes every temporary setting PoseKit applied this session. Best effort: failures aren't retried.
    public void ResetAllTemporarySettings()
    {
        if (touchedModDirectories.Count == 0) return;

        if (TryGetLocalPlayerCollectionId() is { } collectionId)
            foreach (var modDirectory in touchedModDirectories)
                TryRemoveTemporarySettings(collectionId, modDirectory);

        touchedModDirectories.Clear();
        TryRedrawLocalPlayer();
    }

    /// A settings change isn't picked up by an already-drawn character until it's redrawn.
    public void TryRedrawLocalPlayer()
    {
        try { redrawObject.Invoke(0); }
        catch { /* best-effort */ }
    }
}
