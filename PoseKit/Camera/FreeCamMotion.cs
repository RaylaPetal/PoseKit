using System;
using System.Numerics;

namespace PoseKit.Camera;

/// <summary>Camera-only math. Never reads or writes an actor.</summary>
internal sealed class FreeCamMotion
{
    internal const float Speed = 20f;
    // Up/down is much easier to overshoot than horizontal travel, so it runs slower.
    private const float VerticalScale = 0.3f;
    private const float EaseHalfLifeSeconds = 0.02f;
    public Vector3 Position { get; private set; }

    /// <summary>The drawn position, eased toward <see cref="Position"/> to hide per-frame stepping.</summary>
    public Vector3 RenderPosition { get; private set; }

    public void Initialize(Vector3 position)
    {
        if (!IsFinite(position))
            throw new ArgumentException("The current camera view is invalid.");
        Position = position;
        RenderPosition = position;
    }

    // Frame-time-based exponential approach: frame-rate independent and never overshoots.
    public void Ease(float seconds)
    {
        if (!float.IsFinite(seconds)) return;
        var t = 1f - MathF.Pow(0.5f, Math.Clamp(seconds, 0f, 0.05f) / EaseHalfLifeSeconds);
        RenderPosition = Vector3.Lerp(RenderPosition, Position, t);
    }

    // Uses the camera's live rotation so the game's own mouse-look keeps working.
    public void Step(Vector3 input, float hRotation, float vRotation, float seconds)
    {
        if (input == Vector3.Zero || !IsFinite(input) || !float.IsFinite(hRotation) || !float.IsFinite(vRotation) || !float.IsFinite(seconds))
            return;
        var forward = Forward(hRotation, vRotation);
        var right = new Vector3(MathF.Cos(hRotation), 0, MathF.Sin(hRotation));
        var planar = right * input.X + forward * input.Z;
        if (planar.LengthSquared() > 1f) planar = Vector3.Normalize(planar);
        // Vertical is added after normalizing so its slower speed is not diluted by diagonal travel.
        var direction = planar + Vector3.UnitY * (input.Y * VerticalScale);
        Position += direction * (Speed * Math.Clamp(seconds, 0f, 0.05f));
    }

    public static Vector3 Forward(float hRotation, float vRotation) =>
        new(MathF.Sin(hRotation) * MathF.Cos(vRotation), MathF.Sin(vRotation), -MathF.Cos(hRotation) * MathF.Cos(vRotation));

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
