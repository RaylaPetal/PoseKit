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
}

public class NamedPose
{
    public string Name = "";
    public PoseIdentifier Pose;
    public PoseOffset Offset;
    public PenumbraLink? Penumbra;
}
