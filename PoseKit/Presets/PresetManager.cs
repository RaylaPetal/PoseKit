namespace PoseKit.Presets;

using System.Collections.Generic;

/// <summary>Save/load/lookup for named offset presets, keyed to the current local player's identity
/// (home world + name) via Configuration.GetOrCreateCharacterConfig.</summary>
public sealed class PresetManager(Configuration configuration)
{
    private CharacterPoseConfig? CurrentCharacterConfig()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return null;
        return configuration.GetOrCreateCharacterConfig(localPlayer.HomeWorld.RowId, localPlayer.Name.TextValue);
    }

    public IReadOnlyList<NamedPose> Presets => CurrentCharacterConfig()?.Presets ?? (IReadOnlyList<NamedPose>)System.Array.Empty<NamedPose>();

    public NamedPose? Save(string name, PoseIdentifier pose, PoseOffset offset, PenumbraLink? penumbra = null,
        LocationAnchor? anchor = null)
    {
        var config = CurrentCharacterConfig();
        if (config == null) return null;

        var namedPose = new NamedPose { Name = name, Pose = pose, Offset = offset, Penumbra = penumbra, Anchor = anchor };
        config.Presets.Add(namedPose);
        configuration.Save();
        return namedPose;
    }

    /// Overwrites an existing preset's offset in place (e.g. after loading it and adjusting the
    /// live-offset drag fields) rather than creating a duplicate.
    public void Update(NamedPose existing, PoseOffset offset)
    {
        existing.Offset = offset;
        configuration.Save();
    }

    public void Delete(NamedPose pose)
    {
        CurrentCharacterConfig()?.Presets.Remove(pose);
        configuration.Save();
    }
}
