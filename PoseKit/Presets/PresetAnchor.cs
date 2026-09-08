namespace PoseKit.Presets;

using System;

/// <summary>
/// A preset's anchor choice: none, a fixed world spot, or a furniture item — structurally at most
/// one of the two, rather than two independent nullable fields on NamedPose that UI convention alone
/// would have to keep from both being set. Plain data (no polymorphism) so it round-trips through
/// Dalamud's plugin-config JSON serialization the same way every other preset field already does.
/// </summary>
public sealed class PresetAnchor
{
    public LocationAnchor? Spot { get; private set; }
    public FurnitureAnchor? Furniture { get; private set; }

    /// Parameterless constructor kept for JSON deserialization only — use FromSpot/FromFurniture to
    /// construct one with the exclusivity guarantee intact.
    public PresetAnchor() { }

    private PresetAnchor(LocationAnchor? spot, FurnitureAnchor? furniture)
    {
        Spot = spot;
        Furniture = furniture;
    }

    public static PresetAnchor FromSpot(LocationAnchor spot) => new(spot, null);
    public static PresetAnchor FromFurniture(FurnitureAnchor furniture) => new(null, furniture);

    /// True when the values loaded (e.g. from JSON) actually leave this anchor meaningful — a
    /// preset's Anchor field can be non-null with both Spot and Furniture null, which is equivalent
    /// to no anchor at all.
    public bool IsSet => Spot != null || Furniture != null;
}
