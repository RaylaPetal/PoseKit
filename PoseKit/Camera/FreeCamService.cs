using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace PoseKit.Camera;

public sealed unsafe class FreeCamService : IDisposable
{
    private delegate nint CalculateView(SceneCamera* camera);
    private readonly FreeCamMotion motion = new();
    private FreeCamInput? input;
    private Hook<CalculateView>? viewHook;
    private Hook<GameCamera.Delegates.Update>? updateHook;
    private NativeCameraView.Loader? loadView;
    private GameCamera* camera;
    private nint actor;
    private Vector3 actorPosition;
    private Vector3 observedPosition;
    private nint observedActor;
    private float stationarySeconds;
    private uint territory;
    private bool disposed;
    private float debugTimer;
    private float dirH, dirV, distance, interpDistance, fov;
    private Vector3 scenePosition, sceneLookAt;
    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "Freecam disabled.";

    public FreeCamService()
    {
        Plugin.Condition.ConditionChange += OnConditionChange;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Toggle()
    {
        if (disposed) return;
        if (Enabled) { Disable(); return; }
        try
        {
            var reason = UnsupportedReason();
            if (reason != null) { Status = reason; return; }
            if (InputManager.Addresses.IsAutoRunning.Value == 0)
                throw new InvalidOperationException("Autorun detection is unavailable.");
            if (InputManager.IsAutoRunning() || stationarySeconds < 0.25f)
            {
                Status = "Stop moving and turn off autorun before enabling freecam.";
                return;
            }

            var manager = CameraManager.Instance();
            var candidate = manager->GetActiveCamera();
            NativeCameraView.Validate((nint)candidate->SceneCamera.RenderCamera, (Matrix4x4*)&candidate->SceneCamera.ViewMatrix);
            if (viewHook != null) { viewHook.Dispose(); viewHook = null; }
            if (updateHook != null) { updateHook.Dispose(); updateHook = null; }
            // Create all hooks before acquiring input or changing the live camera.
            input = new FreeCamInput();
            // ScanText follows an initial E8/E9 and returns the function target, not
            // the call site. Applying another relative displacement creates an invalid pointer.
            var loadAddress = Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 17 48 8D 4D E0");
            loadView = Marshal.GetDelegateForFunctionPointer<NativeCameraView.Loader>(loadAddress);
            viewHook = Plugin.GameInteropProvider.HookFromSignature<CalculateView>(
                "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? F6 81 ?? ?? ?? ?? ?? 48 8B D9 48 89 B4 24 ?? ?? ?? ??", CalculateViewDetour);
            updateHook = Plugin.GameInteropProvider.HookFromAddress<GameCamera.Delegates.Update>(
                (nint)candidate->VirtualTable->Update, UpdateCameraDetour);

            var view = (Matrix4x4)candidate->SceneCamera.ViewMatrix;
            // The game's affine view storage does not guarantee the homogeneous element.
            view.M44 = 1f;
            if (!Matrix4x4.Invert(view, out var inverse)) throw new InvalidOperationException("The camera view cannot be read.");
            motion.Initialize(inverse.Translation);
            camera = candidate;
            dirH = camera->DirH;
            dirV = camera->DirV;
            distance = camera->Distance;
            interpDistance = camera->InterpDistance;
            fov = camera->FoV;
            scenePosition = camera->SceneCamera.Position;
            sceneLookAt = camera->SceneCamera.LookAtVector;
            actor = Plugin.ObjectTable.LocalPlayer!.Address;
            actorPosition = Plugin.ObjectTable.LocalPlayer.Position;
            territory = Plugin.ClientState.TerritoryType;
            debugTimer = 0f;
            input.Enable();
            updateHook.Enable();
            viewHook.Enable();
            Enabled = true;
            Status = "Freecam enabled. /posekit tfc to exit.";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[PoseKit] Freecam activation failed.");
            Disable();
            Status = $"Freecam unavailable: {ex.Message}";
        }
    }

    private static string? UnsupportedReason()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) return "Log in before enabling freecam.";
        if (Plugin.ClientState.IsGPosing) return "Leave GPose before enabling freecam.";
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return "Freecam is unavailable during this transition.";
        var manager = CameraManager.Instance();
        if (manager == null || manager->ActiveCameraIndex != 0 || manager->Camera == null ||
            manager->GetActiveCamera() != manager->Camera || manager->Camera->SceneCamera.RenderCamera == null)
            return "Freecam requires the normal world camera.";
        return null;
    }

    public void Tick(float seconds)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            observedActor = 0;
            stationarySeconds = 0;
        }
        else
        {
            stationarySeconds = observedActor == player.Address && Vector3.DistanceSquared(observedPosition, player.Position) < 0.00000001f
                ? stationarySeconds + Math.Clamp(seconds, 0, 0.05f) : 0;
            observedActor = player.Address;
            observedPosition = player.Position;
        }
        if (!Enabled) return;
        try
        {
            var reason = InvalidSessionReason();
            if (reason != null)
            {
                Plugin.Log.Warning($"[PoseKit] Freecam auto-disabled: {reason}");
                Disable();
                Status = "Freecam ended because the world or character state changed.";
                return;
            }
            StepMotion(seconds);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[PoseKit] Freecam update failed.");
            Disable();
        }
    }

    // Polls the game's own input functions directly (bypassing this session's block via
    // FreeCamInput.IsHeld) rather than ImGui key/mouse state, which does not reliably
    // reflect gameplay input during native camera-drag capture. Direction comes from the
    // camera's live rotation, which the game keeps updating via normal right-click-drag.
    private void StepMotion(float seconds)
    {
        var framework = GameFramework.Instance();
        var atk = RaptureAtkModule.Instance();
        var io = ImGui.GetIO();
        var blocked = framework == null || !framework->CursorInputs.IsGameWindowFocused || atk == null ||
            atk->IsTextInputActive() || io.WantTextInput || io.WantCaptureKeyboard || io.WantCaptureMouse;
        debugTimer += seconds;
        if (debugTimer >= 1f)
        {
            debugTimer = 0f;
            Plugin.Log.Warning($"[PoseKit] Freecam tick: blocked={blocked} pos={motion.Position} dirH={camera->DirH} dirV={camera->DirV}");
        }
        if (blocked || framework == null || input == null) return;
        var data = (InputData*)framework->UIModule->GetUIInputData();
        var move = new Vector3(
            (input.IsHeld(data, InputId.MOVE_RIGHT) || input.IsHeld(data, InputId.MOVE_STRIFE_R) ? 1 : 0) -
            (input.IsHeld(data, InputId.MOVE_LEFT) || input.IsHeld(data, InputId.MOVE_STRIFE_L) ? 1 : 0),
            (input.IsHeld(data, InputId.JUMP) ? 1 : 0) - (input.IsHeld(data, InputId.MOVE_DESCENT) ? 1 : 0),
            (input.IsHeld(data, InputId.MOVE_FORE) ? 1 : 0) - (input.IsHeld(data, InputId.MOVE_BACK) ? 1 : 0));
        // DirH increases opposite to our forward/right formula's convention; negate here
        // rather than touch the native field, which stays under the game's own control.
        motion.Step(move, -camera->DirH, camera->DirV, seconds);
    }

    // Diagnostic breakdown, logged so an auto-disable can be traced to the specific
    // condition that tripped it instead of guessing blind. Character immobility comes
    // from FreeCamInput's direct input-query hooks, not from the shared native movement
    // counter (that value gets reset by the game itself well within a frame, so it isn't
    // a usable liveness signal here even though we still set it for other consumers).
    private string? InvalidSessionReason()
    {
        var unsupported = UnsupportedReason();
        if (unsupported != null) return $"unsupported ({unsupported})";
        if (camera == null) return "camera null";
        if (CameraManager.Instance()->GetActiveCamera() != camera) return "active camera changed";
        if (Plugin.ClientState.TerritoryType != territory) return "territory changed";
        if (Plugin.ObjectTable.LocalPlayer is not { } player || player.Address != actor) return "actor changed";
        var distSq = Vector3.DistanceSquared(actorPosition, player.Position);
        if (distSq >= ActorDriftEpsilonSquared) return $"actor moved (distSq={distSq})";
        return null;
    }

    // Snapshot comparison against the position at activation time, so this needs enough
    // slack to tolerate per-frame recompute noise (below world-coordinate float precision)
    // without missing an actual teleport/reposition, unlike the frame-to-frame stationary gate.
    private const float ActorDriftEpsilonSquared = 0.0001f;

    private bool SessionValid() => InvalidSessionReason() == null;

    private void UpdateCameraDetour(GameCamera* current)
    {
        // The normal update drives scene/render work too; never skip it wholesale.
        updateHook!.Original(current);
        if (Enabled && current == camera)
        {
            try
            {
                if (!SessionValid()) { Disable(); return; }
                // DirH/DirV are left alone: the game's normal right-click-drag still
                // drives them, and StepMotion reads them live for movement direction.
                // Freezing them here would fight native camera-look every frame.
                current->Distance = distance;
                current->InterpDistance = interpDistance;
                current->FoV = fov;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[PoseKit] Freecam camera update failed.");
                Disable();
            }
        }
    }

    private nint CalculateViewDetour(SceneCamera* current)
    {
        var result = viewHook!.Original(current);
        if (!Enabled || camera == null || current != &camera->SceneCamera) return result;
        try
        {
            if (!SessionValid()) { Disable(); return result; }
            // Use the game's aligned matrix storage, just as the original native
            // calculation does. A managed Matrix4x4 local can be only 8-byte aligned.
            var matrix = (Matrix4x4*)&current->ViewMatrix;
            NativeCameraView.Validate((nint)current->RenderCamera, matrix);
            var forward = FreeCamMotion.Forward(-camera->DirH, camera->DirV);
            current->Position = motion.Position;
            current->LookAtVector = motion.Position + forward;
            *matrix = Matrix4x4.CreateLookAt(motion.Position, motion.Position + forward, Vector3.UnitY);
            // Update derived render state through the game's own matrix loader.
            NativeCameraView.Load(loadView!, (nint)current->RenderCamera, matrix);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[PoseKit] Freecam view failed.");
            Disable();
        }
        return result;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (Enabled && value && flag is ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51 or
            ConditionFlag.WatchingCutscene or ConditionFlag.WatchingCutscene78 or ConditionFlag.OccupiedInCutSceneEvent)
            Disable();
    }

    private void OnTerritoryChanged(uint _) => Disable();
    private void OnLogout(int _, int __) => Disable();

    public void Disable()
    {
        Enabled = false;
        try
        {
            if (camera != null && UnsupportedReason() == null && Plugin.ClientState.TerritoryType == territory &&
                CameraManager.Instance()->GetActiveCamera() == camera)
            {
                camera->DirH = dirH;
                camera->DirV = dirV;
                camera->Distance = distance;
                camera->InterpDistance = interpDistance;
                camera->FoV = fov;
                camera->SceneCamera.Position = scenePosition;
                camera->SceneCamera.LookAtVector = sceneLookAt;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[PoseKit] Freecam restoration failed.");
        }
        finally
        {
            camera = null;
            if (viewHook != null) FreeCamInput.TryCleanup(viewHook.Disable);
            if (updateHook != null) FreeCamInput.TryCleanup(updateHook.Disable);
            input?.Dispose();
            input = null;
            // Hooks can be disabled from their detours; disposal waits until the next activation/unload.
            Status = "Freecam disabled.";
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Plugin.Condition.ConditionChange -= OnConditionChange;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.ClientState.Logout -= OnLogout;
        Disable();
        if (viewHook != null) FreeCamInput.TryCleanup(viewHook.Dispose);
        if (updateHook != null) FreeCamInput.TryCleanup(updateHook.Dispose);
    }
}
