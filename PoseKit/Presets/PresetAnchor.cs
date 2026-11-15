namespace PoseKit.Presets;

using System;
using Newtonsoft.Json;

/// <summary>
/// A preset's anchor: a world spot, a furniture item, or a partner. At most one is set. Kept as
/// plain data so it serializes with the plugin config.
/// </summary>
public sealed class PresetAnchor
{
    // [JsonProperty] lets the config's Newtonsoft deserializer use the private setters. Without it every anchor loaded
    // as empty, and the next save wrote the empties back over the saved anchor.
    [JsonProperty] public LocationAnchor? Spot { get; private set; }
    [JsonProperty] public FurnitureAnchor? Furniture { get; private set; }
    [JsonProperty] public PartnerAnchor? Partner { get; private set; }

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
    [JsonIgnore] public bool IsSet => Spot != null || Furniture != null || Partner != null;
}
