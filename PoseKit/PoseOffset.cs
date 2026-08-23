using System.Numerics;

namespace PoseKit;

/// <summary>Render-only local offset applied on top of a pose's natural draw offset. Position only for v1 —
/// see OffsetEngine for why a yaw offset was dropped from scope.</summary>
public struct PoseOffset
{
    public Vector3 Position;

    public static PoseOffset Zero => new() { Position = Vector3.Zero };
}
