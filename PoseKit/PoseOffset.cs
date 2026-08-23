using System.Numerics;

namespace PoseKit;

/// <summary>Render-only local offset applied on top of a pose's natural draw offset/rotation.</summary>
public struct PoseOffset
{
    public Vector3 Position;

    /// Yaw offset in radians. Experimental — see OffsetEngine for why this hooks a signature with no
    /// FFXIVClientStructs-maintained equivalent to verify against ahead of time.
    public float Rotation;

    public static PoseOffset Zero => new() { Position = Vector3.Zero, Rotation = 0f };
}
