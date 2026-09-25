using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PoseKit.Furniture;
using PoseKit.Pairing;
using PoseKit.Penumbra;
using PoseKit.Presets;
using PoseKit.Sync;
using PoseKit.Windows;
using PoseKit.Camera;
using PoseKit.Movement;

namespace PoseKit;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;

    private const string CommandName = "/posekit";

    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PoseKit");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private WelcomeWindow WelcomeWindow { get; init; }

    public EmoteSyncCommand EmoteSync { get; init; }
    public FreeCamService FreeCam { get; init; }
    public AlignService AlignService { get; init; }

    public OffsetEngine OffsetEngine { get; init; }
    public PresetManager PresetManager { get; init; }
    public PoseTrigger PoseTrigger { get; init; }
    public SimpleHeelsBridge SimpleHeelsBridge { get; init; }
    public PenumbraIpc PenumbraIpc { get; init; }
    public PenumbraPoseScanner PenumbraPoseScanner { get; init; }
    public List<PoseModInfo> DiscoveredPoses { get; private set; } = new();

    /// Conflict-only data for mods outside Configuration.SelectedPenumbraMods — see
    /// PenumbraPoseScanner.ScanExternalConflicts. Never used for anything but the conflict marker;
    /// these mods have no UI representation of their own in the Animations tab.
    public Dictionary<PoseIdentifier, List<PenumbraPoseScanner.ExternalPoseClaim>> ExternalPoseClaims { get; private set; } = new();

    public PairingState PairingState { get; init; }
    public PairingListener PairingListener { get; init; }
    public CoupleQueueService CoupleQueueService { get; init; }
    public CouplePresetCaptureService CouplePresetCaptureService { get; init; }
    public CoupleRelayInbox CoupleRelayInbox { get; init; }
    public CoupleRelayOutbox CoupleRelayOutbox { get; init; }

    /// The preset currently loaded into the live-offset editor, if any — lets the UI offer
    /// "update this preset" instead of only ever "save as new".
    public NamedPose? LoadedPreset { get; set; }

    /// The Penumbra mod/group state a Play action in the Penumbra panel last put in place, if any —
    /// attached to the next saved preset so replaying it can restore that mod state too, not just
    /// the offset. Best-effort: goes stale if the user changes Penumbra settings some other way
    /// afterward, same as any other snapshot.
    public PenumbraLink? LastPlayedPenumbraContext { get; set; }

    /// One-shot latch for the Animations-tab scan, mirroring HasOfferedSimpleHeelsBridge below —
    /// on a fresh full game launch, Penumbra (or the local player's collection specifically) may
    /// not be resolvable yet at the exact moment this plugin's constructor runs, so the one eager
    /// scan there can come back empty and never get retried without a manual "Rescan" click.
    private bool hasScannedPenumbraPoses;

    /// Used only to resolve a capture-request's furniture-anchor hint against this side's own nearby
    /// furniture (see TryCaptureOwnStateForPartnerRequest below) — PresetButtonsPanel and PoseTrigger
    /// each keep their own instance for the same reason (a live furniture scan can't be cached).
    private readonly FurnitureScanner furnitureScanner = new();

    /// Last-seen sit/groundsit/doze pose, tracked so a drop that isn't the player's own doing (see
    /// RestorePoseIfDropped below) can be re-entered. Null whenever nothing needs recovering.
    private PoseIdentifier? lastKnownPoseForFreecamRestore;
    private int freecamRestoreAttempts;
    private long nextFreecamRestoreAttemptTime;
    private const int FreecamRestoreAttemptDelayMs = 500;
    private const int MaxFreecamRestoreAttempts = 5;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        PairingState = new PairingState();

        EmoteSync = new EmoteSyncCommand();
        FreeCam = new FreeCamService();
        OffsetEngine = new OffsetEngine();
        AlignService = new AlignService(PairingState, OffsetEngine);

        PresetManager = new PresetManager(Configuration);
        SimpleHeelsBridge = new SimpleHeelsBridge();
        PoseTrigger = new PoseTrigger(Configuration, OffsetEngine, SimpleHeelsBridge);
        PenumbraIpc = new PenumbraIpc();
        PenumbraPoseScanner = new PenumbraPoseScanner(PenumbraIpc, Configuration);

        PairingListener = new PairingListener(PairingState);
        CoupleQueueService = new CoupleQueueService(Configuration, PairingState, PairingListener, AlignService);
        CouplePresetCaptureService = new CouplePresetCaptureService(PairingState, PairingListener, PresetManager);
        CouplePresetCaptureService.Saved += saved => LoadedPreset = saved;
        CoupleRelayInbox = new CoupleRelayInbox(PairingState, PairingListener);
        CoupleRelayInbox.AutoApply += ApplyCapturedPartnerState;
        CoupleRelayOutbox = new CoupleRelayOutbox(PairingState, PairingListener, AlignService, PlayPreset);

        // A capture request arrives here when the partner is saving an "include partner" preset:
        // reply once with *this* side's own currently-playing pose/offset/Penumbra link — AND its own
        // anchor (own current spot, or own position relative to a matching nearby furniture item) —
        // never the requester's, since each side of a couple pose is typically already sitting/
        // standing in its own different spot (own mod/option, own offset, own seat on the same sofa)
        // by the time either one saves it as a preset. Silently sends nothing if this side isn't
        // currently in a pose — there's nothing meaningful to capture, per couple-preset-relay's
        // "silently omitted" requirement.
        PairingListener.PartnerCaptureRequested += (sender, requestId, hint) =>
        {
            if (TryCaptureOwnStateForPartnerRequest(hint) is { } captured)
                PairingListener.ReplyToCoupleCapture(sender, requestId, captured);
        };

        // A force-selected name arrives here with no local queue bookkeeping to do — it's resolved
        // against what's actually available on *this* side: a saved preset by name, then (for a
        // Penumbra-discovered pose) by the mod/group/option/trigger hash quad it carries — resilient
        // to the mod having been renamed locally since — falling back to matching its "ModName —
        // OptionName" label only if the hash match fails too. Tells the player (rather than silently
        // doing nothing) if none of these resolve — see couple-pairing's Partner Pose Not Found
        // Notice requirement.
        PairingListener.ForceSelectionReceived += (_, name, hashes) =>
        {
            var preset = PresetManager.Presets.FirstOrDefault(p => p.Name == name);
            if (preset != null) { PlayPreset(preset); return; }
            if (PenumbraPosePanel.TryPlayByHash(this, hashes)) return;
            if (PenumbraPosePanel.TryPlayByLabel(this, name)) return;
            ChatGui.Print($"[PoseKit] Partner picked \"{name}\", but it wasn't found in your list.");
        };

        // Announces this side's current override-toggle state once whenever pairing activates — the
        // toggle itself already sends on every change, but a toggle set before pairing existed (or
        // set during a prior pairing) would otherwise never reach a newly-paired partner. Only sent
        // when the state is actually on: the overwhelmingly common default-off case has nothing worth
        // announcing, and sending it anyway was pure unsolicited-message clutter on every pairing —
        // see couple-pairing's Pairing Override State Announcement requirement (pairing-align-polish).
        var wasPairingActive = false;
        PairingState.Changed += () =>
        {
            if (PairingState.Active && !wasPairingActive && PairingState.LocalOverrideEnabled)
                PairingListener.SendOverrideToggle(PairingState.LocalOverrideEnabled);
            wasPairingActive = PairingState.Active;
        };

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        WelcomeWindow = new WelcomeWindow(this) { IsOpen = !Configuration.HasSeenWelcome };

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(WelcomeWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the PoseKit window. '/posekit tfc' toggles freecam. '/posekit sync [delay <seconds>]' resyncs nearby rendered player emotes. '/posekit align' walks to your target's exact position."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        RefreshPenumbraPoses();
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        FreeCam.Dispose();
        AlignService.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        WelcomeWindow.Dispose();
        EmoteSync.Dispose();
        OffsetEngine.Dispose();
        CoupleQueueService.Dispose();
        CouplePresetCaptureService.Dispose();
        CoupleRelayInbox.Dispose();
        CoupleRelayOutbox.Dispose();
        PairingListener.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        FreeCam.Tick((float)framework.UpdateDelta.TotalSeconds);
        AlignService.Tick();
        var localPlayer = ObjectTable.LocalPlayer;
        var currentPose = PoseIdentifier.FromCharacter(localPlayer);
        RestorePoseIfDropped(currentPose);

        // Auto-clear once the character leaves the pose/emote loop entirely — without this, a
        // leftover offset keeps fighting the game's own draw-offset updates during normal
        // movement (turning, walking) indefinitely, since the hook re-applies it on every write
        // regardless of what's actually playing. Mirrors SimpleHeels clearing its temp offset on
        // emote change (SimpleHeels-master/Plugin.cs). Checked via PoseTrigger.HasAppliedOffset,
        // not OffsetEngine.Active alone — bridging to SimpleHeels deliberately leaves the latter
        // false to avoid double-applying the offset. Also gated on lastKnownPoseForFreecamRestore
        // being clear, so this doesn't drop the offset while a freecam restore attempt is still
        // in flight above.
        if (PoseTrigger.HasAppliedOffset && currentPose == null && lastKnownPoseForFreecamRestore == null)
        {
            PoseTrigger.ClearOffset(localPlayer);
            LoadedPreset = null;
            LastPlayedPenumbraContext = null;
        }

        // One-shot: the first time SimpleHeels is ever observed loaded (which may not be until well
        // after PoseKit's own constructor runs — plugin load order isn't guaranteed), default the
        // bridge on. HasOfferedSimpleHeelsBridge stops this from re-enabling it if the user turns it
        // back off afterward.
        if (!Configuration.HasOfferedSimpleHeelsBridge && SimpleHeelsBridge.IsLoaded)
        {
            Configuration.BridgeOffsetToSimpleHeels = true;
            Configuration.HasOfferedSimpleHeelsBridge = true;
            Configuration.Save();
        }

        // Same one-shot-retry idea for the Animations tab: keep checking every frame until the
        // local player's Penumbra collection actually resolves (the constructor's own eager scan
        // can miss this on a fresh full game launch), then scan exactly once and stop checking.
        if (!hasScannedPenumbraPoses && PenumbraIpc.TryGetLocalPlayerCollectionId() != null)
        {
            RefreshPenumbraPoses();
            hasScannedPenumbraPoses = true;
        }

        OffsetEngine.Tick(localPlayer);
        PoseTrigger.Tick();
        PairingListener.Tick();
        CoupleQueueService.Tick();
        CouplePresetCaptureService.Tick();
        CoupleRelayInbox.Tick();
        CoupleRelayOutbox.Tick();
    }

    /// Defensive fallback for a sit/groundsit/doze loop dropping back to Character->Mode Normal
    /// with no PoseKit code involved and no real player movement — FreeCamInput's own
    /// EmoteController.cancelEmote hook is the primary defense (see
    /// openspec/changes/preserve-emote-during-freecam) and should mean this rarely fires. Re-enters
    /// the same pose when it does happen, with a bounded retry budget in case something keeps
    /// re-triggering the drop while the player keeps rotating the freecam. Does nothing outside
    /// that specific situation — a real movement-triggered exit, or any exit while freecam is off,
    /// is left alone.
    private void RestorePoseIfDropped(PoseIdentifier? currentPose)
    {
        if (currentPose != null)
        {
            lastKnownPoseForFreecamRestore = currentPose;
            freecamRestoreAttempts = 0;
            return;
        }

        if (lastKnownPoseForFreecamRestore is not { } droppedPose) return;

        if (!FreeCam.Enabled || FreeCam.MovementKeyHeld)
        {
            lastKnownPoseForFreecamRestore = null;
            return;
        }

        if (Environment.TickCount64 < nextFreecamRestoreAttemptTime) return;

        if (freecamRestoreAttempts >= MaxFreecamRestoreAttempts)
        {
            lastKnownPoseForFreecamRestore = null;
            return;
        }

        PoseTrigger.RestorePose(droppedPose);
        freecamRestoreAttempts++;
        nextFreecamRestoreAttemptTime = Environment.TickCount64 + FreecamRestoreAttemptDelayMs;
    }

    /// Resets every temporary Penumbra setting PoseKit itself applied this session before
    /// re-scanning, so browsing/re-discovering poses starts from Penumbra's own default state
    /// instead of leaving behind whatever was last enabled/selected while clicking around.
    public void RefreshPenumbraPoses()
    {
        PenumbraIpc.ResetAllTemporarySettings();
        DiscoveredPoses = PenumbraPoseScanner.Scan();
        ExternalPoseClaims = PenumbraPoseScanner.ScanExternalConflicts();
    }

    /// Replays a saved preset: if it's linked to a Penumbra mod, re-applies that mod's group
    /// selections (enabling it if needed) and forces a redraw before triggering the pose, so the
    /// right animation is actually active by the time the character enters it — not just the offset.
    public void PlayPreset(NamedPose pose)
    {
        PlayPose(pose.Pose, pose.Offset, pose.Anchor, pose.Penumbra);
        LoadedPreset = pose;
    }

    /// Plays a preset that carries a captured partner half (see NamedPose.PartnerHalf). Only while
    /// paired with the exact partner it was captured from: walks to them first if auto-align is on,
    /// then relays their half for accept/deny (auto-accepted under mutual override), and plays this
    /// side's own half only once they accept — both halves start together. See CoupleRelayOutbox.
    /// Paired with someone else, or not paired at all, only this side's own half plays, immediately,
    /// and nothing is relayed.
    public void PlayCouplePreset(NamedPose pose)
    {
        if (pose.PartnerHalf is { } half && PairingState.Active && PairingState.Peer is { } peer && peer.Equals(half.Partner))
        {
            CoupleRelayOutbox.Start(pose, Configuration.AutoAlignBeforePlay);
            return;
        }

        PlayPreset(pose);
    }

    /// Applies a captured pose/offset/anchor/Penumbra state directly — the same best-effort pipeline
    /// PlayPreset uses, just against loose fields instead of a saved NamedPose. Used for an accepted
    /// (or mutual-override auto-accepted) relayed partner half, which isn't itself a saved preset.
    public void ApplyCapturedPartnerState(CapturedPoseState captured) =>
        PlayPose(captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra, silent: true);

    private void PlayPose(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, PenumbraLink? penumbra, bool silent = false)
    {
        if (penumbra is { } link && !TryApplyPenumbraLink(link) && link.ModDirectory.StartsWith('#'))
        {
            // Only a partner-originated link (hashed ModDirectory — see ResolveModDirectory) gets a
            // not-found notice: a local preset's own linked mod going missing is a separate, existing
            // situation this change doesn't touch. See couple-pairing's Partner Pose Not Found Notice.
            var modLabel = link.ModName.Length > 0 ? link.ModName : "a mod";
            ChatGui.Print($"[PoseKit] Partner's pose uses {modLabel}, which wasn't found in your list.");
        }

        PoseTrigger.Trigger(pose, offset, anchor, silent);
    }

    /// Applies a Penumbra link's mod/group/option selection, enabling the mod if needed — true only
    /// once the redirect was actually applied. False covers every failure mode (mod not resolvable
    /// locally, ambiguous hash, or Penumbra's own TrySetTemporarySettings call failing), so PlayPose
    /// can tell a real failure from a successful apply.
    private bool TryApplyPenumbraLink(PenumbraLink link)
    {
        if (PenumbraIpc.TryGetLocalPlayerCollectionId() is not { } collectionId) return false;
        if (ResolveModDirectory(link.ModDirectory) is not { } modDirectory) return false;

        var selections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (group, options) in link.GroupSelections)
            selections[group] = options;

        if (link.GroupName.StartsWith('#') && ResolveGroupOption(modDirectory, link.GroupName, link.OptionName) is { } resolved)
            selections[resolved.GroupName] = [resolved.OptionName];

        // Preserve whatever priority the user already has set for this mod in Penumbra — the temporary-
        // settings API takes priority as a required value with no "leave it alone" option, so it has to
        // be read back and passed through explicitly or it gets silently reset to whatever's passed.
        var (_, priority, _) = PenumbraIpc.TryGetCurrentSettings(collectionId, modDirectory);
        if (!PenumbraIpc.TrySetTemporarySettings(collectionId, modDirectory, true, priority, selections)) return false;

        PenumbraIpc.TryRedrawLocalPlayer();
        return true;
    }

    /// A partner half's ModDirectory travels over the wire as "#&lt;hash&gt;" (see
    /// PairingComposer.ComposeCapturedStateTail / ModDirectoryHash) rather than its literal path,
    /// since only the client whose own mod it names can resolve it — never the peer relaying it in
    /// between. Anything without the "#" prefix is a preset's own half, captured locally and never
    /// hashed, so it's used as-is. Resolves via a linear scan of this client's own installed mods;
    /// no match, or more than one (an astronomically unlikely hash collision), is treated the same as
    /// "mod no longer available" — best-effort per couple-preset-relay's spec.
    private string? ResolveModDirectory(string modDirectory)
    {
        if (!modDirectory.StartsWith('#')) return modDirectory;

        var hash = modDirectory[1..];
        if (PenumbraIpc.TryGetModList() is not { } modList) return null;

        string? match = null;
        foreach (var directory in modList.Keys)
        {
            if (ModDirectoryHash.Compute(directory) != hash) continue;
            if (match != null) return null; // ambiguous — fail closed rather than guess
            match = directory;
        }
        return match;
    }

    /// A partner half's GroupName/OptionName travel over the wire as "#&lt;hash&gt;" too (see
    /// PairingComposer.ComposeCapturedStateTail), resolved here against the already-resolved local
    /// mod's own meta.json group/option list (PenumbraPoseScanner.TryReadGroups) — only meaningful
    /// once ResolveModDirectory has already found which mod this is. Resolves the group first, then
    /// the option within only that matched group (not the mod's other groups), so an option name
    /// reused across two different groups of the same mod can't cross-match. No match, or more than
    /// one, at either stage is treated as unresolved and simply skipped — same fail-closed policy as
    /// ResolveModDirectory, and the same "no selection applied for this group" degrade as today's
    /// existing implicit/no-group case.
    private (string GroupName, string OptionName)? ResolveGroupOption(string modDirectory, string groupNameToken, string optionNameToken)
    {
        var groupHash = groupNameToken[1..];
        var optionHash = optionNameToken.StartsWith('#') ? optionNameToken[1..] : optionNameToken;

        if (PenumbraIpc.TryGetModDirectory() is not { } modRoot) return null;
        if (PoseKit.Penumbra.PenumbraPoseScanner.TryReadGroups(modRoot, modDirectory) is not { } groups) return null;

        var matchedGroups = groups.Where(g => ModDirectoryHash.Compute(g.GroupName) == groupHash).ToList();
        if (matchedGroups.Count != 1) return null; // no match, or ambiguous — fail closed rather than guess
        var resolvedGroup = matchedGroups[0];

        var matchedOptions = resolvedGroup.OptionNames.Where(o => ModDirectoryHash.Compute(o) == optionHash).ToList();
        return matchedOptions.Count == 1 ? (resolvedGroup.GroupName, matchedOptions[0]) : null;
    }

    private void OnCommand(string command, string args)
    {
        var splitArgs = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (splitArgs.Length == 0)
        {
            MainWindow.Toggle();
            return;
        }

        if (string.Equals(splitArgs[0], "tfc", StringComparison.OrdinalIgnoreCase))
        {
            if (splitArgs.Length != 1)
                ChatGui.PrintError("[PoseKit] Usage: /posekit tfc");
            else
            {
                FreeCam.Toggle();
                ChatGui.Print($"[PoseKit] {FreeCam.Status}");
            }
            return;
        }

        if (string.Equals(splitArgs[0], "align", StringComparison.OrdinalIgnoreCase))
        {
            if (splitArgs.Length != 1)
                ChatGui.PrintError("[PoseKit] Usage: /posekit align");
            else
                AlignService.AlignToTarget();
            return;
        }

        if (!string.Equals(splitArgs[0], "sync", StringComparison.OrdinalIgnoreCase))
        {
            ChatGui.PrintError($"[PoseKit] Unknown command: {splitArgs[0]}");
            return;
        }

        var error = EmoteSync.HandleArgs(splitArgs[1..]);
        if (error != null)
            ChatGui.PrintError($"[PoseKit] {error}");
    }

    /// Builds this side's own reply to a partner's capture request: current pose/offset, own anchor
    /// (per the request's anchor-kind hint), and own Penumbra mod/group/option — reduced to just the
    /// one relevant group/option pair (not the mod's full GroupSelections), since that's all the
    /// receiving side needs to re-enable the same selection. Null if this side isn't currently in a
    /// pose — nothing meaningful to capture.
    private CapturedPoseState? TryCaptureOwnStateForPartnerRequest(AnchorHint hint)
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (PoseIdentifier.FromCharacter(localPlayer) is not { } pose || localPlayer == null) return null;

        PresetAnchor? anchor = hint.AnchorKind switch
        {
            1 => PresetAnchor.FromSpot(LocationAnchor.Capture(localPlayer, ClientState.TerritoryType)),
            2 => TryCaptureOwnFurnitureAnchor(localPlayer, hint.FurnitureEntryId),
            _ => null,
        };

        PenumbraLink? penumbra = null;
        if (LastPlayedPenumbraContext is { ModDirectory.Length: > 0 } link)
        {
            penumbra = new PenumbraLink
            {
                ModDirectory = link.ModDirectory, ModName = link.ModName, GroupName = link.GroupName, OptionName = link.OptionName,
            };
            if (link.GroupName.Length > 0)
                penumbra.GroupSelections[link.GroupName] = [link.OptionName];
        }

        return new CapturedPoseState(pose, OffsetEngine.DesiredOffset, anchor, penumbra);
    }

    /// Finds the nearest currently-live furniture instance matching the synced EntryId and captures
    /// this side's own position/rotation relative to it — null if none is nearby (the receiving side
    /// may simply not be standing near the same furniture yet).
    private PresetAnchor? TryCaptureOwnFurnitureAnchor(IPlayerCharacter localPlayer, uint entryId)
    {
        var nearby = furnitureScanner.ScanNearby(localPlayer);
        var match = nearby.Where(f => f.EntryId == entryId)
            .OrderBy(f => Vector3.Distance(f.Position, localPlayer.Position))
            .Cast<NearbyFurniture?>()
            .FirstOrDefault();
        return match is { } furniture ? PresetAnchor.FromFurniture(FurnitureAnchor.Capture(localPlayer, furniture)) : null;
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
