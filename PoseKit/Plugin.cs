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

    public OffsetEngine OffsetEngine { get; init; }
    public PresetManager PresetManager { get; init; }
    public PoseTrigger PoseTrigger { get; init; }
    public SimpleHeelsBridge SimpleHeelsBridge { get; init; }
    public PenumbraIpc PenumbraIpc { get; init; }
    public PenumbraPoseScanner PenumbraPoseScanner { get; init; }
    public List<PoseModInfo> DiscoveredPoses { get; private set; } = new();

    /// Poses claimed by unscanned mods, used only for conflict warnings.
    public Dictionary<PoseIdentifier, List<PenumbraPoseScanner.ExternalPoseClaim>> ExternalPoseClaims { get; private set; } = new();

    public PairingState PairingState { get; init; }
    public PairingListener PairingListener { get; init; }
    public CoupleQueueService CoupleQueueService { get; init; }
    public CouplePresetCaptureService CouplePresetCaptureService { get; init; }
    public CoupleRelayInbox CoupleRelayInbox { get; init; }
    public CoupleRelayOutbox CoupleRelayOutbox { get; init; }
    public Bones.BoneAlignService BoneAlign { get; init; }
    public Bones.AlignmentMemory AlignmentMemory { get; init; }
    public Bones.AutoAlignCoordinator AutoAlign { get; init; }

    /// The preset loaded into the live-offset editor, so it can be updated in place.
    public NamedPose? LoadedPreset { get; set; }

    /// The mod state the last Play set, saved into the next preset.
    public PenumbraLink? LastPlayedPenumbraContext { get; set; }

    public Bones.PlayContext? CurrentPlayContext { get; private set; }

    /// Call right after triggering, so the context carries the play's OffsetGeneration.
    public void SetPlayContext(string modDirectory, string modName, string group, string option, string trigger,
        PoseIdentifier? pose, bool fromPartnerAnchoredPreset = false) =>
        CurrentPlayContext = new Bones.PlayContext(modDirectory, modName, group, option, trigger, pose,
            fromPartnerAnchoredPreset, PoseTrigger.OffsetGeneration, Environment.TickCount64);

    /// On game launch Penumbra may not be ready when the constructor scans, so retry once it is.
    private bool hasScannedPenumbraPoses;

    private readonly FurnitureScanner furnitureScanner = new();

    /// Last pose seen, so a pose dropped during freecam can be re-entered.
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

        PresetManager = new PresetManager(Configuration);
        SimpleHeelsBridge = new SimpleHeelsBridge();
        PoseTrigger = new PoseTrigger(Configuration, OffsetEngine, SimpleHeelsBridge);
        PenumbraIpc = new PenumbraIpc();
        PenumbraPoseScanner = new PenumbraPoseScanner(PenumbraIpc, Configuration);

        PairingListener = new PairingListener(PairingState);
        CoupleQueueService = new CoupleQueueService(PairingState, PairingListener);
        CouplePresetCaptureService = new CouplePresetCaptureService(PairingState, PairingListener, PresetManager);
        CouplePresetCaptureService.Saved += saved => LoadedPreset = saved;
        CoupleRelayInbox = new CoupleRelayInbox(PairingState, PairingListener);
        CoupleRelayInbox.AutoApply += ApplyCapturedPartnerState;
        CoupleRelayOutbox = new CoupleRelayOutbox(PairingState, PairingListener, PlayPreset);
        BoneAlign = new Bones.BoneAlignService(Configuration, PairingState, PairingListener, PoseTrigger, OffsetEngine, EmoteSync);
        AlignmentMemory = new Bones.AlignmentMemory(PluginInterface.GetPluginConfigDirectory());
        AutoAlign = new Bones.AutoAlignCoordinator(this);

        // Reply with this side's own pose state; send nothing when not in a pose.
        PairingListener.PartnerCaptureRequested += (sender, requestId, hint) =>
        {
            if (TryCaptureOwnStateForPartnerRequest(hint) is { } captured)
                PairingListener.ReplyToCoupleCapture(sender, requestId, captured);
        };

        // Resolve a forced pick: preset by name, then mod pose by hash, then by label.
        PairingListener.ForceSelectionReceived += (_, name, hashes) =>
        {
            var preset = PresetManager.Presets.FirstOrDefault(p => p.Name == name);
            if (preset != null) { PlayPreset(preset); return; }
            if (PenumbraPosePanel.TryPlayByHash(this, hashes)) return;
            if (PenumbraPosePanel.TryPlayByLabel(this, name)) return;
            ChatGui.Print($"[PoseKit] Partner picked \"{name}\", but it wasn't found in your list.");
        };

        // Tell a new partner about an override that was already on before pairing.
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
            HelpMessage = "Toggle the PoseKit window.\n" +
                          "/posekit tfc → Toggle freecam.\n" +
                          "/posekit sync [delay <seconds>] → Resync nearby players' emotes."
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
        CouplePresetCaptureService.Dispose();
        CoupleRelayInbox.Dispose();
        CoupleRelayOutbox.Dispose();
        AutoAlign.Dispose();
        BoneAlign.Dispose();
        PairingListener.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        FreeCam.Tick((float)framework.UpdateDelta.TotalSeconds);
        var localPlayer = ObjectTable.LocalPlayer;
        var currentPose = PoseIdentifier.FromCharacter(localPlayer);
        RestorePoseIfDropped(currentPose);
        UpdatePlayContext(currentPose);

        // Clear the offset once the character leaves the pose, unless a freecam restore is pending.
        if (PoseTrigger.HasAppliedOffset && currentPose == null && lastKnownPoseForFreecamRestore == null)
        {
            PoseTrigger.ClearOffset(localPlayer);
            LoadedPreset = null;
            LastPlayedPenumbraContext = null;
            CurrentPlayContext = null;
        }

        // Turn the bridge on the first time SimpleHeels is seen; never again after that.
        if (!Configuration.HasOfferedSimpleHeelsBridge && SimpleHeelsBridge.IsLoaded)
        {
            Configuration.BridgeOffsetToSimpleHeels = true;
            Configuration.HasOfferedSimpleHeelsBridge = true;
            Configuration.Save();
        }

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
        BoneAlign.Tick();
        AutoAlign.Tick();
    }

    // A context this fresh is kept while the pose cycles or hands over between emotes.
    private const long FreshPlayContextMs = 8000;

    private PoseIdentifier? lastPoseForPlayContext;

    /// Tracks poses PoseKit didn't start. A hand-typed emote gets a context only when exactly one
    /// selected option claims its pose.
    private void UpdatePlayContext(PoseIdentifier? currentPose)
    {
        if (currentPose == lastPoseForPlayContext) return;
        var previous = lastPoseForPlayContext;
        lastPoseForPlayContext = currentPose;

        var context = CurrentPlayContext;
        var fresh = context != null && Environment.TickCount64 - context.SetAt < FreshPlayContextMs;

        if (currentPose is not { } pose)
        {
            if (previous != null && !fresh)
                CurrentPlayContext = null;
            return;
        }

        if (PoseTrigger.IsCycling || fresh || (context != null && context.Pose == pose))
            return;

        CurrentPlayContext = null;
        if (!ActivePoseMap.Build(DiscoveredPoses).TryGetValue(pose, out var claimants)) return;
        var options = claimants.DistinctBy(c => c.Option).ToList();
        if (options.Count != 1) return;

        var (mod, option) = options[0];
        if (mod.Groups.FirstOrDefault(g => g.Options.Contains(option)) is not { } group) return;
        var triggerIndex = option.Triggers.FindIndex(t => t.PoseIdentifier == pose);
        if (triggerIndex < 0) return;
        var trigger = option.Triggers[triggerIndex];
        SetPlayContext(mod.ModDirectory, mod.ModName, group.IsImplicit ? "" : group.Name, option.Name,
            PenumbraPosePanel.TriggerText(trigger), pose);
    }

    /// Fallback for a pose dropped during freecam without the player moving: re-enters it, with a
    /// few retries.
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

    /// Resets PoseKit's temporary Penumbra settings, then rescans.
    public void RefreshPenumbraPoses()
    {
        PenumbraIpc.ResetAllTemporarySettings();
        DiscoveredPoses = PenumbraPoseScanner.Scan();
        ExternalPoseClaims = PenumbraPoseScanner.ScanExternalConflicts();
    }

    /// Re-applies the preset's mod selection, then triggers the pose.
    public void PlayPreset(NamedPose pose)
    {
        PlayPose(pose.Pose, pose.Offset, pose.Anchor, pose.Penumbra);
        LoadedPreset = pose;
    }

    /// Paired with the partner it was captured from: relays their half and waits for the answer.
    /// Otherwise only this side's half plays.
    public void PlayCouplePreset(NamedPose pose)
    {
        if (pose.PartnerHalf is { } half && PairingState.Active && PairingState.Peer is { } peer && peer.Equals(half.Partner))
        {
            CoupleRelayOutbox.Start(pose);
            return;
        }

        PlayPreset(pose);
    }

    /// Plays an accepted partner half.
    public void ApplyCapturedPartnerState(CapturedPoseState captured) =>
        PlayPose(captured.Pose, captured.Offset, captured.Anchor, captured.Penumbra, silent: true);

    private void PlayPose(PoseIdentifier pose, PoseOffset offset, PresetAnchor? anchor, PenumbraLink? penumbra, bool silent = false)
    {
        if (penumbra is { } link && !TryApplyPenumbraLink(link) && link.ModDirectory.StartsWith('#'))
        {
            // Only a partner's (hashed) link gets the notice.
            var modLabel = link.ModName.Length > 0 ? link.ModName : "a mod";
            ChatGui.Print($"[PoseKit] Partner's pose uses {modLabel}, which wasn't found in your list.");
        }

        PoseTrigger.Trigger(pose, offset, anchor, silent);
        SetPresetPlayContext(pose, anchor, penumbra);
    }

    /// Sets a context only when the link resolves to exactly one trigger for the pose.
    private void SetPresetPlayContext(PoseIdentifier pose, PresetAnchor? anchor, PenumbraLink? penumbra)
    {
        CurrentPlayContext = null;
        if (penumbra is not { ModDirectory.Length: > 0 } link || ResolveModDirectory(link.ModDirectory) is not { } modDirectory) return;
        if (DiscoveredPoses.FirstOrDefault(m => m.ModDirectory == modDirectory) is not { } mod) return;

        var matches = new List<(PoseModGroup Group, PoseModOption Option, PoseTriggerHint Trigger)>();
        foreach (var group in mod.Groups)
        {
            var groupMatches = link.GroupName.Length == 0 ? group.IsImplicit
                : link.GroupName.StartsWith('#') ? ModDirectoryHash.Compute(group.Name) == link.GroupName[1..]
                : group.Name == link.GroupName;
            if (!groupMatches) continue;

            foreach (var option in group.Options)
            {
                var optionMatches = group.IsImplicit
                    || (link.OptionName.StartsWith('#') ? ModDirectoryHash.Compute(option.Name) == link.OptionName[1..] : option.Name == link.OptionName);
                if (!optionMatches) continue;

                foreach (var trigger in option.Triggers)
                {
                    if (trigger.PoseIdentifier == pose)
                        matches.Add((group, option, trigger));
                }
            }
        }

        if (matches.Count != 1) return;
        var (g, o, t) = matches[0];
        SetPlayContext(mod.ModDirectory, mod.ModName, g.IsImplicit ? "" : g.Name, o.Name,
            PenumbraPosePanel.TriggerText(t), pose, fromPartnerAnchoredPreset: anchor?.Partner != null);
    }

    /// Enables the linked mod and option. False on any failure.
    private bool TryApplyPenumbraLink(PenumbraLink link)
    {
        if (PenumbraIpc.TryGetLocalPlayerCollectionId() is not { } collectionId) return false;
        if (ResolveModDirectory(link.ModDirectory) is not { } modDirectory) return false;

        var selections = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (group, options) in link.GroupSelections)
            selections[group] = options;

        if (link.GroupName.StartsWith('#') && ResolveGroupOption(modDirectory, link.GroupName, link.OptionName) is { } resolved)
            selections[resolved.GroupName] = [resolved.OptionName];

        // Keep the user's priority; the API has no way to leave it unchanged.
        var (_, priority, _) = PenumbraIpc.TryGetCurrentSettings(collectionId, modDirectory);
        if (!PenumbraIpc.TrySetTemporarySettings(collectionId, modDirectory, true, priority, selections)) return false;

        PenumbraIpc.TryRedrawLocalPlayer();
        return true;
    }

    /// Resolves a partner's "#&lt;hash&gt;" against local mods; plain names are returned as-is. No
    /// match or an ambiguous one gives null.
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

    /// Resolves hashed group and option names within an already-resolved mod. The option is only
    /// matched inside its group. No match or an ambiguous one gives null.
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

    /// "/posekit bones": logs every bone of the player and their target to /xllog.
    private void DumpBones()
    {
        var targets = new List<(string Label, IPlayerCharacter Character)>();
        if (ObjectTable.LocalPlayer is { } self)
            targets.Add(("self", self));
        if ((TargetManager.Target ?? TargetManager.SoftTarget) is IPlayerCharacter target && target.Address != ObjectTable.LocalPlayer?.Address)
            targets.Add(("target", target));

        if (ObjectTable.LocalPlayer is { } me)
            Log.Information($"[bones] self actual position {me.Position}, applied offset {OffsetEngine.DesiredOffset.Position} rot {OffsetEngine.DesiredOffset.Rotation}");

        foreach (var (label, character) in targets)
        {
            Log.Information($"[bones] {label} {character.Name.TextValue} timelines: {Bones.AnimationReadiness.DescribeTimelines(character)}");
            var count = 0;
            Bones.BoneReader.ForEachBone(character, (partial, name, world) =>
            {
                Log.Information($"[bones] {label} {character.Name.TextValue} partial {partial}: {name} {world}");
                count++;
            });
            ChatGui.Print($"[PoseKit] Logged {count} bones for {character.Name.TextValue} ({label}) — open /xllog and search \"[bones]\".");
        }
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

        if (string.Equals(splitArgs[0], "bones", StringComparison.OrdinalIgnoreCase))
        {
            DumpBones();
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

    /// This side's pose state for a partner's capture request, or null when not in a pose.
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

        return new CapturedPoseState(pose, PoseTrigger.GetOffsetForNewPreset(), anchor, penumbra);
    }

    /// Anchors to the nearest matching furniture, or null if none is nearby.
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
