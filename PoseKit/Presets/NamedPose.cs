namespace PoseKit.Presets;

using System.Collections.Generic;
using PoseKit;
using PoseKit.Pairing;

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

    /// The group OptionName belongs to, or "" for an implicit (no real Penumbra group) mod — needed
    /// separately from GroupSelections (which snapshots every group of the mod, for local replay)
    /// whenever only the *one* relevant group/option pair matters, e.g. syncing a couple preset to a
    /// partner without also sending every other group's selection.
    public string GroupName = "";
}

public class NamedPose
{
    public string Name = "";
    public PoseIdentifier Pose;
    public PoseOffset Offset;
    public PenumbraLink? Penumbra;

    /// Where the player was standing (or which furniture they were near) when this preset was
    /// saved, if the user opted in — lets replaying it fold a correction into the offset instead of
    /// only looking right from the exact same spot/furniture instance. Null (the default, including
    /// for every preset saved before this existed) means "not anchored," which is unaffected. See
    /// PresetAnchor.
    public PresetAnchor? Anchor;

    /// The paired partner's own pose/offset/anchor/mod state, captured (via a request/reply over
    /// /tell) at the moment this preset was saved with "include partner" enabled — null (the
    /// default, including for every preset saved before this existed, or saved without that option)
    /// means this is an ordinary, saver-only preset. Stored only here, never written to the
    /// partner's own config — see couple-preset-relay's spec for why: the old model (partner
    /// auto-saves its own matching copy) is what let the two sides silently diverge.
    public PartnerHalf? PartnerHalf;
}

/// <summary>A partner's own pose/offset/anchor/mod state captured into one side's preset, plus who it
/// was captured from — see NamedPose.PartnerHalf. Playing a preset with this set relays it back to
/// that exact partner instead of the ordinary queue-and-match flow (see couple-preset-relay).</summary>
public class PartnerHalf
{
    public PartnerIdentity Partner;
    public PoseIdentifier Pose;
    public PoseOffset Offset;
    public PenumbraLink? Penumbra;
    public PresetAnchor? Anchor;
}
