using System;
using System.Collections.Generic;
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

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PoseKit");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public EmoteSyncCommand EmoteSync { get; init; }

    public OffsetEngine OffsetEngine { get; init; }
    public PresetManager PresetManager { get; init; }
    public PoseTrigger PoseTrigger { get; init; }
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
        PoseTrigger = new PoseTrigger(OffsetEngine);
        PenumbraIpc = new PenumbraIpc();
        PenumbraPoseScanner = new PenumbraPoseScanner(PenumbraIpc, Configuration);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the PoseKit window. Use '/posekit sync [delay <seconds>]' to resync your emote loop timer."
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
        // emote change (SimpleHeels-master/Plugin.cs).
        if (OffsetEngine.Active && PoseIdentifier.FromCharacter(localPlayer) == null)
        {
            OffsetEngine.Reset(localPlayer);
            LoadedPreset = null;
            LastPlayedPenumbraContext = null;
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
