namespace PoseKit.Pairing;

using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

/// <summary>
/// Adds "Pair with PoseKit" to the game's player context menu, in the world and on player names in
/// chat, the party list and the friend list. Sends the same invite as the pairing panel.
/// </summary>
public sealed class PairingContextMenu : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly PairingState state;
    private readonly PairingListener listener;

    public PairingContextMenu(IContextMenu contextMenu, PairingState state, PairingListener listener)
    {
        this.contextMenu = contextMenu;
        this.state = state;
        this.listener = listener;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        // Runs inside the game's menu code, so nothing may escape.
        try
        {
            if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault target) return;
            if (!PairingTargets.CanInvite(state)) return;
            if (TryGetInvitee(target) is not { } invitee) return;

            args.AddMenuItem(new MenuItem
            {
                Name = "Pair with PoseKit",
                PrefixChar = 'P',
                OnClicked = _ => Invite(invitee),
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PoseKit] Failed to add the pairing context-menu entry.");
        }
    }

    /// Null for non-players, unknown home worlds and the local player's own character.
    private static PartnerIdentity? TryGetInvitee(MenuTargetDefault target)
    {
        if (target.TargetObject is { } obj && obj is not IPlayerCharacter) return null;
        if (target.TargetHomeWorld.RowId == 0 || !target.TargetHomeWorld.IsValid) return null;

        var invitee = PartnerIdentity.FromMenuTarget(target.TargetName, target.TargetHomeWorld.Value.Name.ExtractText());
        if (invitee is not { } identity) return null;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer != null && PartnerIdentity.FromCharacter(localPlayer) is var own && identity.Matches(own.Name, own.World))
            return null;
        return identity;
    }

    private void Invite(PartnerIdentity invitee)
    {
        // The menu may have stayed open while the pairing state changed.
        if (!PairingTargets.CanInvite(state)) return;
        listener.InitiatePairing(invitee);
        Plugin.ChatGui.Print($"[PoseKit] Pairing invite sent to {invitee}.");
    }
}
