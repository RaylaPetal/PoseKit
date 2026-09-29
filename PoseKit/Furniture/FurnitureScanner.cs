namespace PoseKit.Furniture;

using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SheetHousingFurniture = Lumina.Excel.Sheets.HousingFurniture;
using SheetHousingYardObject = Lumina.Excel.Sheets.HousingYardObject;

/// <summary>A snapshot of a furniture item near the player. EntryId is its sheet row id.</summary>
public readonly record struct NearbyFurniture(uint EntryId, string Name, Vector3 Position, float Rotation);

/// <summary>
/// Finds placed housing furniture near the player for the furniture anchor picker.
///
/// Furniture isn't in Dalamud's object table; it lives in the housing FurnitureManager's own object
/// array, as HousingEventObjects. LastDiagnostic reports which step came back empty.
/// </summary>
public sealed unsafe class FurnitureScanner
{
    private const float ScanRadius = 5f;

    public string? LastDiagnostic { get; private set; }

    public List<NearbyFurniture> ScanNearby(IPlayerCharacter? localPlayer)
    {
        var results = new List<NearbyFurniture>();
        if (localPlayer == null)
        {
            LastDiagnostic = "no local player";
            return results;
        }

        var housingManager = HousingManager.Instance();
        if (housingManager == null)
        {
            LastDiagnostic = "HousingManager.Instance() is null";
            return results;
        }

        if (housingManager->IndoorTerritory != null)
            LastDiagnostic = Collect(&housingManager->IndoorTerritory->FurnitureManager, "indoor", localPlayer, results);
        else if (housingManager->OutdoorTerritory != null)
            LastDiagnostic = Collect(&housingManager->OutdoorTerritory->FurnitureManager, "outdoor", localPlayer, results);
        else
            LastDiagnostic = "not in a housing territory (IndoorTerritory and OutdoorTerritory are both null)";

        return results;
    }

    private static string Collect(HousingFurnitureManager* manager, string territoryLabel, IPlayerCharacter localPlayer, List<NearbyFurniture> results)
    {
        var objectArray = &manager->ObjectManager.ObjectArray;
        var objects = objectArray->Objects;
        var slots = 0;
        var nearestUnfiltered = float.MaxValue;
        var sampleReasons = new List<string>();

        for (var i = 0; i < objectArray->ObjectCount && i < objects.Length; i++)
        {
            var gameObject = objects[i].Value;
            if (gameObject == null) continue;

            var housing = (HousingEventObject*)gameObject;
            slots++;

            Vector3 position = housing->Position;
            var distance = Vector3.Distance(position, localPlayer.Position);
            if (distance < nearestUnfiltered) nearestUnfiltered = distance;
            if (distance > ScanRadius) continue;

            var (name, rowId, reason) = ResolveName(housing->HousingObjectId.EntryId, housing->HousingObjectId.Type);
            if (sampleReasons.Count < 5)
                sampleReasons.Add($"entryId={housing->HousingObjectId.EntryId} type={housing->HousingObjectId.Type} -> {reason}");
            if (string.IsNullOrEmpty(name)) continue;

            results.Add(new NearbyFurniture(rowId, name, position, housing->Rotation));
        }

        var nearestText = slots > 0 ? $"{nearestUnfiltered:0.0}y" : "n/a";
        var summary = $"{territoryLabel} territory, player@{localPlayer.Position.X:0.0},{localPlayer.Position.Y:0.0},{localPlayer.Position.Z:0.0}, " +
                      $"{objectArray->ObjectCount} object slots ({slots} non-null), nearest {nearestText} away, {results.Count} matched within {ScanRadius}y with a resolvable name";
        return sampleReasons.Count > 0 ? $"{summary} | samples: {string.Join("; ", sampleReasons)}" : summary;
    }

    /// Sheet row ids are (Type &lt;&lt; 16) | EntryId.
    private static (string? Name, uint RowId, string Reason) ResolveName(ushort entryId, HousingObjectType type)
    {
        var rowId = ((uint)type << 16) | entryId;
        switch (type)
        {
            case HousingObjectType.Furniture:
            {
                var row = Plugin.DataManager.GetExcelSheet<SheetHousingFurniture>().GetRowOrDefault(rowId);
                if (row is not { } r) return (null, rowId, $"no HousingFurniture row {rowId}");
                if (!r.Item.IsValid) return (null, rowId, $"HousingFurniture row found, Item link invalid (Item.RowId={r.Item.RowId})");
                var name = r.Item.Value.Name.ExtractText();
                return string.IsNullOrEmpty(name) ? (null, rowId, "Item found but Name is empty") : (name, rowId, "ok");
            }
            case HousingObjectType.YardObject:
            {
                var row = Plugin.DataManager.GetExcelSheet<SheetHousingYardObject>().GetRowOrDefault(rowId);
                if (row is not { } r) return (null, rowId, $"no HousingYardObject row {rowId}");
                if (!r.Item.IsValid) return (null, rowId, $"HousingYardObject row found, Item link invalid (Item.RowId={r.Item.RowId})");
                var name = r.Item.Value.Name.ExtractText();
                return string.IsNullOrEmpty(name) ? (null, rowId, "Item found but Name is empty") : (name, rowId, "ok");
            }
            default:
                return (null, rowId, $"unrecognized HousingObjectType ({type})");
        }
    }
}
