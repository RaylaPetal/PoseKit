namespace PoseKit.Presets;

using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

/// <summary>
/// Optional per-preset snapshot of where the player was standing when it was saved, so replaying
/// it somewhere else can fold a correction into the existing render-only draw-offset instead of
/// looking wrong until manually readjusted. Never touches the server-authoritative
/// GameObject.Position/Rotation — same "read-only, offset-only" boundary OffsetEngine already
/// holds for the pose offset itself.
/// </summary>
public class LocationAnchor
{
    public uint TerritoryType;
    public Vector3 Position;
    public float Rotation; // radians, same convention as GameObject.Rotation / PoseOffset.Rotation

    // Draw-offset is a small render-only nudge; beyond this the rendered model would visibly
    // desync from the character's real hitbox/camera/nameplate. Starting guess — may need tuning
    // once tested live, since it can't be verified without a running game client.
    private const float MaxCorrectionDistance = 15f;

    public static LocationAnchor Capture(IPlayerCharacter localPlayer, uint territoryType) => new()
    {
        TerritoryType = territoryType,
        Position = localPlayer.Position,
        Rotation = localPlayer.Rotation,
    };

    /// Human-readable zone name for display (e.g. preset library entries) — resolved on demand
    /// via Lumina rather than stored, so it stays correct even across game data updates. Falls
    /// back to the raw territory ID if the sheet lookup fails for any reason.
    public string ZoneName
    {
        get
        {
            var territory = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(TerritoryType);
            var placeName = territory?.PlaceName;
            return placeName is { IsValid: true } ? placeName.Value.Value.Name.ExtractText() : $"Zone #{TerritoryType}";
        }
    }

    /// Null if the correction isn't meaningful right now (different zone, or too far away) —
    /// callers should fall back to the preset's plain saved offset and warn the user.
    /// <param name="baseRotationOffset">The pose's own saved PoseOffset.Rotation (before this
    /// correction is added) — needed because the engine renders the position offset relative to
    /// the character's FINAL facing (native rotation plus whatever rotation offset is also being
    /// applied), not their pre-offset facing. Mirrors SimpleHeels-master/GizmoOverlay.cs's own
    /// WorldToLocal call, which builds its rotation from `character->Rotation + target.R` (the
    /// offset's own existing rotation value), not raw character rotation alone.</param>
    /// <param name="rotationOffsetApplies">OffsetEngine.RotationHookResolved — whether a rotation
    /// offset actually reaches the model at all. When the rotation hook didn't resolve (a known
    /// possibility per OffsetEngine's own doc), the game keeps rendering at the character's plain
    /// native rotation no matter what baseRotationOffset/this correction's own rotation say —
    /// folding either into the facing used for the POSITION transform then rotates the position
    /// correction by an angle the model was never actually turned by, which gets worse the further
    /// the saved facing is from the current one (can look fully mirrored around 180°). Skipping
    /// both terms when the hook is unresolved keeps the position correction in the frame that's
    /// actually rendered.</param>
    public PoseOffset? TryComputeCorrection(IPlayerCharacter localPlayer, uint currentTerritoryType, float baseRotationOffset, bool rotationOffsetApplies)
    {
        if (currentTerritoryType != TerritoryType) return null;

        var worldDelta = Position - localPlayer.Position;
        if (worldDelta.Length() > MaxCorrectionDistance) return null;

        var rotationCorrection = rotationOffsetApplies
            ? MathF.IEEERemainder(Rotation - localPlayer.Rotation, MathF.Tau)
            : 0f;

        // Same WorldToLocal pattern as SimpleHeels-master/GizmoOverlay.cs: inverse-rotate the
        // world-space delta by the character's FINAL facing (native + base offset + this
        // correction), since that's the frame the engine applies DesiredOffset.Position in.
        var finalFacing = localPlayer.Rotation + (rotationOffsetApplies ? baseRotationOffset : 0f) + rotationCorrection;
        var inverseFacing = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(finalFacing, 0, 0));
        var localCorrection = Vector3.Transform(worldDelta, inverseFacing);

        return new PoseOffset { Position = localCorrection, Rotation = rotationCorrection };
    }
}
