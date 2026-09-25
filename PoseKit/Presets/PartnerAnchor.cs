namespace PoseKit.Presets;

using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Pairing;

/// <summary>
/// Optional per-preset snapshot of where the player stood relative to a paired partner when a couple
/// preset was saved — the partner is the root on replay: they stay put, and only the side playing the
/// preset folds a correction into its own render-only offset to land back in the same spot around
/// them. Same math as FurnitureAnchor, with the partner's live transform standing in for the
/// furniture's, and the same "read-only, render-offset-only" boundary — never touches
/// GameObject.Position/Rotation. Captured and resolved against the partner's actual (server-side)
/// transform, never their render offset, which this client can't see anyway.
///
/// Carries its own PartnerIdentity rather than relying on NamedPose.PartnerHalf.Partner, so it still
/// resolves when played unpaired/under solo play, or when the partner half capture timed out.
/// </summary>
public class PartnerAnchor
{
    public PartnerIdentity Partner;

    /// The player's position at save time, in the partner's local frame (same convention as
    /// FurnitureAnchor.RelativePosition).
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

    /// The anchored partner's character if it's currently loaded nearby, matched by name + home world
    /// (PartnerIdentity.Matches), or null.
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

    /// Null if the correction isn't meaningful right now (the computed target is implausibly far
    /// away) — mirrors FurnitureAnchor.TryComputeCorrection's contract and math exactly, just against
    /// the partner's live transform. See LocationAnchor.TryComputeCorrection's doc for why
    /// baseRotationOffset and rotationOffsetApplies are needed.
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
