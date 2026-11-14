using System;
using System.Numerics;

namespace PoseKit.Camera;

/// <summary>Camera-only math. Never reads or writes an actor.</summary>
internal sealed class FreeCamMotion
{
    internal const float Speed = 14f;
    // Up/down is much easier to overshoot than horizontal travel, so it runs slower.
    private const float VerticalScale = 0.3f;
    // Four half-lives reach about 94%: roughly 0.2 s to cruise and 0.3 s to rest.
    private const float StartHalfLifeSeconds = 0.05f;
    private const float StopHalfLifeSeconds = 0.07f;
    private const float MaxStepSeconds = 0.05f;
    // A stopping camera below this speed is set at rest, cutting the exponential's long creep;
    // 2.5% of cruising speed is too slow to see the snap.
    private const float RestSpeed = 0.5f;

    public Vector3 Position { get; private set; }
    public Vector3 Velocity { get; private set; }

    public void Initialize(Vector3 position)
    {
        if (!IsFinite(position))
            throw new ArgumentException("The current camera view is invalid.");
        Position = position;
        Velocity = Vector3.Zero;
    }

    // Uses the camera's live rotation so the game's own mouse-look keeps working. Velocity approaches
    // the input's target exponentially: frame-rate independent, eased in and out, never overshooting.
    public void Step(Vector3 input, float hRotation, float vRotation, float seconds)
    {
        if (!IsFinite(input) || !float.IsFinite(hRotation) || !float.IsFinite(vRotation) || !float.IsFinite(seconds))
            return;
        var dt = Math.Clamp(seconds, 0f, MaxStepSeconds);
        var target = Target(input, hRotation, vRotation);
        var halfLife = target == Vector3.Zero ? StopHalfLifeSeconds : StartHalfLifeSeconds;
        var velocity = Vector3.Lerp(Velocity, target, 1f - MathF.Pow(0.5f, dt / halfLife));
        if (target == Vector3.Zero && velocity.LengthSquared() < RestSpeed * RestSpeed) velocity = Vector3.Zero;
        Velocity = velocity;
        Position += Velocity * dt;
    }

    private static Vector3 Target(Vector3 input, float hRotation, float vRotation)
    {
        if (input == Vector3.Zero) return Vector3.Zero;
        var forward = Forward(hRotation, vRotation);
        var right = new Vector3(MathF.Cos(hRotation), 0, MathF.Sin(hRotation));
        var planar = right * input.X + forward * input.Z;
        if (planar.LengthSquared() > 1f) planar = Vector3.Normalize(planar);
        // Vertical is added after normalizing so its slower speed is not diluted by diagonal travel.
        return (planar + Vector3.UnitY * (input.Y * VerticalScale)) * Speed;
    }

    public static Vector3 Forward(float hRotation, float vRotation) =>
        new(MathF.Sin(hRotation) * MathF.Cos(vRotation), MathF.Sin(vRotation), -MathF.Cos(hRotation) * MathF.Cos(vRotation));

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
