namespace PoseKit.Presets;

using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

/// <summary>
/// Where the player stood when the preset was saved. Replaying corrects the render offset only; the
/// real position is never changed.
/// </summary>
public class LocationAnchor
{
    public uint TerritoryType;
    public Vector3 Position;
    public float Rotation; // radians

    // Beyond this the rendered model visibly drifts from the real character.
    private const float MaxCorrectionDistance = 15f;

    public static LocationAnchor Capture(IPlayerCharacter localPlayer, uint territoryType) => new()
    {
        TerritoryType = territoryType,
        Position = localPlayer.Position,
        Rotation = localPlayer.Rotation,
    };

    public string ZoneName
    {
        get
        {
            var territory = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(TerritoryType);
            var placeName = territory?.PlaceName;
            return placeName is { IsValid: true } ? placeName.Value.Value.Name.ExtractText() : $"Zone #{TerritoryType}";
        }
    }

    /// Null when in a different zone or too far away.
    /// <param name="baseRotationOffset">The preset's saved rotation offset. The position offset is
    /// applied in the character's final facing, which includes it.</param>
    /// <param name="rotationOffsetApplies">Whether rotation offsets reach the model at all. When
    /// they don't, rotation is left out so the position correction isn't turned by an angle the
    /// model never turned.</param>
    public PoseOffset? TryComputeCorrection(IPlayerCharacter localPlayer, uint currentTerritoryType, float baseRotationOffset, bool rotationOffsetApplies)
    {
        if (currentTerritoryType != TerritoryType) return null;

        var worldDelta = Position - localPlayer.Position;
        if (worldDelta.Length() > MaxCorrectionDistance) return null;

        var rotationCorrection = rotationOffsetApplies
            ? MathF.IEEERemainder(Rotation - localPlayer.Rotation, MathF.Tau)
            : 0f;

        // The engine applies the offset in the character's final facing, so convert into that frame.
        var finalFacing = localPlayer.Rotation + (rotationOffsetApplies ? baseRotationOffset : 0f) + rotationCorrection;
        var inverseFacing = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(finalFacing, 0, 0));
        var localCorrection = Vector3.Transform(worldDelta, inverseFacing);

        return new PoseOffset { Position = localCorrection, Rotation = rotationCorrection };
    }
}
