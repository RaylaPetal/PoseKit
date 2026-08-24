using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using PoseKit.Presets;

namespace PoseKit;

public class CharacterPoseConfig
{
    public List<NamedPose> Presets = new();
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;

    /// Keyed by home world row ID then character name, mirroring SimpleHeels' WorldCharacterDictionary
    /// identity pattern so alts don't collide.
    public Dictionary<uint, Dictionary<string, CharacterPoseConfig>> Characters { get; set; } = new();

    /// Mod directory names to scan for poses (set in the Settings window) — scanning every installed
    /// mod was slow and mostly irrelevant noise, so this is opt-in per mod rather than automatic.
    public HashSet<string> SelectedPenumbraMods { get; set; } = new();

    /// Restricts the Settings mod picker to mods filed under this Penumbra sort-folder path (e.g.
    /// "Animations") — Penumbra's own UI-organized mod tree, not the on-disk directory. Empty means
    /// no filter (every enabled mod is offered, same as before this existed).
    public string PenumbraFolderFilter { get; set; } = "";

    /// When on and SimpleHeels is loaded, PoseKit registers its offset onto the local player through
    /// SimpleHeels' own IPC instead of applying it directly, so Mare/Snowcloak/etc. — which already
    /// sync SimpleHeels offsets — pick it up automatically. See PoseKit.Sync.SimpleHeelsBridge.
    public bool BridgeOffsetToSimpleHeels { get; set; }

    /// One-shot latch so BridgeOffsetToSimpleHeels only gets auto-enabled the first time SimpleHeels
    /// is ever observed loaded — never again afterward, so a user who turns it back off stays off.
    public bool HasOfferedSimpleHeelsBridge { get; set; }

    public CharacterPoseConfig GetOrCreateCharacterConfig(uint homeWorldId, string characterName)
    {
        if (!Characters.TryGetValue(homeWorldId, out var byName))
        {
            byName = new Dictionary<string, CharacterPoseConfig>();
            Characters[homeWorldId] = byName;
        }

        if (!byName.TryGetValue(characterName, out var config))
        {
            config = new CharacterPoseConfig();
            byName[characterName] = config;
        }

        return config;
    }

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
