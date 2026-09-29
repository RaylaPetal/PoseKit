namespace PoseKit.Presets;

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Furniture;

/// <summary>
/// Where the player was relative to a furniture item when the preset was saved, so it replays
/// against any placed copy of that item. Corrects the render offset only.
/// </summary>
public class FurnitureAnchor
{
    /// The furniture's sheet row id, shared by every placed copy (unlike its entity id).
    public uint EntryId;

    public string FurnitureName = "";

    /// The player's position at save time, in the furniture's local frame.
    public Vector3 RelativePosition;

    /// The player's rotation at save time, relative to the furniture's rotation. Radians.
    public float RelativeRotation;

    private const float MaxCorrectionDistance = 15f;

    public static FurnitureAnchor Capture(IPlayerCharacter localPlayer, NearbyFurniture furniture)
    {
        var inverseFurnitureFacing = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(furniture.Rotation, 0, 0));
        return new FurnitureAnchor
        {
            EntryId = furniture.EntryId,
            FurnitureName = furniture.Name,
            RelativePosition = Vector3.Transform(localPlayer.Position - furniture.Position, inverseFurnitureFacing),
            RelativeRotation = MathF.IEEERemainder(localPlayer.Rotation - furniture.Rotation, MathF.Tau),
        };
    }

    /// The nearest matching furniture, or null if none is nearby.
    public NearbyFurniture? TryFindLiveInstance(IEnumerable<NearbyFurniture> nearby, Vector3 nearPosition)
    {
        NearbyFurniture? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var candidate in nearby)
        {
            if (candidate.EntryId != EntryId) continue;
            var distance = Vector3.Distance(candidate.Position, nearPosition);
            if (distance >= nearestDistance) continue;
            nearest = candidate;
            nearestDistance = distance;
        }
        return nearest;
    }

    /// Null when the target is too far away. See LocationAnchor.TryComputeCorrection for the
    /// parameters.
    public PoseOffset? TryComputeCorrection(IPlayerCharacter localPlayer, NearbyFurniture liveFurniture, float baseRotationOffset, bool rotationOffsetApplies)
    {
        var furnitureFacing = Quaternion.CreateFromYawPitchRoll(liveFurniture.Rotation, 0, 0);
        var targetPosition = liveFurniture.Position + Vector3.Transform(RelativePosition, furnitureFacing);
        var targetRotation = liveFurniture.Rotation + RelativeRotation;

        var worldDelta = targetPosition - localPlayer.Position;
        if (worldDelta.Length() > MaxCorrectionDistance) return null;

        var rotationCorrection = rotationOffsetApplies
            ? MathF.IEEERemainder(targetRotation - localPlayer.Rotation, MathF.Tau)
            : 0f;

        var finalFacing = localPlayer.Rotation + (rotationOffsetApplies ? baseRotationOffset : 0f) + rotationCorrection;
        var inverseFacing = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(finalFacing, 0, 0));
        var localCorrection = Vector3.Transform(worldDelta, inverseFacing);

        return new PoseOffset { Position = localCorrection, Rotation = rotationCorrection };
    }
}
