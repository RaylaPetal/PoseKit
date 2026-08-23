# TODO

Read the posekit.d implementation. Read dalamud.dev plugin stuff and study them https://dalamud.dev/plugin-development/getting-started

Give me a plan implementing all these features with:
A friendly UI for offsetting and saving poses with a name and replaying them
Syncing emotes
Finding animation mods within a folder and playing them with a click of a button

# PoseKit — Design Doc
*(working name — rename freely; I'll call it "PoseKit" throughout)*

An Avsitter-for-FFXIV plugin: offset + save + replay poses, auto-discover Penumbra pose mods, and manually align emote timing with a partner. Built as a Dalamud plugin in C#, leaning heavily on prior art from **SimpleHeels** (offset engine) and **Penumbra's IPC** (mod introspection).

---

## 0. Correction on Feature #4 (important)

I checked SimpleHeels' actual changelog before designing this. `/heels emotesync` is **not networked**. It's a local command:

```
/heels emotesync
/heels emotesync delay [seconds]
```

It just resets *your own* emote's internal loop timer (optionally after a delay), so two people can manually count down in party/tell chat ("3, 2, 1, go") and both hit their own sync command to land back in phase with each other. There's no packet exchange, no relay server, no dependency on the other person even having the plugin.

This is good news — it means feature #4 is by far the *simplest* of the four to build (no networking, no desync risk, no server), and it's what you asked for ("do the same as SimpleHeels emotesync"). Section 5 below designs it exactly this way.

---

## 1. Feature → Component Map

| # | Feature | Component |
|---|---------|-----------|
| 1 | Live offset like SimpleHeels | `OffsetEngine` + `TempOffsetOverlay` (gizmo) |
| 2 | Save/name/replay offset per emote | `PoseIdentifier` + `PoseConfig` (presets) |
| 3 | Read Penumbra mods in "Animations" folder, button per pose | `PenumbraPoseScanner` |
| 4 | Manual timing re-sync | `EmoteSyncCommand` (local-only, no networking) |

---

## 2. Dependencies

- **Dalamud** (plugin SDK) — `IFramework`, `IClientState`, `IGameGui`, `IChatGui`, `ICommandManager`, `IPluginConfig`
- **Penumbra.Api** (NuGet, or copy the IPC subscriber classes) — for mod enumeration
- Optional: fork/vendor pieces of **SimpleHeels** (MIT-ish, check current license header before redistributing) — specifically its `IOffsetProvider`, `ModelOffsetProvider`, and `TempOffset`/`TempOffsetOverlay` gizmo code, since your Feature 1 is functionally identical to what it already does. No reason to re-derive the render-only offset hook from scratch.

---

## 3. Core Data Model

### 3.1 `PoseIdentifier`
Mirrors SimpleHeels' `EmoteIdentifier` — a value that uniquely identifies "which pose/emote variant is currently playing" so offsets and presets can be keyed to it.

```csharp
public readonly record struct PoseIdentifier(
    ushort EmoteId,        // base emote/EmoteMode row
    byte Variant           // sub-pose index, e.g. chair-sit pose 2 vs 3
) {
    public static PoseIdentifier? FromCharacter(ICharacter c) { /* read EmoteController state */ }
}
```

The FFXIV animation state for things like ground-sit / chair-sit exposes a "pose" sub-index (this is the same value the in-game `/sit`, `/groundsit`, `/doze` cycling uses — see `ActionTimeline`/`EmoteMode` sheets). Read it the same way SimpleHeels' `EmoteIdentifier.cs` does — from the character's animation/EmoteController state, not by guessing off the played `.pap` filename.

### 3.2 `PoseOffset`
```csharp
public struct PoseOffset {
    public Vector3 Position;   // local, relative to actor's base transform
    public float Rotation;     // yaw, radians
}
```

### 3.3 `NamedPose` (Feature 2)
```csharp
public class NamedPose {
    public string Name = "";
    public PoseIdentifier Pose;
    public PoseOffset Offset;
}
```

### 3.4 Config
```csharp
public class PoseKitConfig : IPluginConfiguration {
    public int Version { get; set; } = 1;
    // keyed per "identity" (character), same pattern as SimpleHeels
    public Dictionary<string, CharacterPoseConfig> Characters = new();
}

public class CharacterPoseConfig {
    public List<NamedPose> Presets = new();
}
```

Save via Dalamud's standard `IDalamudPluginInterface.SavePluginConfig`. Key by local content ID (or "identity" name) so alts don't collide — copy SimpleHeels' identity pattern (`/heels identity set [name]` / `reset`) if you want manual override too.

---

## 4. Feature 1 + 2: Offset Engine & Named Presets

### 4.1 Applying the offset (render-only, critical detail)

**Do not write to `GameObject.Position`.** That's the server-authoritative position; nudging it client-side causes rubber-banding/desync the moment the server sends a correction. SimpleHeels avoids this by offsetting at the **draw/model level** — it hooks into the actor's render transform (not the logical game-object transform) every frame via `IFramework.Update`, so the server never sees the offset. Reuse `ModelOffsetProvider`/`RelativeEmoteOffsetProvider` from SimpleHeels directly if you fork it — this is the single trickiest/most fragile part of the whole plugin (it deals with game struct offsets that shift between patches), and there's no reason to re-solve it independently when a maintained implementation exists.

Flow each frame:
```
OnFrameworkUpdate:
    if (localPlayer has active TempOffset or matched NamedPose offset):
        currentPose = PoseIdentifier.FromCharacter(localPlayer)
        if currentPose changed since last frame:
            offset = LookupOffsetFor(currentPose)   // temp edit, or last-applied preset
        WriteRenderOffset(localPlayer, offset.Position, offset.Rotation)
```

### 4.2 Live editing (gizmo)
Port `TempOffset` + `TempOffsetOverlay` behavior: while a config window toggle is active, draw an ImGui 3D gizmo over the character (screen-projected from world position) letting the user drag X/Y/Z and yaw. Store this as `TempOffset` (uncommitted) until they hit **Save**.

### 4.3 Save & replay (Feature 2 specifically)

**Save flow:**
1. User is in an emote/pose, adjusts gizmo → `TempOffset`.
2. Clicks "Save as preset", types a name.
3. `PoseIdentifier.FromCharacter(localPlayer)` is captured *at that moment* alongside the offset → stored as `NamedPose` in `CharacterPoseConfig.Presets`.

**Replay flow (the "click a button, it plays the emote with that offset" part):**
1. UI groups saved presets by `PoseIdentifier`, shown as buttons: `[Cuddle - "close"] [Cuddle - "far"]`.
2. On click:
   - Issue the emote itself. Two ways, pick based on whether the identifier is a real chat emote or a pose-cycle sub-index:
     - Real emote: send `/[emotename] motion` via `IChatGui`/`ChatGui.SendMessage`, or better, resolve to the emote's `Action`/`EmoteMode` and use Dalamud's emote-execution helper if available, so you don't spam visible chat text.
     - Sit/pose-cycle sub-variant: replicate what `/sit`-cycling does — trigger the base emote, then step the pose sub-index to match `Variant` (this needs the same internal call the game uses when you repeat `/sit` to cycle poses — check how SimpleHeels or Brio trigger pose-cycling programmatically rather than reverse-engineering the opcode yourself).
   - Once the emote's `PoseIdentifier` is confirmed active (poll for 1-2 frames), apply `NamedPose.Offset` via the same render-offset path as 4.1.

---

## 5. Feature 3: Penumbra Pose Discovery

This is the novel part. Goal: for every enabled Penumbra mod, find `.pap` files sitting under an **"Animations"** folder inside that mod, figure out which sit/gesture pose each one replaces, and generate one button per pose.

### Two viable approaches — recommend combining them

**Approach A — Resolved file tree (robust, recommended primary source)**
Penumbra's IPC exposes `GetPlayerResourceTrees` (or equivalent `ResourceTree` subscriber depending on API version) — this returns the files *actually currently resolved* for the local player's rendered actor, including which mod redirected which game path. Filter to paths under `.../animation/...` ending `.pap`. This tells you definitively "this specific pose is live right now," with zero guessing about folder naming conventions on the modder's end.

**Approach B — Static mod-folder scan (matches what you described)**
1. `Penumbra.Api.Ipc.GetModList` → dictionary of enabled mod directory names + display names for the active collection.
2. `Penumbra.Api.Ipc.GetModPath` (or read `GetModDirectory` from plugin config) → resolve each mod's on-disk folder.
3. Recursively scan each mod folder for a subdirectory literally named `Animations` (case-insensitive), collect `.pap` files inside.
4. For grouped/optional mods, cross-reference against `GetOptionGroups`/`GetAvailableModSettings` so you only list `.pap`s belonging to **currently enabled options**, not every variant the mod ships.

Use **A** to know what's actually active on the character right now (for correctness), and **B** when you want to preview poses that exist in an enabled mod's option group *before* the mod redirect has ever been resolved (i.e., let the user click a pose that isn't currently playing yet, and B tells you which optional groups exist to switch on).

### Mapping a `.pap` path to a human label

Neither Penumbra nor the raw file path tells you "this is Sit Pose 2." You need a static lookup table you build once, mapping known game animation paths (or the `ActionTimeline`/`EmoteMode` row they belong to) to friendly names — e.g. via Lumina sheets (`Emote`, `EmoteMode`, `ActionTimeline`). Community pose mods are fairly consistent about which game path they redirect for "sit pose N" since that's dictated by the game's own sit-cycling animation slots, not by the modder — so this table is finite and reusable across mods, not something you rebuild per mod.

```csharp
static readonly Dictionary<string, PoseIdentifier> KnownSitSlots = new() {
    ["chara/.../sit_loop_01.pap"] = new PoseIdentifier(EmoteId: SitEmoteId, Variant: 1),
    ["chara/.../sit_loop_02.pap"] = new PoseIdentifier(EmoteId: SitEmoteId, Variant: 2),
    // ...
};
```

### UI generation
```
For each enabled mod with pose files:
    Header: [Mod Name]
        For each matched pose in that mod:
            Button("Sit Pose 2") -> ReplayFlow(matched PoseIdentifier)   // reuses 4.3
```
This is why `PoseIdentifier` is the shared key across Features 2 and 3 — a button generated from Penumbra discovery and a button generated from a saved preset both funnel into the exact same "trigger emote → confirm pose → apply offset" replay path.

---

## 6. Feature 4: Manual Emote Sync (no networking)

Chat command, mirroring SimpleHeels exactly:

```
/posekit sync                → resets local emote loop timer immediately
/posekit sync delay 2.5      → waits 2.5s, then resets it
```

Implementation: find whatever internal timer/frame-counter drives the currently-playing loop animation (SimpleHeels' emotesync code is public — read `PluginService.cs`/relevant command handler for the exact struct field it resets) and zero it out, optionally via a `Task.Delay` for the delay variant. No IPC, no other player involvement required — purely local.

Also expose it as a button in the main UI window, not just a chat command, since your goal is "click a button."

---

## 7. Suggested File Layout

```
PoseKit/
  Plugin.cs                     // entry point, service wiring
  PluginConfig.cs                // PoseKitConfig, CharacterPoseConfig
  PoseIdentifier.cs
  PoseOffset.cs
  OffsetEngine.cs                 // per-frame render offset apply (ported from SimpleHeels)
  TempOffset.cs / TempOffsetOverlay.cs   // ported gizmo
  Presets/
    NamedPose.cs
    PresetManager.cs             // save/load/lookup
  Penumbra/
    PenumbraIpc.cs                // thin wrapper over Penumbra.Api subscribers
    PenumbraPoseScanner.cs        // Approach A + B
    KnownPoseSlots.cs             // static path -> PoseIdentifier table
  Sync/
    EmoteSyncCommand.cs
  UI/
    MainWindow.cs
    PresetButtonsPanel.cs
    PenumbraPosePanel.cs
  Commands.cs                     // /posekit ...
```

---

## 8. Build/Deploy Notes

- Standard Dalamud plugin template (`XIVLauncher.PluginBuilder` or the community cookiecutter) targets .NET matching current Dalamud (check `Dalamud.Plugin` NuGet for the current target framework — this shifts across game patches, verify against Dalamud's own repo before scaffolding).
- Self-host via a `repo.json` + `DevPlugins` folder for local iteration, same as SimpleHeels/Penumbra ecosystem plugins.
- Hard dependency on Penumbra being installed and its IPC version being compatible — guard all Penumbra calls behind a version check + "Penumbra not found" UI state, since Feature 3 must degrade gracefully (Features 1/2/4 don't need Penumbra at all).

---

## 9. Open Risks / Unknowns to Resolve Early

1. **Render-offset hook stability** — this is the part of SimpleHeels most likely to break across game patches (it pokes at internal struct layout). Forking it means inheriting that maintenance burden; a hard dependency (calling SimpleHeels' own IPC, if it exposes one, to request offsets rather than reimplementing) is worth evaluating before committing to a fork.
2. **Programmatic pose-cycling** — confirm the exact internal call for stepping a sit/doze pose sub-variant without spamming visible `/sit` chat text repeatedly; this needs verifying against current game version, not assumed from memory.
3. **Penumbra IPC surface** — `GetPlayerResourceTrees` / `GetOptionGroups` exact signatures drift between Penumbra API versions; pin a version and check Penumbra's own IPC changelog before implementation.
4. **KnownSitSlots table completeness** — building this table (game path → friendly pose name) is manual, one-time research work against Lumina sheets; scope how many sit/ground-sit/doze variants you actually want to support at launch vs. later.

---

*Next step: pick one feature to prototype first. Given the dependency graph, I'd start with Feature 1 (offset engine, forking SimpleHeels' render-offset code) since Features 2 and 3 both build on top of it.*
