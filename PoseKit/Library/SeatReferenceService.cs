using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using PoseKit.Bones;
using PoseKit.Furniture;

namespace PoseKit.Library;

/// <summary>What the character was on when the reference was recorded.</summary>
public enum ReferenceKind { Seat, Bed }

/// <summary>
/// Where the game put the character when they sat or lay down: the position and facing, the hip
/// point, and the furniture item when there is one. Placed plays are put back here.
/// </summary>
public sealed record SeatReference(ReferenceKind Kind, uint TerritoryType, Vector3 Position, float Rotation, Vector3? HipPoint,
    NearbyFurniture? Furniture)
{
    /// "seat" or "bed", for messages.
    public string Surface => SurfaceFor(Kind);

    public static string SurfaceFor(ReferenceKind kind) => kind == ReferenceKind.Bed ? "bed" : "seat";

    public string Label => Furniture?.Name ?? (Kind == ReferenceKind.Bed ? "Bed" : "Seat");

    /// The emote mode the game uses on this surface: chair sit or doze.
    public uint NativeMode => NativeModeFor(Kind);

    public static uint NativeModeFor(ReferenceKind kind) =>
        kind == ReferenceKind.Bed ? LibraryIndex.DozeMode : LibraryIndex.ChairSitMode;
}

/// <summary>
/// Records a SeatReference each time the character settles into a native chair sit or doze, and
/// clears it on a zone change or once the character walks away. One reference at a time: the latest
/// sit or lie-down replaces it. Recording only reads; it never moves anything.
/// </summary>
public sealed class SeatReferenceService
{
    // The sit-down transition plays first; measuring during it would catch a half-seated body.
    private const long MinSettleMs = 600;
    private const long MaxSettleMs = 2000;
    // Lying down takes longer than sitting.
    private const long MaxBedSettleMs = 3500;
    private const float SettledHipMovement = 0.01f;
    private const long SampleMs = 1000;

    private const float ClearDistance = 3f;
    private const float FurnitureReach = 2f;

    private enum Phase { Idle, Settling, Sampling }

    private readonly FurnitureScanner furnitureScanner = new();

    private Phase phase = Phase.Idle;
    private long phaseStartedAt;
    private Vector3? lastHip;
    private Vector3 hipSum;
    private int hipSamples;
    private ReferenceKind? lastKind;
    private ReferenceKind measuringKind;

    public SeatReference? Reference { get; private set; }

    /// Seated and still measuring, so there's no reference for this seat yet.
    public bool IsMeasuring => phase != Phase.Idle;

    /// The character entered a native chair sit or doze (once per entry, before measuring).
    public event Action? SatDown;

    public void Tick(IPlayerCharacter? localPlayer, PoseIdentifier? currentPose, uint territoryType)
    {
        if (localPlayer == null)
        {
            Reset();
            return;
        }

        ReferenceKind? kind = currentPose?.EmoteModeId switch
        {
            LibraryIndex.ChairSitMode => ReferenceKind.Seat,
            LibraryIndex.DozeMode => ReferenceKind.Bed,
            _ => null,
        };
        if (kind is { } entered && kind != lastKind)
        {
            measuringKind = entered;
            phase = Phase.Settling;
            phaseStartedAt = Environment.TickCount64;
            lastHip = null;
            SatDown?.Invoke();
        }
        lastKind = kind;

        if (Reference is { } reference &&
            (reference.TerritoryType != territoryType || Vector3.Distance(reference.Position, localPlayer.Position) > ClearDistance))
            Reference = null;

        if (phase == Phase.Idle) return;
        if (kind != measuringKind)
        {
            // Got up before measuring finished; the old reference (if any) stays.
            phase = Phase.Idle;
            return;
        }

        var hasHip = HipPoint.TryGet(localPlayer, out var hip);
        var elapsed = Environment.TickCount64 - phaseStartedAt;

        if (phase == Phase.Settling)
        {
            var still = hasHip && lastHip is { } previous && Vector3.Distance(previous, hip) < SettledHipMovement;
            lastHip = hasHip ? hip : null;
            var maxSettle = measuringKind == ReferenceKind.Bed ? MaxBedSettleMs : MaxSettleMs;
            if (elapsed < MinSettleMs || (!still && elapsed < maxSettle)) return;

            phase = Phase.Sampling;
            phaseStartedAt = Environment.TickCount64;
            hipSum = Vector3.Zero;
            hipSamples = 0;
            return;
        }

        if (hasHip)
        {
            hipSum += hip;
            hipSamples++;
        }
        if (elapsed < SampleMs) return;

        Record(localPlayer, territoryType);
        phase = Phase.Idle;
    }

    private void Record(IPlayerCharacter localPlayer, uint territoryType)
    {
        Vector3? hip = hipSamples > 0 ? hipSum / hipSamples : null;
        var furniture = furnitureScanner.ScanNearby(localPlayer)
            .Where(f => Vector3.Distance(f.Position, localPlayer.Position) <= FurnitureReach)
            .OrderBy(f => Vector3.Distance(f.Position, localPlayer.Position))
            .Cast<NearbyFurniture?>()
            .FirstOrDefault();

        Reference = new SeatReference(measuringKind, territoryType, localPlayer.Position, localPlayer.Rotation, hip, furniture);
        Plugin.Log.Debug($"[PoseKit] {Reference.Kind} reference: {Reference.Label}, position {localPlayer.Position}, " +
                         $"hip {(hip is { } h ? h.ToString() : "unreadable")} (height above position {(hip is { } y ? y.Y - localPlayer.Position.Y : float.NaN):0.00}).");
    }

    private void Reset()
    {
        phase = Phase.Idle;
        lastKind = null;
        Reference = null;
    }
}
