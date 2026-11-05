# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

PoseKit is a Dalamud plugin (C#, FFXIV) for posing: Penumbra animation-mod discovery, live render offsets,
presets with anchors, partner pairing over `/tell`, Bone Align, alignment memory, a quick library UI and a freecam.

## Build and release

- Build: `dotnet build PoseKit/PoseKit.csproj -c Debug -p:Platform=x64` (output in `PoseKit/bin/x64/Debug`).
  CI builds `--configuration Release` on windows with .NET 10 and Dalamud from `%AppData%\XIVLauncher\addon\Hooks\dev`.
- `Penumbra.Api` and `Luna` are git submodules built from source (`git submodule update --init --recursive`);
  the published Penumbra.Api package doesn't work with the published Luna.
- There is no test project. Changes are verified by building, then in-game. For pure logic (e.g. the meta.json
  scanning code) a throwaway console project that compiles the real source files against a stub `Plugin.Log` works.
- Release: bump `<Version>` (4-part) in `PoseKit/PoseKit.csproj`, commit to master, push, then push a `v*` tag.
  `release.yml` builds whatever version the csproj says (not the tag) and commits the `repo.json` update to master.
  A tag ending in `-testing` publishes a testing build only.
- What's new: `Windows/ChangelogWindow.cs` holds the player-facing notes. Rewrite its sections and bump
  `ChangelogId` for a release worth announcing; it then opens once for every updating player.

## OpenSpec workflow

Features are planned as OpenSpec changes (`openspec/changes/<name>`: proposal, specs, design, tasks; durable
capabilities in `openspec/specs/`). Use the `/opsx:*` commands (explore, propose, apply, archive) and
`openspec validate --strict`. Tasks needing in-game verification stay unchecked until the user confirms them.

## Architecture

`Plugin.cs` is the composition root: it creates every service, wires events between them, registers
`/posekit` subcommands, and ticks services from `Framework.Update` in a deliberate order. Services take
`Plugin` or explicit dependencies; there is no DI container.

**Poses and offsets**
- `PoseIdentifier` names a pose by emote mode (1 groundsit, 2 chair sit, 3 doze) and CPose state, or an emote.
  `PoseTrigger` plays it (cycling `/cpose` for sit variants) and owns the applied offset.
- `OffsetEngine` hooks `SetDrawOffset`/`SetDrawRotation`; the offset position is in the character's *drawn*
  frame (actual rotation + rotation offset), so world deltas must be rotated by the inverse of that facing.
  Optionally bridged to SimpleHeels instead.
- The applied offset is layered: base (preset/manual) + partner-anchor correction + bone-align correction.
  Only `PoseTrigger` composes these (`ComposeOffset`/`Subtract`); `GetOffsetForNewPreset` strips the bone layer.
  Manual edits must go through `ApplyManualOffset`. `OffsetGeneration` bumps on every reset/new play, and
  in-flight work (Bone Align, seat placement) cancels when it changes.
- `CurrentPlayContext` (`Bones/PlayContext`) identifies what's playing (mod/group/option/trigger); alignment
  memory and seat-snap memory key off it.

**Penumbra** (`Penumbra/`): scans selected mods' `meta.json` groups/options for `.pap` files to derive triggers.
Playing selects options via *temporary* settings in the player's collection, never permanent ones. Each play is
exclusive (`Library/ExclusivePlay.cs`): it clears other options in the mod overlapping its `.pap` game paths, raises the
mod's temporary priority only above mods actively claiming them (`AnimationClaimIndex`), and `PlayVerifier` then checks
what Penumbra resolves and reports a loss in chat.

**Presets** (`Presets/`): `NamedPose` with offset, `PenumbraLink`, and an optional anchor (spot, furniture or
partner) that recomputes the offset on replay. Couple presets carry the partner's half.

**Pairing** (`Pairing/`): a session-only protocol over `/tell` with `posekit…` keywords. Each keyword exists in
both `PairingComposer` (send) and `PairingListener` (parse; sender taken from the chat sender, never the text),
and `StartsWith` order matters for keywords that prefix others. Mod/group/option names travel as hashes
(`ModDirectoryHash`). Relays (couple presets, library roles) are answered exactly once; `CoupleRelayOutbox` plays
this side only after `Accepted`. New messages must degrade gracefully for older versions that ignore them.
Picks normally go through `CoupleQueueService` (both pick, then both play); "override" and "solo" change that.

**Bones** (`Bones/`): `BoneAlignService` samples bones and corrects the bone layer; `AlignmentMemory` persists
results per animation in `alignments.json`; `AutoAlignCoordinator` re-aligns remembered animations and saves
hand nudges. `AlignOrigin` (Manual/Memory/Nudge) decides what gets recorded and whether emotes resync.

**Quick library** (`Library/`, `Windows/QuickLibraryWindow.cs`): `LibraryIndex` flattens discovered options;
`LibraryContextResolver` picks Standing, Seated or Bed context from `SeatReferenceService`; `SeatPlacement` moves
groundsits/emotes played while seated onto the seat (stand-up retry, hip snap). `PlayDispatch` is the single
click path shared with the main window. The main window must keep working as before; the library is the quick UI.

**Pairing tags** (`Library/DeclaredTags.cs`, `PairingTagService.cs`): each option's `FxF`, `MxF` or `M/F x F` state comes only from evidence, strongest first: the player's right-click tag, parts recorded by Bone Align (`ContactEvidence`), words naming the act (`NameGuess`, Strong), the author's role code or solo mark, any installed mod's byte-identical animation, a literal label, and other words in its names. The rule: a couple scene is `MxF` unless named acts show no penis is needed (those are `M/F x F`), explicit no-penis acts are `FxF`, and male-only scenes are `MxF`. Author codes are often wrong, which is why act words outrank them. Identical files share their best word reading, and a non-list group's options share a tag. Don't infer from bone tracks: penis tracks appear in F/F animations. `AnimationHasher` identifies animations by `.pap` content in the background, so tags and alignments carry across renumbering and packs. `LibraryBuckets` sorts rows into Fits and Doesn't fit; only a stated solo removes one. `ContactObserver` measures closest parts during paired plays, and its results stay suggestions in the row's menu.

**Scenes** (`Library/SceneRoles.cs`): a name like `(Dom-Confirm/Sub-Sweep)` or `[Sub-Gsit1, Dom-Gsit2]` whose two triggers exist on the option is a scene; anything less clear keeps the queue flow. Play together offers the partner their role through `posekitrole` and plays this side only on `Accepted`; a partner's Swap declines and sends the reversed offer.

**Recents** (`Library/RecentPlays.cs`): `PlayDispatch.PlayOptionTrigger` records every mod play per partner or solo, and the quick library lists them above the mods, ignoring type filters.

**Favorites** (`Library/Favorites.cs`): starred options (`modDirectory|group|option`, ones with triggers) in
`Configuration`; both windows list them in a Favorites section above the mods (playable there).
There are no categories: the library is the main window's mod → group → option tree, collapsed and filtered to
the current context.

**Persistence**: settings in `Configuration`; the plugin config dir also holds `alignments.json`,
`seat-snap.json`, `pairing-tags.json` (player tags and the file hash cache), `recents.json` and
`contact-observations.json`. These files save via a temp file, and an unreadable
one is renamed to `.bad` on the next save rather than overwritten.

**Freecam** (`Camera/`): see `PoseKit/Camera/README.md`.

## Conventions

- Match the existing style: `///` comments on members that explain *why*, not restating the code; LF line
  endings, 4-space indent (`.editorconfig`).
- User-facing messages go to chat as `[PoseKit] …` (`ChatGui.Print`/`PrintError`).
- ImGui is `Dalamud.Bindings.ImGui` (ImGuizmo is available too); use `ImRaii` for scoped style/disable.
