using System;
using System.Collections.Generic;
using System.Numerics;
using PoseKit.Penumbra;
using PoseKit.Presets;

namespace PoseKit.Library;

/// <summary>
/// A titled group of the library; an option is listed under it with only the triggers it includes.
/// Placed sections play onto the seat (see SeatPlacement) instead of where the character stands.
/// </summary>
public sealed record LibrarySection(string Title, Func<PoseTriggerHint, bool> Includes, bool Placed = false);

/// <summary>What the quick library shows for what the character is doing right now.</summary>
public interface ILibraryContext
{
    string Name { get; }

    /// The heading of the fitting presets, listed above the sections.
    string PresetsTitle { get; }

    IReadOnlyList<LibrarySection> Sections { get; }
    bool PresetFits(NamedPose preset);
}

/// <summary>Anywhere the character isn't chair-seated or in bed: groundsits, dozes and emotes play where they stand.</summary>
public sealed class StandingContext : ILibraryContext
{
    public static readonly StandingContext Instance = new();

    public string Name => "Standing";
    public string PresetsTitle => "Presets";

    public IReadOnlyList<LibrarySection> Sections { get; } =
    [
        new("Ground sits", LibraryIndex.IsGroundSit),
        new("Doze poses", LibraryIndex.IsDoze),
        new("Emotes & dances", LibraryIndex.IsEmote),
    ];

    /// Chair-sit poses need a chair (doze plays anywhere, see AnywherePose), and a furniture preset
    /// belongs to that furniture.
    public bool PresetFits(NamedPose preset) =>
        preset.Pose.EmoteModeId != LibraryIndex.ChairSitMode && preset.Anchor?.Furniture == null;
}

/// <summary>
/// On furniture the game placed the character on (a chair or a bed), or playing something placed
/// onto it: the furniture's own poses play natively, groundsits and emotes are placed onto it.
/// </summary>
public abstract class FurnitureContextBase : ILibraryContext
{
    // A spot preset counts as this furniture's when saved this close to it.
    private const float SameSpotDistance = 1f;

    /// The surface being shown; null while it's still being measured.
    public SeatReference? Reference { get; set; }

    public abstract ReferenceKind Kind { get; }

    /// The snap-memory context kind, kept separate per surface.
    public abstract string MemoryKind { get; }

    /// "seat" or "bed", for labels.
    public string Surface => SeatReference.SurfaceFor(Kind);

    public string Name => Reference?.Label ?? (Kind == ReferenceKind.Bed ? "Bed" : "Seat");
    public string PresetsTitle => "Here";
    public abstract IReadOnlyList<LibrarySection> Sections { get; }

    /// The snap mode a placed trigger starts with when the player hasn't chosen one.
    public abstract SnapMode DefaultSnapMode(PoseTriggerHint trigger);

    /// Presets saved for this furniture item (any placed copy), or at this spot.
    public bool PresetFits(NamedPose preset)
    {
        if (Reference is not { } surface || preset.Anchor is not { } anchor) return false;
        if (anchor.Furniture is { } furniture)
            return surface.Furniture is { } here && here.EntryId == furniture.EntryId;
        return anchor.Spot is { } spot && spot.TerritoryType == surface.TerritoryType
               && Vector3.Distance(spot.Position, surface.Position) <= SameSpotDistance;
    }
}

/// <summary>In a native chair sit: only chair sits are listed, and they play natively.</summary>
public sealed class SeatedContext : FurnitureContextBase
{
    public static readonly SeatedContext Instance = new();

    public override ReferenceKind Kind => ReferenceKind.Seat;
    public override string MemoryKind => "seated";

    public override IReadOnlyList<LibrarySection> Sections { get; } =
    [
        new("Sit poses", LibraryIndex.IsChairSit),
    ];

    /// Groundsits are authored sitting on the floor, so they need lifting onto the seat; emotes are
    /// as often kneeling or standing beside it.
    public override SnapMode DefaultSnapMode(PoseTriggerHint trigger) =>
        LibraryIndex.IsGroundSit(trigger) ? SnapMode.Hips : SnapMode.PositionOnly;
}

/// <summary>In a native doze: only doze poses are listed, and they play natively.</summary>
public sealed class BedContext : FurnitureContextBase
{
    public static readonly BedContext Instance = new();

    public override ReferenceKind Kind => ReferenceKind.Bed;
    public override string MemoryKind => "bed";

    public override IReadOnlyList<LibrarySection> Sections { get; } =
    [
        new("Bed poses", LibraryIndex.IsDoze),
    ];

    /// Bed animations are mostly laying or kneeling at floor height, so everything is lifted to the
    /// mattress by default.
    public override SnapMode DefaultSnapMode(PoseTriggerHint trigger) => SnapMode.Hips;
}

public static class LibraryContextResolver
{
    public static ILibraryContext Resolve(Plugin plugin)
    {
        var pose = PoseIdentifier.FromCharacter(Plugin.ObjectTable.LocalPlayer);
        var seats = plugin.SeatReferences;

        // The game's own pose decides while on the furniture; a placed play keeps the reference's kind.
        ReferenceKind? kind = pose?.EmoteModeId switch
        {
            LibraryIndex.ChairSitMode => ReferenceKind.Seat,
            LibraryIndex.DozeMode => ReferenceKind.Bed,
            _ => plugin.SeatPlacement.IsActive ? seats.Reference?.Kind : null,
        };
        if (kind is not { } k) return StandingContext.Instance;

        FurnitureContextBase context = k == ReferenceKind.Bed ? BedContext.Instance : SeatedContext.Instance;
        // While a new sit or lie-down is measured, the previous reference doesn't describe it.
        context.Reference = seats.IsMeasuring || seats.Reference?.Kind != k ? null : seats.Reference;
        return context;
    }
}
