namespace PoseKit.Presets;

using System.Collections.Generic;
using PoseKit;
using PoseKit.Pairing;

/// <summary>The Penumbra mod and option selections a preset was captured with, so replaying it can
/// re-enable them.</summary>
public class PenumbraLink
{
    public string ModDirectory = "";
    public Dictionary<string, List<string>> GroupSelections = new();

    /// Display only; replay uses ModDirectory and GroupSelections.
    public string ModName = "";

    /// The option that was playing; "Default" for a mod's default files.
    public string OptionName = "";

    /// The group OptionName belongs to, or "" for a mod without groups.
    public string GroupName = "";
}

public class NamedPose
{
    public string Name = "";
    public PoseIdentifier Pose;
    public PoseOffset Offset;
    public PenumbraLink? Penumbra;

    /// Null when the preset isn't anchored.
    public PresetAnchor? Anchor;

    /// The partner's half of a couple preset; null for a solo preset. Only stored on the saving
    /// side, so the two halves can't drift apart.
    public PartnerHalf? PartnerHalf;
}

/// <summary>A partner's pose state captured into a couple preset, plus who it came from. Playing
/// the preset relays this half to that partner.</summary>
public class PartnerHalf
{
    public PartnerIdentity Partner;
    public PoseIdentifier Pose;
    public PoseOffset Offset;
    public PenumbraLink? Penumbra;
    public PresetAnchor? Anchor;
}
