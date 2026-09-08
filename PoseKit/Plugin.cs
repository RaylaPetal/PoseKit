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

    private const string CommandName = "/posekit";

    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PoseKit");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private WelcomeWindow WelcomeWindow { get; init; }

    public EmoteSyncCommand EmoteSync { get; init; }
    public FreeCamService FreeCam { get; init; }

    public OffsetEngine OffsetEngine { get; init; }
    public PresetManager PresetManager { get; init; }
    public PoseTrigger PoseTrigger { get; init; }
    public SimpleHeelsBridge SimpleHeelsBridge { get; init; }
    public PenumbraIpc PenumbraIpc { get; init; }
    public PenumbraPoseScanner PenumbraPoseScanner { get; init; }
    public List<PoseModInfo> DiscoveredPoses { get; private set; } = new();

    public PairingState PairingState { get; init; }
    public PairingListener PairingListener { get; init; }
    public CoupleQueueService CoupleQueueService { get; init; }

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

    /// Used only to resolve a synced furniture anchor's EntryId against this side's own nearby
    /// furniture (see the PresetSyncReceived wiring below) — PresetButtonsPanel and PoseTrigger each
    /// keep their own instance for the same reason (a live furniture scan can't be cached).
    private readonly FurnitureScanner furnitureScanner = new();

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        EmoteSync = new EmoteSyncCommand();
        FreeCam = new FreeCamService();

        OffsetEngine = new OffsetEngine();
        PresetManager = new PresetManager(Configuration);
        SimpleHeelsBridge = new SimpleHeelsBridge();
        PoseTrigger = new PoseTrigger(Configuration, OffsetEngine, SimpleHeelsBridge);
        PenumbraIpc = new PenumbraIpc();
        PenumbraPoseScanner = new PenumbraPoseScanner(PenumbraIpc, Configuration);

        PairingState = new PairingState();
        PairingListener = new PairingListener(PairingState);
        CoupleQueueService = new CoupleQueueService(PairingState, PairingListener);

        // A preset saved while paired lands here for the receiving side: capture *this* side's own
        // currently-playing pose/offset/Penumbra link — AND its own anchor (own current spot, or own
        // position relative to a matching nearby furniture item) — under the synced name, never the
        // sender's, since each side of a couple pose is typically already sitting/standing in its own
        // different spot (own mod/option, own offset, own seat on the same sofa) by the time either
        // one saves it as a preset; reusing the sender's captured position/rotation would anchor this
        // side to the SENDER's spot instead of its own. Silently does nothing if this side isn't
        // currently in a pose, or (for a furniture anchor) can't find a matching item nearby — there's
        // nothing meaningful to capture.
        PairingListener.PresetSyncReceived += (_, payload) =>
        {
            if (PoseIdentifier.FromCharacter(ObjectTable.LocalPlayer) is not { } pose) return;
            var localPlayer = ObjectTable.LocalPlayer;
            if (localPlayer == null) return;

            PresetAnchor? anchor = payload.AnchorKind switch
            {
                1 => PresetAnchor.FromSpot(LocationAnchor.Capture(localPlayer, ClientState.TerritoryType)),
                2 => TryCaptureOwnFurnitureAnchor(localPlayer, payload.FurnitureEntryId),
                _ => null,
            };
            PresetManager.Save(payload.Name, pose, OffsetEngine.DesiredOffset, LastPlayedPenumbraContext, anchor);
        };

        // A force-selected name arrives here with no local queue bookkeeping to do — it's resolved
        // against what's actually available on *this* side (a saved preset by name, then a
        // Penumbra-discovered animation by its "ModName — OptionName" label) and played immediately
        // if found. Silently does nothing if neither resolves — the receiving side may simply not
        // have that preset or mod, same best-effort tolerance as PresetSyncReceived above.
        PairingListener.ForceSelectionReceived += (_, name) =>
        {
            var preset = PresetManager.Presets.FirstOrDefault(p => p.Name == name);
            if (preset != null) { PlayPreset(preset); return; }
            PenumbraPosePanel.TryPlayByLabel(this, name);
        };

        // Announces this side's current override-toggle state once whenever pairing activates — the
        // toggle itself already sends on every change, but a toggle set before pairing existed (or
        // set during a prior pairing) would otherwise never reach a newly-paired partner.
        var wasPairingActive = false;
        PairingState.Changed += () =>
        {
            if (PairingState.Active && !wasPairingActive)
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
            HelpMessage = "Toggle the PoseKit window. '/posekit tfc' toggles freecam. '/posekit sync [delay <seconds>]' resyncs nearby rendered player emotes."
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
        PairingListener.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        FreeCam.Tick((float)framework.UpdateDelta.TotalSeconds);
        var localPlayer = ObjectTable.LocalPlayer;

        // Auto-clear once the character leaves the pose/emote loop entirely — without this, a
        // leftover offset keeps fighting the game's own draw-offset updates during normal
        // movement (turning, walking) indefinitely, since the hook re-applies it on every write
        // regardless of what's actually playing. Mirrors SimpleHeels clearing its temp offset on
        // emote change (SimpleHeels-master/Plugin.cs). Checked via PoseTrigger.HasAppliedOffset,
        // not OffsetEngine.Active alone — bridging to SimpleHeels deliberately leaves the latter
        // false to avoid double-applying the offset.
        if (PoseTrigger.HasAppliedOffset && PoseIdentifier.FromCharacter(localPlayer) == null)
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
        CoupleQueueService.Tick();
    }

    /// Resets every temporary Penumbra setting PoseKit itself applied this session before
    /// re-scanning, so browsing/re-discovering poses starts from Penumbra's own default state
    /// instead of leaving behind whatever was last enabled/selected while clicking around.
    public void RefreshPenumbraPoses()
    {
        PenumbraIpc.ResetAllTemporarySettings();
        DiscoveredPoses = PenumbraPoseScanner.Scan();
    }

    /// Replays a saved preset: if it's linked to a Penumbra mod, re-applies that mod's group
    /// selections (enabling it if needed) and forces a redraw before triggering the pose, so the
    /// right animation is actually active by the time the character enters it — not just the offset.
    public void PlayPreset(NamedPose pose)
    {
        if (pose.Penumbra is { } link && PenumbraIpc.TryGetLocalPlayerCollectionId() is { } collectionId)
        {
            var selections = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var (group, options) in link.GroupSelections)
                selections[group] = options;

            if (PenumbraIpc.TrySetTemporarySettings(collectionId, link.ModDirectory, true, selections))
                PenumbraIpc.TryRedrawLocalPlayer();
        }

        LoadedPreset = pose;
        PoseTrigger.Trigger(pose);
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

        if (!string.Equals(splitArgs[0], "sync", StringComparison.OrdinalIgnoreCase))
        {
            ChatGui.PrintError($"[PoseKit] Unknown command: {splitArgs[0]}");
            return;
        }

        var error = EmoteSync.HandleArgs(splitArgs[1..]);
        if (error != null)
            ChatGui.PrintError($"[PoseKit] {error}");
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
