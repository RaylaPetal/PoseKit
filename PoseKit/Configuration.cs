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
