namespace PoseKit.Presets;

using System.Collections.Generic;
using PoseKit;

/// <summary>Which Penumbra mod (and exact group selections) a preset was captured from, if any —
/// lets replaying the preset re-enable that mod/option automatically instead of just applying the
/// offset against whatever happens to be active at the time.</summary>
public class PenumbraLink
{
    public string ModDirectory = "";
    public Dictionary<string, List<string>> GroupSelections = new();

    /// Display-only — captured at save time purely so the preset library can show "which
    /// animation this plays" without needing a live Penumbra round-trip (and staying correct even
    /// if the mod's since been renamed, moved, or uninstalled). Never used for the actual replay,
    /// which goes through ModDirectory/GroupSelections instead.
    public string ModName = "";

    /// The specific option that was playing when this preset was captured — "Default" for
    /// PenumbraPoseScanner's synthetic default_mod.json group, since there's nothing more specific
    /// to name there.
    public string OptionName = "";
}

public class NamedPose
{
    public string Name = "";
    public PoseIdentifier Pose;
    public PoseOffset Offset;
    public PenumbraLink? Penumbra;

    /// Where the player was standing when this preset was saved, if the user opted in — lets
    /// replaying it fold a correction into the offset instead of only looking right from the
    /// exact same spot. Null (the default, including for every preset saved before this existed)
    /// means "not anchored," which is unaffected. See LocationAnchor.
    public LocationAnchor? Anchor;
}
