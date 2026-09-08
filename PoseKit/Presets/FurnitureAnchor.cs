namespace PoseKit.Presets;

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Furniture;

/// <summary>
/// Optional per-preset snapshot of a furniture item the player was near when it was saved, stored as
/// the player's own position/rotation relative to that item — not an absolute world point — so
/// replaying it against the same furniture placed somewhere else (a different room, a different
/// copy of the item) still folds a correct correction into the offset. Same "read-only,
/// render-offset-only" boundary as LocationAnchor; never touches GameObject.Position/Rotation.
///
/// Works against NearbyFurniture snapshots rather than a live IGameObject: IGameObject wraps a
/// pointer only valid for the current frame, and the furniture picked at save time (or matched at
/// replay time) is read from a FurnitureScanner.ScanNearby() snapshot, not held across frames.
/// </summary>
public class FurnitureAnchor
{
    /// The stable game-data identifier for the furniture item (NearbyFurniture.EntryId — a
    /// HousingFurniture/HousingYardObject sheet row id, read via HousingObjectId on the housing
    /// object array; see FurnitureScanner), not a per-session entity ID — entity IDs aren't stable
    /// across sessions or rooms, but every placed copy of the same furniture item shares this.
    public uint EntryId;

    /// Display-only, resolved at save time purely so the preset library can show which furniture
    /// this is anchored to without needing a live lookup.
    public string FurnitureName = "";

    /// The player's position at save time, expressed relative to the furniture's own position and
    /// rotation (i.e. in the furniture's local frame) — this is what lets the same relative offset
    /// be re-applied against a differently-placed copy of the same item.
    public Vector3 RelativePosition;

    /// The player's rotation at save time, relative to the furniture's rotation. Radians.
    public float RelativeRotation;

    // Same render-only correction budget as LocationAnchor — beyond this the rendered model would
    // visibly desync from the character's real hitbox/camera/nameplate.
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

    /// Picks the nearest currently-live furniture instance matching this anchor's EntryId, or null if
    /// none is nearby — callers should fall back to the preset's plain saved offset and warn the user.
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

    /// Null if the correction isn't meaningful right now (the computed target is implausibly far
    /// away) — mirrors LocationAnchor.TryComputeCorrection's contract and math exactly, just against
    /// a live-computed target instead of a frozen point. See that method's doc for why
    /// baseRotationOffset is needed.
    public PoseOffset? TryComputeCorrection(IPlayerCharacter localPlayer, NearbyFurniture liveFurniture, float baseRotationOffset)
    {
        var furnitureFacing = Quaternion.CreateFromYawPitchRoll(liveFurniture.Rotation, 0, 0);
        var targetPosition = liveFurniture.Position + Vector3.Transform(RelativePosition, furnitureFacing);
        var targetRotation = liveFurniture.Rotation + RelativeRotation;

        var worldDelta = targetPosition - localPlayer.Position;
        if (worldDelta.Length() > MaxCorrectionDistance) return null;

        var rotationCorrection = MathF.IEEERemainder(targetRotation - localPlayer.Rotation, MathF.Tau);

        var finalFacing = localPlayer.Rotation + baseRotationOffset + rotationCorrection;
        var inverseFacing = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(finalFacing, 0, 0));
        var localCorrection = Vector3.Transform(worldDelta, inverseFacing);

        return new PoseOffset { Position = localCorrection, Rotation = rotationCorrection };
    }
}
