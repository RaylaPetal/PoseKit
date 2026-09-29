namespace PoseKit.Presets;

using System.Collections.Generic;

/// <summary>Saves and loads the current character's presets.</summary>
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
        PresetAnchor? anchor = null, PartnerHalf? partnerHalf = null)
    {
        var config = CurrentCharacterConfig();
        if (config == null) return null;

        var namedPose = new NamedPose
        {
            Name = name, Pose = pose, Offset = offset, Penumbra = penumbra, Anchor = anchor, PartnerHalf = partnerHalf,
        };
        config.Presets.Add(namedPose);
        configuration.Save();
        return namedPose;
    }

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
