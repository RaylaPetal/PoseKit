using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PoseKit.Penumbra;
using PoseKit.Presets;
using PoseKit.Sync;
using PoseKit.Windows;

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

    private const string CommandName = "/posekit";

    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PoseKit");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private WelcomeWindow WelcomeWindow { get; init; }

    public EmoteSyncCommand EmoteSync { get; init; }

    public OffsetEngine OffsetEngine { get; init; }
    public PresetManager PresetManager { get; init; }
    public PoseTrigger PoseTrigger { get; init; }
    public SimpleHeelsBridge SimpleHeelsBridge { get; init; }
    public PenumbraIpc PenumbraIpc { get; init; }
    public PenumbraPoseScanner PenumbraPoseScanner { get; init; }
    public List<PoseModInfo> DiscoveredPoses { get; private set; } = new();

    /// The preset currently loaded into the live-offset editor, if any — lets the UI offer
    /// "update this preset" instead of only ever "save as new".
    public NamedPose? LoadedPreset { get; set; }

    /// The Penumbra mod/group state a Play action in the Penumbra panel last put in place, if any —
    /// attached to the next saved preset so replaying it can restore that mod state too, not just
    /// the offset. Best-effort: goes stale if the user changes Penumbra settings some other way
    /// afterward, same as any other snapshot.
    public PenumbraLink? LastPlayedPenumbraContext { get; set; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        EmoteSync = new EmoteSyncCommand();

        OffsetEngine = new OffsetEngine();
        PresetManager = new PresetManager(Configuration);
        SimpleHeelsBridge = new SimpleHeelsBridge();
        PoseTrigger = new PoseTrigger(Configuration, OffsetEngine, SimpleHeelsBridge);
        PenumbraIpc = new PenumbraIpc();
        PenumbraPoseScanner = new PenumbraPoseScanner(PenumbraIpc, Configuration);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        WelcomeWindow = new WelcomeWindow(this) { IsOpen = !Configuration.HasSeenWelcome };

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(WelcomeWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the PoseKit window. Use '/posekit sync [delay <seconds>]' to resync nearby rendered player emotes."
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
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        WelcomeWindow.Dispose();
        EmoteSync.Dispose();
        OffsetEngine.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
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

        OffsetEngine.Tick(localPlayer);
        PoseTrigger.Tick();
    }

    public void RefreshPenumbraPoses()
    {
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

        if (!string.Equals(splitArgs[0], "sync", StringComparison.OrdinalIgnoreCase))
        {
            ChatGui.PrintError($"[PoseKit] Unknown command: {splitArgs[0]}");
            return;
        }

        var error = EmoteSync.HandleArgs(splitArgs[1..]);
        if (error != null)
            ChatGui.PrintError($"[PoseKit] {error}");
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
