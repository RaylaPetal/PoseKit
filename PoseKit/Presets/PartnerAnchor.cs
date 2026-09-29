namespace PoseKit.Presets;

using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Pairing;

/// <summary>
/// Where the player stood relative to their partner when the preset was saved. On replay the partner
/// stays put and only this side corrects its render offset. Same math as FurnitureAnchor, using the
/// partner's real (not rendered) transform.
///
/// Stores its own PartnerIdentity so it still works unpaired or without a partner half.
/// </summary>
public class PartnerAnchor
{
    public PartnerIdentity Partner;

    /// The player's position at save time, in the partner's local frame.
    public Vector3 RelativePosition;

    /// The player's rotation at save time, relative to the partner's rotation. Radians.
    public float RelativeRotation;

    // Same render-only correction budget as LocationAnchor/FurnitureAnchor.
    private const float MaxCorrectionDistance = 15f;

    public static PartnerAnchor Capture(IPlayerCharacter localPlayer, IPlayerCharacter partner, PartnerIdentity identity)
    {
        var inversePartnerFacing = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(partner.Rotation, 0, 0));
        return new PartnerAnchor
        {
            Partner = identity,
            RelativePosition = Vector3.Transform(localPlayer.Position - partner.Position, inversePartnerFacing),
            RelativeRotation = MathF.IEEERemainder(localPlayer.Rotation - partner.Rotation, MathF.Tau),
        };
    }

    /// The partner's character if loaded nearby, or null.
    public static IPlayerCharacter? TryFindLive(PartnerIdentity identity)
    {
        foreach (var gameObject in Plugin.ObjectTable)
        {
            if (gameObject is not IPlayerCharacter character) continue;
            if (identity.Matches(character.Name.TextValue, character.HomeWorld.Value.Name.ExtractText()))
                return character;
        }
        return null;
    }

    /// Null when the target is too far away. See LocationAnchor.TryComputeCorrection for the
    /// parameters.
    public PoseOffset? TryComputeCorrection(IPlayerCharacter localPlayer, IPlayerCharacter livePartner, float baseRotationOffset, bool rotationOffsetApplies)
    {
        var partnerFacing = Quaternion.CreateFromYawPitchRoll(livePartner.Rotation, 0, 0);
        var targetPosition = livePartner.Position + Vector3.Transform(RelativePosition, partnerFacing);
        var targetRotation = livePartner.Rotation + RelativeRotation;

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
