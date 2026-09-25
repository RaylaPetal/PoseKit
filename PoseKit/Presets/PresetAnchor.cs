namespace PoseKit.Presets;

using System;

/// <summary>
/// A preset's anchor choice: none, a fixed world spot, a furniture item, or a paired partner —
/// structurally at most one of them, rather than independent nullable fields on NamedPose that UI
/// convention alone would have to keep from being set together. Plain data (no polymorphism) so it
/// round-trips through Dalamud's plugin-config JSON serialization the same way every other preset
/// field already does.
/// </summary>
public sealed class PresetAnchor
{
    public LocationAnchor? Spot { get; private set; }
    public FurnitureAnchor? Furniture { get; private set; }
    public PartnerAnchor? Partner { get; private set; }

    /// Parameterless constructor kept for JSON deserialization only — use FromSpot/FromFurniture/
    /// FromPartner to construct one with the exclusivity guarantee intact.
    public PresetAnchor() { }

    private PresetAnchor(LocationAnchor? spot, FurnitureAnchor? furniture, PartnerAnchor? partner)
    {
        Spot = spot;
        Furniture = furniture;
        Partner = partner;
    }

    public static PresetAnchor FromSpot(LocationAnchor spot) => new(spot, null, null);
    public static PresetAnchor FromFurniture(FurnitureAnchor furniture) => new(null, furniture, null);
    public static PresetAnchor FromPartner(PartnerAnchor partner) => new(null, null, partner);

    /// True when the values loaded (e.g. from JSON) actually leave this anchor meaningful — a
    /// preset's Anchor field can be non-null with every kind null, which is equivalent to no anchor
    /// at all.
    public bool IsSet => Spot != null || Furniture != null || Partner != null;
}
