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

    public bool HasSeenWelcome { get; set; }

    /// The ChangelogWindow.ChangelogId last shown, so each changelog opens once after an update.
    public string LastSeenChangelog { get; set; } = "";

    /// Keyed by home world row ID, then character name.
    public Dictionary<uint, Dictionary<string, CharacterPoseConfig>> Characters { get; set; } = new();

    /// Mod directories to scan for poses.
    public HashSet<string> SelectedPenumbraMods { get; set; } = new();

    /// Starred animations (mod options), as "modDirectory|groupName|optionName". See Library.Favorites.
    public HashSet<string> FavoriteOptions { get; set; } = new();

    /// Penumbra sort-folder the mod picker is limited to; empty means all mods.
    public string PenumbraFolderFilter { get; set; } = "";

    /// Apply the offset through SimpleHeels instead of directly. See SimpleHeelsBridge.
    public bool BridgeOffsetToSimpleHeels { get; set; }

    /// The bridge is auto-enabled only the first time SimpleHeels is seen, so turning it off sticks.
    public bool HasOfferedSimpleHeelsBridge { get; set; }

    public PoseKit.Bones.BodyPart BoneAlignSelf { get; set; } = PoseKit.Bones.BodyPart.Penis;
    public PoseKit.Bones.BodyPart BoneAlignPartner { get; set; } = PoseKit.Bones.BodyPart.Vagina;

    /// Distance short of contact Bone Align stops at, in yalms.
    public float BoneAlignGap { get; set; } = 0.02f;

    /// Align turns this player to face the partner's way first, on the assumption that couple
    /// animations are authored with both characters facing the same direction.
    public bool BoneAlignFaceSameWay { get; set; } = true;

    /// Auto-align remembered animations while paired. Manual aligns are remembered either way.
    public bool AutoAlignFromMemory { get; set; } = true;

    /// The small on-screen button that opens the quick library.
    public bool ShowLibraryChip { get; set; } = true;

    /// Hides the library chip even while paired, for players who only use /posekit lib or the main window.
    public bool NeverShowLibraryChip { get; set; }

    /// Opens the quick library each time the character sits or lies down.
    public bool OpenLibraryWhenSeated { get; set; } = true;

    /// A chat sound when a partner's invite, pick or offer arrives, since chat lines scroll away unnoticed.
    public bool PairingAlertSound { get; set; } = true;

    /// Which of the game's chat sounds (&lt;se.1&gt; to &lt;se.16&gt;) the alert plays.
    public int PairingAlertSoundId { get; set; } = 2;

    /// This install's relay key (P-256, base64url). Plaintext like Oathbound's under Wine: it only authenticates
    /// pairing traffic, and losing it just means pairing by targeting again. See Relay.DeviceIdentity.
    public string RelayDeviceKeyD { get; set; } = "";
    public string RelayDeviceKeyX { get; set; } = "";
    public string RelayDeviceKeyY { get; set; } = "";

    /// Partners paired with before, trusted by device key so pairing again needs no targeting. See Pairing.RememberedPartners.
    public List<Pairing.RememberedPartner> RememberedPartners { get; set; } = new();

    /// Holds the character still during a pose PoseKit started, since one stray movement key ends a
    /// pose that may have taken placing, offsetting and aligning to set up.
    public bool LockMovementWhilePosing { get; set; } = true;

    /// A move/rotate gizmo on the character's root, shown below the live-offset sliders' toggle.
    public bool ShowOffsetGizmo { get; set; }

    public PoseKit.Windows.OffsetGizmoMode OffsetGizmoMode { get; set; } = PoseKit.Windows.OffsetGizmoMode.Move;

    /// Held to rotate with the gizmo without leaving the character for the radio buttons; never
    /// changes OffsetGizmoMode. None turns it off for players who type capitals mid-pose and mind.
    public PoseKit.Windows.GizmoHoldKey OffsetGizmoRotateKey { get; set; } = PoseKit.Windows.GizmoHoldKey.Shift;

    public PoseKit.Library.LibraryTypeFilter LibraryTypeFilter { get; set; } = PoseKit.Library.LibraryTypeFilter.All;

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

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
