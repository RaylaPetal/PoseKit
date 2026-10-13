namespace PoseKit.Pairing;

using Dalamud.Game.ClientState.Objects.SubKinds;

/// <summary>Finds the player character the local player is targeting, for pairing and bone align.</summary>
public static class PairingTargets
{
    /// The target, else the soft target, if it's another player character.
    public static IPlayerCharacter? TryGetTargetedPlayer(IPlayerCharacter? localPlayer)
    {
        if (localPlayer == null) return null;
        if ((Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget) is not IPlayerCharacter target) return null;
        return target.GameObjectId == localPlayer.GameObjectId ? null : target;
    }

    public static PartnerIdentity? TryGetTargetedIdentity(IPlayerCharacter? localPlayer) =>
        TryGetTargetedPlayer(localPlayer) is { } target ? PartnerIdentity.FromCharacter(target) : null;

    /// Whether a new invite may be sent: not paired and no invite already waiting for an answer.
    public static bool CanInvite(PairingState state) => !state.Active && state.OutgoingInvite == null;
}
