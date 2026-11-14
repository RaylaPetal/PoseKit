using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace PoseKit;

/// <summary>
/// Keeps the character from moving while any holder (freecam, the pose lock) needs it. One shared set
/// of hooks and one counter contribution, so holders can't release each other's suppression in the
/// wrong order.
/// </summary>
public sealed unsafe class MovementSuppression : IDisposable
{
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool InputQuery(InputData* data, InputId id);
    private delegate int AxisQuery(InputData* data, uint axis);

    private readonly List<Hook<InputQuery>> queries = new();
    private readonly HashSet<string> holders = new();
    private Hook<AxisQuery>? axisHook;
    private Hook<InputManager.Delegates.GetInputStatus>? statusHook;
    private Hook<InputQuery>? downHook;
    private int* movementCounter;
    private bool ownsCounter;

    /// False when a signature didn't resolve; freecam and the pose lock then can't hold the character.
    public bool Available { get; }

    /// True while at least one holder keeps movement suppressed.
    public bool Active => holders.Count > 0;

    public MovementSuppression()
    {
        try
        {
            // The movss operand resolves to a float; the ForceDisableMovement counter is the
            // int 4 bytes past it.
            var instruction = Plugin.SigScanner.ScanText("F3 0F 10 05 ?? ?? ?? ?? 0F 2E C7");
            movementCounter = (int*)((byte*)(instruction + 8 + *(int*)(instruction + 4)) + 4);
            if (*movementCounter < 0 || *movementCounter > 100)
                throw new InvalidOperationException("Unexpected movement suppression state.");
            AddQuery(InputData.Addresses.IsInputIdPressed.Value);
            downHook = AddQuery(InputData.Addresses.IsInputIdDown.Value);
            AddQuery(InputData.Addresses.IsInputIdHeld.Value);
            AddQuery(InputData.Addresses.IsInputIdReleased.Value);
            axisHook = Plugin.GameInteropProvider.HookFromSignature<AxisQuery>(
                "E8 ?? ?? ?? ?? 66 44 0F 6E C3", (data, axis) => Active && axis is 3 or 4 ? 0 : axisHook!.Original(data, axis));
            var address = InputManager.Addresses.GetInputStatus.Value;
            if (address == 0) throw new InvalidOperationException("Gameplay input support is unavailable.");
            statusHook = Plugin.GameInteropProvider.HookFromAddress<InputManager.Delegates.GetInputStatus>(address,
                (manager, code) => !(Active && Blocks(code)) && statusHook!.Original(manager, code));
            Available = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[PoseKit] Movement suppression unavailable.");
            DisposeHooks();
        }
    }

    private Hook<InputQuery> AddQuery(nint address)
    {
        if (address == 0) throw new InvalidOperationException("Movement binding support is unavailable.");
        Hook<InputQuery>? hook = null;
        hook = Plugin.GameInteropProvider.HookFromAddress<InputQuery>(address,
            (data, id) => !(Active && Blocks(id)) && hook!.Original(data, id));
        queries.Add(hook);
        return hook;
    }

    // Character movement only; camera-look inputs stay unblocked.
    private static bool Blocks(InputId id) => id is
        >= InputId.MOVE_FORE and <= InputId.MOVE_AND_STEER or
        >= InputId.JUMP and <= InputId.AUTORUN_PAD or
        >= InputId.MOVE_DESCENT and <= InputId.MOVE_ANGLE_DESCENT;

    private static bool Blocks(InputCode code) => code is
        >= InputCode.MOVE_DESCENT and <= InputCode.MOVE_STRIFE_R;

    /// Reads whether a key is down right now, past the block. "Held" is the game's hold/repeat state,
    /// which starts late and pulses.
    public bool IsDown(InputData* data, InputId id) => downHook != null && downHook.Original(data, id);

    /// Whether any movement, strafe, jump or descend key is physically down.
    public bool IsMovementKeyDown()
    {
        var framework = GameFramework.Instance();
        if (framework == null || framework->UIModule == null) return false;
        var data = (InputData*)framework->UIModule->GetUIInputData();
        return IsDown(data, InputId.MOVE_FORE) || IsDown(data, InputId.MOVE_BACK) ||
            IsDown(data, InputId.MOVE_LEFT) || IsDown(data, InputId.MOVE_RIGHT) ||
            IsDown(data, InputId.MOVE_STRIFE_L) || IsDown(data, InputId.MOVE_STRIFE_R) ||
            IsDown(data, InputId.JUMP) || IsDown(data, InputId.MOVE_DESCENT);
    }

    /// Idempotent per owner. False when suppression isn't available or the counter looks wrong.
    public bool Acquire(string owner)
    {
        if (!Available) return false;
        if (holders.Contains(owner)) return true;
        if (holders.Count == 0)
        {
            if (movementCounter == null || *movementCounter < 0 || *movementCounter >= 100) return false;
            ++*movementCounter;
            ownsCounter = true;
            foreach (var hook in queries) hook.Enable();
            axisHook!.Enable();
            statusHook!.Enable();
        }
        holders.Add(owner);
        return true;
    }

    public void Release(string owner)
    {
        if (!holders.Remove(owner) || holders.Count > 0) return;
        ReleaseAll();
    }

    private void ReleaseAll()
    {
        holders.Clear();
        // Remove this plugin's contribution only; never reset a shared counter to zero.
        if (ownsCounter)
        {
            ownsCounter = false;
            if (movementCounter != null && *movementCounter > 0) --*movementCounter;
        }
        foreach (var hook in queries) TryCleanup(hook.Disable);
        if (axisHook != null) TryCleanup(axisHook.Disable);
        if (statusHook != null) TryCleanup(statusHook.Disable);
    }

    public void Dispose()
    {
        ReleaseAll();
        DisposeHooks();
    }

    private void DisposeHooks()
    {
        foreach (var hook in queries) TryCleanup(hook.Dispose);
        queries.Clear();
        if (axisHook != null) TryCleanup(axisHook.Dispose);
        if (statusHook != null) TryCleanup(statusHook.Dispose);
        axisHook = null;
        statusHook = null;
        downHook = null;
        movementCounter = null;
    }

    internal static void TryCleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "[PoseKit] Movement suppression cleanup failed."); }
    }
}
