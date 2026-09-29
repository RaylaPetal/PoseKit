using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace PoseKit;

/// <summary>
/// Render-only position/rotation offset for the local player's model. Only the draw offset is
/// changed, never the real position, so the server never sees it.
///
/// If the rotation signature doesn't resolve, rotation offsets are a no-op.
/// </summary>
public sealed unsafe class OffsetEngine : IDisposable
{
    private delegate void SetDrawOffsetDelegate(GameObject* gameObject, float x, float y, float z);
    private delegate void* SetDrawRotationDelegate(GameObject* gameObject, float rotation);

    private readonly Hook<SetDrawOffsetDelegate>? setDrawOffsetHook;
    private readonly Hook<SetDrawRotationDelegate>? setDrawRotationHook;
    private Vector3 baseOffset;
    private float baseRotation;
    private bool hasBaseRotation;

    public bool Active { get; set; }
    public PoseOffset DesiredOffset { get; set; } = PoseOffset.Zero;
    public bool HookResolved => setDrawOffsetHook != null;
    public bool RotationHookResolved => setDrawRotationHook != null;

    public OffsetEngine()
    {
        var address = GameObject.Addresses.SetDrawOffset.Value;
        if (address == nint.Zero)
        {
            Plugin.Log.Warning("[PoseKit] GameObject.SetDrawOffset address did not resolve; live offset will be unavailable.");
        }
        else
        {
            setDrawOffsetHook = Plugin.GameInteropProvider.HookFromAddress<SetDrawOffsetDelegate>(address, SetDrawOffsetDetour);
            setDrawOffsetHook.Enable();
        }

        try
        {
            setDrawRotationHook = Plugin.GameInteropProvider.HookFromSignature<SetDrawRotationDelegate>(
                "E8 ?? ?? ?? ?? 83 FE 01 75 0D", SetDrawRotationDetour);
            setDrawRotationHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PoseKit] SetDrawRotation signature did not resolve; rotation offset will be unavailable.");
        }
    }

    public void Dispose()
    {
        setDrawOffsetHook?.Disable();
        setDrawOffsetHook?.Dispose();
        setDrawRotationHook?.Disable();
        setDrawRotationHook?.Dispose();
    }

    /// Writes the un-offset values back immediately instead of waiting for the game's next call.
    public void Reset(IPlayerCharacter? localPlayer)
    {
        Active = false;
        DesiredOffset = PoseOffset.Zero;

        if (localPlayer == null) return;
        var obj = (GameObject*)localPlayer.Address;
        if (obj == null) return;

        if (setDrawRotationHook != null && hasBaseRotation)
            setDrawRotationHook.Original(obj, baseRotation);

        if (setDrawOffsetHook == null) return;
        setDrawOffsetHook.Original(obj, baseOffset.X, baseOffset.Y, baseOffset.Z);
    }

    /// Re-applies the offset every frame, since the game may only call SetDrawRotation once when a
    /// pose starts. Rotation is only re-applied when nonzero, so a stale cached base rotation can't
    /// overwrite the character turning.
    public void Tick(IPlayerCharacter? localPlayer)
    {
        if (!Active || localPlayer == null) return;

        var obj = (GameObject*)localPlayer.Address;
        if (obj == null) return;

        if (setDrawOffsetHook != null)
        {
            var desired = baseOffset + DesiredOffset.Position;
            if (Vector3.Distance(desired, obj->DrawOffset) > 0.0001f)
                setDrawOffsetHook.Original(obj, desired.X, desired.Y, desired.Z);
        }

        if (setDrawRotationHook != null && hasBaseRotation && DesiredOffset.Rotation != 0f)
            setDrawRotationHook.Original(obj, baseRotation + DesiredOffset.Rotation);
    }

    private void SetDrawOffsetDetour(GameObject* gameObject, float x, float y, float z)
    {
        if (gameObject->ObjectIndex == 0)
        {
            baseOffset = new Vector3(x, y, z);
            if (Active)
            {
                var desired = baseOffset + DesiredOffset.Position;
                setDrawOffsetHook!.Original(gameObject, desired.X, desired.Y, desired.Z);
                return;
            }
        }

        setDrawOffsetHook!.Original(gameObject, x, y, z);
    }

    private void* SetDrawRotationDetour(GameObject* gameObject, float rotation)
    {
        if (gameObject->ObjectIndex == 0)
        {
            baseRotation = rotation;
            hasBaseRotation = true;
            if (Active)
                return setDrawRotationHook!.Original(gameObject, rotation + DesiredOffset.Rotation);
        }

        return setDrawRotationHook!.Original(gameObject, rotation);
    }
}
