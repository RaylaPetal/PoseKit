using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace PoseKit;

/// <summary>
/// Render-only position/rotation offset for the local player's model, ported from SimpleHeels'
/// SetDrawOffset/SetDrawRotation detours (SimpleHeels-master/Plugin.cs). Never writes
/// GameObject.Position/Rotation (server-authoritative) — only the draw-time offset/rotation the
/// game itself recomputes every frame, so the server never sees it and there's no rubber-banding.
///
/// Position: unlike SimpleHeels (which hand-rolls an AOB signature for this function), this hooks
/// the address FFXIVClientStructs itself already resolves for GameObject.SetDrawOffset (confirmed
/// byte-for-byte identical signature via GameObject's [MemberFunction] attribute), so the hook
/// target is maintained by FFXIVClientStructs across game patches rather than a byte pattern
/// PoseKit would have to keep in sync by hand.
///
/// Rotation: experimental. SimpleHeels' separate "SetDrawRotation" hook has no equivalent
/// [MemberFunction]-exposed match in the currently installed FFXIVClientStructs build to verify
/// against ahead of time, so this hooks SimpleHeels' own hand-rolled AOB pattern directly and just
/// has to be tested in-game — if the signature doesn't resolve, HookResolved/RotationHookResolved
/// stays false and the rotation field is silently a no-op rather than crashing.
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

    /// Deactivates and immediately writes the last known un-offset values back, rather than passively
    /// waiting for the game to next call SetDrawOffset/SetDrawRotation on its own (which might not
    /// happen soon if the character is just standing still, making Reset feel like it did nothing).
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

    /// Called every frame from Plugin's framework tick; re-applies the offset in case nothing
    /// else prompted the game to call SetDrawOffset/SetDrawRotation itself this frame. Position has
    /// always done this; rotation didn't (see "Post-implementation finding" in
    /// openspec/changes/align-handoff-cancel-guard's design.md for why that gap mattered) — a pose's
    /// own native call into SetDrawRotation may happen only once, at entry, so a rotation offset
    /// that isn't already set by the moment that single call happens has no other chance to land that
    /// frame. Re-applying every tick, the same way position already does, removes that race.
    ///
    /// Rotation is only reapplied when DesiredOffset.Rotation is actually nonzero — i.e. only while
    /// there's a real offset to maintain. Every pose trigger calls PoseTrigger.ApplyOffset (which sets
    /// Active = true) regardless of whether that pose has any offset dialed in, so an unconditional
    /// reapply here would force-write a *cached* baseRotation (from whenever the game last happened to
    /// call SetDrawRotation, which can predate a later real GameObject.Rotation change — the character
    /// turning) every single frame with nothing to actually maintain —
    /// silently overwriting any real rotation change for as long as the pose stays active. See
    /// design.md Decision 3j.
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
