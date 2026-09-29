namespace PoseKit.Presets;

using System;

/// <summary>
/// A preset's anchor: a world spot, a furniture item, or a partner. At most one is set. Kept as
/// plain data so it serializes with the plugin config.
/// </summary>
public sealed class PresetAnchor
{
    public LocationAnchor? Spot { get; private set; }
    public FurnitureAnchor? Furniture { get; private set; }
    public PartnerAnchor? Partner { get; private set; }

    /// For JSON deserialization only; use the From* factories.
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

    /// A loaded anchor can have every kind null, which means no anchor.
    public bool IsSet => Spot != null || Furniture != null || Partner != null;
}
