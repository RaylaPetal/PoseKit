using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace PoseKit.Camera;

/// <summary>
/// Freecam's own input handling on top of the shared <see cref="MovementSuppression"/>: E/Q capture and
/// the emote-cancel guard. Consumes gameplay actions rather than keyboard characters, preserving chat input.
/// </summary>
internal sealed unsafe class FreeCamInput : IDisposable
{
    private const string Owner = "freecam";

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool CancelEmoteDelegate(EmoteController* emoteController, nint unknown);
    private delegate void DeviceUpdate(nint manager, nint a2, nint controller, nint mouse, KeyboardDeviceData* keyboard);

    // Raw keyboard state the game fills each frame before hotbars read it (layout as used by Ktisis).
    [StructLayout(LayoutKind.Explicit)]
    private struct KeyboardDeviceData
    {
        internal const int KeyCount = 160;
        [FieldOffset(4)] public fixed uint KeyMap[KeyCount];
    }

    private const int KeyE = 0x45;
    private const int KeyQ = 0x51;
    private readonly MovementSuppression suppression;
    private Hook<DeviceUpdate>? deviceHook;
    private Hook<CancelEmoteDelegate>? cancelEmoteHook;
    public bool Active { get; private set; }

    /// Set each tick by the service; while true, raw keys are neither read nor cleared.
    public bool NavigationPaused { get; set; } = true;

    /// E/Q state from the raw keyboard, captured before the keys are hidden from the hotbars.
    public bool RawUp { get; private set; }
    public bool RawDown { get; private set; }

    public bool IsDown(InputData* data, InputId id) => suppression.IsDown(data, id);

    public FreeCamInput(MovementSuppression suppression)
    {
        this.suppression = suppression;
        try
        {
            if (!suppression.Available) throw new InvalidOperationException("Movement suppression is unavailable.");
            // EmoteController's cancel-emote check. Detaching the camera trips it (e.g. /doze),
            // so it always answers "don't cancel" while freecam is active.
            var cancelEmoteAddress = Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 7B 08 45 33 C0");
            cancelEmoteHook = Plugin.GameInteropProvider.HookFromAddress<CancelEmoteDelegate>(cancelEmoteAddress, (_, _) => false);
            deviceHook = Plugin.GameInteropProvider.HookFromSignature<DeviceUpdate>("E8 ?? ?? ?? ?? 83 7B 58 00", DeviceUpdateDetour);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Reads E/Q for vertical freecam movement, then clears them so hotbar slots on those keys
    // never fire. Untouched while paused so chat typing and normal play are unaffected.
    private void DeviceUpdateDetour(nint manager, nint a2, nint controller, nint mouse, KeyboardDeviceData* keyboard)
    {
        deviceHook!.Original(manager, a2, controller, mouse, keyboard);
        if (!Active || NavigationPaused || keyboard == null)
        {
            RawUp = RawDown = false;
            return;
        }
        RawUp = keyboard->KeyMap[KeyE] != 0;
        RawDown = keyboard->KeyMap[KeyQ] != 0;
        keyboard->KeyMap[KeyE] = 0;
        keyboard->KeyMap[KeyQ] = 0;
    }

    public void Enable()
    {
        if (Active) return;
        if (!suppression.Acquire(Owner))
            throw new InvalidOperationException("Movement suppression cannot be acquired.");
        try
        {
            Active = true;
            cancelEmoteHook!.Enable();
            deviceHook!.Enable();
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
        // Releases only freecam's hold; the pose lock may still keep the character still.
        suppression.Release(Owner);
        if (cancelEmoteHook != null) TryCleanup(cancelEmoteHook.Disable);
        if (deviceHook != null) TryCleanup(deviceHook.Disable);
        RawUp = RawDown = false;
    }

    public void Dispose()
    {
        Disable();
        if (cancelEmoteHook != null) TryCleanup(cancelEmoteHook.Dispose);
        if (deviceHook != null) TryCleanup(deviceHook.Dispose);
        cancelEmoteHook = null;
        deviceHook = null;
    }

    internal static void TryCleanup(Action action) => MovementSuppression.TryCleanup(action);
}
