using System;
using System.Collections.Generic;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace PoseKit.Penumbra;

/// <summary>
/// Thin wrapper over the Penumbra.Api IPC subscribers needed for pose discovery and settings
/// replication, referencing the published Penumbra.Api NuGet package (not the Penumbra.Api submodule
/// checked into this repo, which is reference-only material and is never built — see PoseKit.csproj).
/// Every call is guarded so Feature 3 degrades gracefully when Penumbra isn't installed/running,
/// per the design doc's "hard dependency ... must degrade gracefully" note.
///
/// Writes go through the *temporary* settings IPC (SetTemporaryModSettings), not the permanent one
/// (TrySetMod/TrySetModSetting) — mirroring Synastry-main/EmoteLink/PenumbraService.cs. Permanent
/// writes are meant for Penumbra's own UI; a third-party plugin flipping a mod's saved config
/// permanently every time someone clicks Play is both surprising and not what "temporarily preview
/// a gesture" should do. Temporary overrides are scoped to PoseKit's own IPC session/source tag and
/// don't require the same trust posture as permanent writes.
/// </summary>
public sealed class PenumbraIpc
{
    private const string Source = "PoseKit";

    private readonly GetModList getModList = new(Plugin.PluginInterface);
    private readonly GetModPath getModPath = new(Plugin.PluginInterface);
    private readonly GetModDirectory getModDirectory = new(Plugin.PluginInterface);
    private readonly GetCollectionForObject getCollectionForObject = new(Plugin.PluginInterface);
    private readonly GetCurrentModSettings getCurrentModSettings = new(Plugin.PluginInterface);
    private readonly SetTemporaryModSettings setTemporaryModSettings = new(Plugin.PluginInterface);
    private readonly RemoveTemporaryModSettings removeTemporaryModSettings = new(Plugin.PluginInterface);
    private readonly RedrawObject redrawObject = new(Plugin.PluginInterface);

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
        catch { return null; }
    }

    public string? TryGetModDirectory()
    {
        try { return getModDirectory.Invoke(); }
        catch { return null; }
    }

    /// The mod's position in Penumbra's user-organized sort-folder tree (e.g.
    /// "Animations/Idles/[JxT] Gyaru") — confirmed against a real Penumbra install
    /// (~/.xlcore/pluginConfigs/Penumbra/sort_order.json.bak), distinct from its on-disk directory
    /// name. Null if the mod isn't in the sort order or the call failed.
    public string? TryGetModPath(string modDirectory, string modName)
    {
        try
        {
            var (ec, fullPath, _, _) = getModPath.Invoke(modDirectory, modName);
            return ec == PenumbraApiEc.Success ? fullPath : null;
        }
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

    /// Whether the mod is enabled in this collection, and its current per-group option selections
    /// (group name -> selected option names). Reflects the *effective* settings, including any
    /// temporary override already active — permanent or temporary, this is the read side either way.
    public (bool Enabled, Dictionary<string, List<string>>? Selections) TryGetCurrentSettings(Guid collectionId, string modDirectory)
    {
        try
        {
            var (_, settings) = getCurrentModSettings.Invoke(collectionId, modDirectory);
            if (settings is not { } s) return (false, null);
            var (enabled, _, selections, _) = s;
            return (enabled, selections);
        }
        catch { return (false, null); }
    }

    /// Replaces this mod's *entire* set of group selections with a temporary override (Penumbra's
    /// temporary-settings API is all-or-nothing per mod, not per-group) — callers must pass every
    /// group's selection, not just the one that changed.
    public bool TrySetTemporarySettings(Guid collectionId, string modDirectory, bool enabled,
        IReadOnlyDictionary<string, IReadOnlyList<string>> allGroupSelections)
    {
        try
        {
            var ec = setTemporaryModSettings.Invoke(collectionId, modDirectory, inherit: false, enabled: enabled,
                priority: 0, settings: allGroupSelections, source: Source);
            return ec == PenumbraApiEc.Success;
        }
        catch { return false; }
    }

    public bool TryRemoveTemporarySettings(Guid collectionId, string modDirectory)
    {
        try { return removeTemporaryModSettings.Invoke(collectionId, modDirectory) == PenumbraApiEc.Success; }
        catch { return false; }
    }

    /// A mod-setting change doesn't necessarily re-resolve files on an already-drawn character —
    /// forcing a redraw is what actually makes the new redirect visible if the character is already
    /// mid-pose (mirrors Synastry-main/EmoteLink/Plugin.cs's "/penumbra redraw self" for the same reason).
    public void TryRedrawLocalPlayer()
    {
        try { redrawObject.Invoke(0); }
        catch { /* best-effort */ }
    }
}
