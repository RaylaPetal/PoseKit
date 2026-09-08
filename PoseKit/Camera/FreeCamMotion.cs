using System;
using System.Numerics;

namespace PoseKit.Camera;

/// <summary>Camera-only math. Never reads or writes an actor.</summary>
internal sealed class FreeCamMotion
{
    internal const float Speed = 20f;
    public Vector3 Position { get; private set; }

    public void Initialize(Vector3 position)
    {
        if (!IsFinite(position))
            throw new ArgumentException("The current camera view is invalid.");
        Position = position;
    }

    // Direction comes from the camera's own live rotation (driven by the game's normal
    // right-click-drag, which freecam no longer blocks) rather than a self-tracked
    // yaw/pitch, so native camera-look keeps working while position is free-fly.
    public void Step(Vector3 input, float hRotation, float vRotation, float seconds)
    {
        if (input == Vector3.Zero || !IsFinite(input) || !float.IsFinite(hRotation) || !float.IsFinite(vRotation) || !float.IsFinite(seconds))
            return;
        var forward = Forward(hRotation, vRotation);
        var right = new Vector3(MathF.Cos(hRotation), 0, MathF.Sin(hRotation));
        var direction = right * input.X + Vector3.UnitY * input.Y + forward * input.Z;
        // Normalize in world space: forward and world-up are not orthogonal when pitched.
        if (direction.LengthSquared() > 1f) direction = Vector3.Normalize(direction);
        Position += direction * (Speed * Math.Clamp(seconds, 0f, 0.05f));
    }

    public static Vector3 Forward(float hRotation, float vRotation) =>
        new(MathF.Sin(hRotation) * MathF.Cos(vRotation), MathF.Sin(vRotation), -MathF.Cos(hRotation) * MathF.Cos(vRotation));

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
