namespace PoseKit.Presets;

using System.Globalization;

/// <summary>
/// How a preset is described in the UI, shared by the main window's preset cards and the quick library's chips so they
/// never disagree about what a preset is or where it lands.
/// </summary>
public static class PresetDescriptions
{
    /// The mod a preset plays from, or null for a plain emote or pose.
    public static string? ModName(NamedPose preset) => preset.Penumbra is { ModName.Length: > 0 } link ? link.ModName : null;

    /// "Solo", or "Couple with Name" (who the partner half is relayed to).
    public static string Type(NamedPose preset) =>
        preset.PartnerHalf is { } half ? $"Couple with {half.Partner.Name}" : "Solo";

    /// Where it lands, in a few words.
    public static string Anchor(NamedPose preset) =>
        preset.Anchor?.Spot is { } spot ? $"Spot in {spot.ZoneName}"
        : preset.Anchor?.Furniture is { } furniture ? $"On {Named(furniture.FurnitureName, "furniture")}"
        : preset.Anchor?.Partner is { } partner ? $"Next to {partner.Partner.Name}"
        : "Plays where you stand";

    /// One word for a chip, or null when it isn't anchored.
    public static string? AnchorShort(NamedPose preset) =>
        preset.Anchor?.Spot != null ? "spot"
        : preset.Anchor?.Furniture != null ? "furniture"
        : preset.Anchor?.Partner != null ? "partner"
        : null;

    /// What the anchor does on replay, for a tooltip.
    public static string AnchorDetail(NamedPose preset)
    {
        var ic = CultureInfo.InvariantCulture;
        if (preset.Anchor?.Spot is { } spot)
            return $"Lands you back on the spot where it was saved in {spot.ZoneName}, facing the same way, when you play it " +
                   $"within 15 yalms of there.\nSaved at {spot.Position.X.ToString("0.0", ic)}, {spot.Position.Y.ToString("0.0", ic)}, " +
                   $"{spot.Position.Z.ToString("0.0", ic)}.";
        if (preset.Anchor?.Furniture is { } furniture)
            return $"Lands you in the same place on any {Named(furniture.FurnitureName, "copy of that furniture")} within 15 yalms, " +
                   "however the room is laid out.";
        if (preset.Anchor?.Partner is { } partner)
            return $"Positions you around {partner.Partner.Name} wherever you both are. They stay put; only you move.";
        return "Not anchored: it plays with its offset from wherever you're standing.";
    }

    /// The animation that plays, without the mod name (cards sit under their mod's heading).
    public static string? Animation(NamedPose preset)
    {
        if (preset.Penumbra is not { ModName.Length: > 0 } link) return preset.Pose.DisplayName;
        return link.OptionName is "" or "Default" ? preset.Pose.DisplayName : link.OptionName;
    }

    /// Everything about a preset in a few lines, for a chip's tooltip.
    public static string Summary(NamedPose preset)
    {
        var lines = $"{preset.Name}\n{Type(preset)}  ·  {Anchor(preset)}";
        if (ModName(preset) is { } mod) lines += $"\n{mod}: {Animation(preset)}";
        else lines += $"\n{preset.Pose.DisplayName}";
        return lines;
    }

    private static string Named(string name, string fallback) => name.Length > 0 ? name : fallback;
}
