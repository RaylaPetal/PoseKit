using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace PoseKit;

/// <summary>
/// Render-only position offset for the local player's model, ported from SimpleHeels'
/// SetDrawOffset detour (SimpleHeels-master/Plugin.cs). Never writes GameObject.Position
/// (server-authoritative) — only the draw-time offset the game itself recomputes every frame,
/// so the server never sees it and there's no rubber-banding.
///
/// Unlike SimpleHeels (which hand-rolls an AOB signature for this function), this hooks the
/// address FFXIVClientStructs itself already resolves for GameObject.SetDrawOffset
/// (confirmed byte-for-byte identical signature via GameObject's [MemberFunction] attribute),
/// so the hook target is maintained by FFXIVClientStructs across game patches rather than a
/// byte pattern PoseKit would have to keep in sync by hand.
///
/// Yaw/rotation offset was dropped from v1: SimpleHeels' separate "SetDrawRotation" hook has no
/// equivalent [MemberFunction]-exposed match in the currently installed FFXIVClientStructs build,
/// and the closest candidate (GameObject.SetRotation) is very likely the logical/server-relevant
/// rotation setter, not a render-only one — not safe to guess at.
/// </summary>
public sealed unsafe class OffsetEngine : IDisposable
{
    private delegate void SetDrawOffsetDelegate(GameObject* gameObject, float x, float y, float z);

    private readonly Hook<SetDrawOffsetDelegate>? setDrawOffsetHook;
    private Vector3 baseOffset;

    public bool Active { get; set; }
    public PoseOffset DesiredOffset { get; set; } = PoseOffset.Zero;
    public bool HookResolved => setDrawOffsetHook != null;

    public OffsetEngine()
    {
        var address = GameObject.Addresses.SetDrawOffset.Value;
        if (address == nint.Zero)
        {
            Plugin.Log.Warning("[PoseKit] GameObject.SetDrawOffset address did not resolve; live offset will be unavailable.");
            return;
        }

        setDrawOffsetHook = Plugin.GameInteropProvider.HookFromAddress<SetDrawOffsetDelegate>(address, SetDrawOffsetDetour);
        setDrawOffsetHook.Enable();
    }

    public void Dispose()
    {
        setDrawOffsetHook?.Disable();
        setDrawOffsetHook?.Dispose();
    }

    /// Called every frame from Plugin's framework tick; re-applies the offset in case nothing
    /// else prompted the game to call SetDrawOffset itself this frame.
    public void Tick(IPlayerCharacter? localPlayer)
    {
        if (setDrawOffsetHook == null || !Active || localPlayer == null) return;

        var obj = (GameObject*)localPlayer.Address;
        if (obj == null) return;

        var desired = baseOffset + DesiredOffset.Position;
        if (Vector3.Distance(desired, obj->DrawOffset) > 0.0001f)
            setDrawOffsetHook.Original(obj, desired.X, desired.Y, desired.Z);
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
}
