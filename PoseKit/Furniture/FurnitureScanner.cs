namespace PoseKit.Furniture;

using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using SheetHousingFurniture = Lumina.Excel.Sheets.HousingFurniture;
using SheetHousingYardObject = Lumina.Excel.Sheets.HousingYardObject;

/// <summary>A furniture item near the player at scan time — a snapshot, not a live pointer into game
/// memory, since that's only guaranteed valid for the current frame. EntryId is the matching key — a
/// real HousingFurniture/HousingYardObject sheet row id (already packed with its type, see
/// FurnitureScanner.ResolveName), so it's opaque and self-sufficient for exact-equality matching.
/// </summary>
public readonly record struct NearbyFurniture(uint EntryId, string Name, Vector3 Position, float Rotation);

/// <summary>
/// Discovers placed housing furniture near the player for the save-preset furniture picker.
///
/// Placed furniture — indoor room items and outdoor/yard items alike — is NOT part of Dalamud's
/// regular object table the way NPCs/players/doors are (confirmed live: filtering the object table
/// for ObjectKind.HousingEventObject found nothing near real furniture). It also isn't reachable via
/// FurnitureManager.FurnitureVector's runtime HousingFurniture records — those carry a differently
/// -keyed id that never resolves against any sheet (also confirmed live).
///
/// The actual working path, found by decompiling ReMakePlacePlugin (an installed, working
/// furniture-editing plugin) with ilspycmd rather than guessing further: FurnitureManager.ObjectManager
/// .ObjectArray holds actual GameObject* pointers for every placed item in the current
/// indoor/outdoor territory — a housing-specific object array Dalamud's own IObjectTable never
/// walks. Casting each to FFXIVClientStructs' HousingEventObject exposes HousingObjectId (an EntryId
/// plus a Furniture/YardObject Type) and Position/Rotation directly.
///
/// HousingObjectId.EntryId alone is NOT the sheet row id — it's the low 16 bits of it. Confirmed
/// offline against the real game data (Lumina.GameData pointed at the installed sqpack, not a live
/// guess): HousingFurniture's actual row ids run 196608-198402, i.e. 0x30000 + a 16-bit index, and
/// 0x30000 == HousingObjectType.Furniture(3) &lt;&lt; 16 exactly. So the row id is
/// `((uint)Type &lt;&lt; 16) | EntryId` — see ResolveName.
///
/// LastDiagnostic exists purely because none of this can be verified without a running game client —
/// it surfaces exactly which stage came back empty directly in the UI, so a report of "still not
/// found" can be narrowed down without guessing again.
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

    /// HousingFurniture/HousingYardObject sheet row ids are packed as (Type &lt;&lt; 16) | EntryId —
    /// confirmed offline against the actual game data files (Lumina.GameData pointed at the installed
    /// sqpack), not guessed: HousingFurniture's real row ids run 196608-198402 (0x30000 + a 16-bit
    /// index), and 0x30000 == HousingObjectType.Furniture(3) &lt;&lt; 16 exactly. Two earlier attempts
    /// (EntryId as a direct row id; FurnitureVector's differently-packed Id, masked or not) were each
    /// tested the same way and ruled out — this is the one that resolves to real rows with real names
    /// (e.g. entryId 197 under Furniture -&gt; row 196805 -&gt; "Carbuncle Armchair").
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
