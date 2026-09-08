using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace PoseKit.Camera;

/// <summary>Consumes gameplay actions rather than keyboard characters, preserving chat input.</summary>
internal sealed unsafe class FreeCamInput : IDisposable
{
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool InputQuery(InputData* data, InputId id);
    private delegate int AxisQuery(InputData* data, uint axis);
    private readonly List<Hook<InputQuery>> queries = new();
    private Hook<AxisQuery>? axisHook;
    private Hook<InputManager.Delegates.GetInputStatus>? statusHook;
    private Hook<InputQuery>? heldHook;
    private int* movementCounter;
    private bool ownsMovement;
    public bool Active { get; private set; }
    public int QueryHits;

    // Bypasses this instance's own block (which returns false for movement IDs) to read
    // the actual physical key state, matching Cammy's Update() polling of isInputIDHeld.
    public bool IsHeld(InputData* data, InputId id) => heldHook != null && heldHook.Original(data, id);

    public FreeCamInput()
    {
        try
        {
            // Cammy's native movement-disable reference. The RIP-relative displacement
            // resolves to the float the movss loads; the actual ForceDisableMovement
            // counter is the int 4 bytes past that (verified against Cammy's own
            // Hypostasis signature, which applies that +4 after the same resolution).
            var instruction = Plugin.SigScanner.ScanText("F3 0F 10 05 ?? ?? ?? ?? 0F 2E C7");
            movementCounter = (int*)((byte*)(instruction + 8 + *(int*)(instruction + 4)) + 4);
            if (*movementCounter < 0 || *movementCounter > 100)
                throw new InvalidOperationException("Unexpected movement suppression state.");
            AddQuery(InputData.Addresses.IsInputIdPressed.Value);
            AddQuery(InputData.Addresses.IsInputIdDown.Value);
            heldHook = AddQuery(InputData.Addresses.IsInputIdHeld.Value);
            AddQuery(InputData.Addresses.IsInputIdReleased.Value);
            axisHook = Plugin.GameInteropProvider.HookFromSignature<AxisQuery>(
                "E8 ?? ?? ?? ?? 66 44 0F 6E C3", (data, axis) => Active && axis is 3 or 4 ? 0 : axisHook!.Original(data, axis));
            var address = InputManager.Addresses.GetInputStatus.Value;
            if (address == 0) throw new InvalidOperationException("Gameplay input support is unavailable.");
            statusHook = Plugin.GameInteropProvider.HookFromAddress<InputManager.Delegates.GetInputStatus>(address,
                (manager, code) => !(Active && Blocks(code)) && statusHook!.Original(manager, code));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private Hook<InputQuery> AddQuery(nint address)
    {
        if (address == 0) throw new InvalidOperationException("Movement binding support is unavailable.");
        Hook<InputQuery>? hook = null;
        hook = Plugin.GameInteropProvider.HookFromAddress<InputQuery>(address,
            (data, id) =>
            {
                if (Active && Blocks(id)) { QueryHits++; return false; }
                return hook!.Original(data, id);
            });
        queries.Add(hook);
        return hook;
    }

    // Character movement/actions only. Camera-look ranges (CAMERA_*, mouse-drag codes)
    // must stay unblocked so the game's own right-click-drag keeps rotating the camera,
    // matching Cammy's approach of blocking only its specific movement keybindings.
    private static bool Blocks(InputId id) => id is
        >= InputId.MOVE_FORE and <= InputId.MOVE_AND_STEER or
        >= InputId.JUMP and <= InputId.AUTORUN_PAD or
        >= InputId.MOVE_DESCENT and <= InputId.MOVE_ANGLE_DESCENT;

    private static bool Blocks(InputCode code) => code is
        >= InputCode.MOVE_DESCENT and <= InputCode.MOVE_STRIFE_R;

    public void Enable()
    {
        if (Active) return;
        if (movementCounter == null || *movementCounter < 0 || *movementCounter >= 100)
            throw new InvalidOperationException("Movement suppression cannot be acquired.");
        try
        {
            ++*movementCounter;
            ownsMovement = true;
            Active = true;
            foreach (var hook in queries) hook.Enable();
            axisHook!.Enable();
            statusHook!.Enable();
        }
        catch
        {
            Disable();
            throw;
        }
    }

    public void Disable()
    {
        Active = false;
        // Remove this session's contribution only; never reset a shared counter to zero.
        if (ownsMovement)
        {
            ownsMovement = false;
            if (movementCounter != null && *movementCounter > 0) --*movementCounter;
        }
        foreach (var hook in queries) TryCleanup(hook.Disable);
        if (axisHook != null) TryCleanup(axisHook.Disable);
        if (statusHook != null) TryCleanup(statusHook.Disable);
    }

    public void Dispose()
    {
        Disable();
        foreach (var hook in queries) TryCleanup(hook.Dispose);
        queries.Clear();
        if (axisHook != null) TryCleanup(axisHook.Dispose);
        if (statusHook != null) TryCleanup(statusHook.Dispose);
        axisHook = null;
        statusHook = null;
        heldHook = null;
        movementCounter = null;
    }

    internal static void TryCleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "[PoseKit] Freecam cleanup failed."); }
    }
}
