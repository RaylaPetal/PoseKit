using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.ImGuiNotification.EventArgs;
using Dalamud.Plugin.Services;

namespace PoseKit.Pairing;

/// <summary>
/// Shows an incoming pairing invite as a Dalamud notification with Accept and Dismiss, so it can be
/// answered without opening a PoseKit window. The panels still show the invite; closing the toast
/// leaves it waiting there.
/// </summary>
public sealed class PairingInviteNotifier : IDisposable
{
    private readonly INotificationManager notifications;
    private readonly PairingState state;
    private readonly PairingListener listener;

    private IActiveNotification? toast;
    private string? shownInviteId;

    public PairingInviteNotifier(INotificationManager notifications, PairingState state, PairingListener listener)
    {
        this.notifications = notifications;
        this.state = state;
        this.listener = listener;
        state.Changed += OnChanged;
    }

    /// Follows the pending invite: a new one gets a toast, and one answered anywhere (a panel, the
    /// library, pairing from the other side) closes it.
    private void OnChanged()
    {
        var inviteId = state.PendingInvite?.InviteId;
        if (inviteId == shownInviteId) return;

        CloseToast();
        if (state.PendingInvite is not { } pending) return;

        shownInviteId = pending.InviteId;
        toast = notifications.AddNotification(new Notification
        {
            Title = "PoseKit pairing invite",
            Content = $"{pending.Sender} wants to pair with you.",
            Type = NotificationType.Info,
            InitialDuration = TimeSpan.FromSeconds(60),
        });
        toast.DrawActions += DrawActions;
    }

    private void DrawActions(INotificationDrawArgs args)
    {
        // Ignore a stale toast still drawing after its invite was answered.
        if (state.PendingInvite?.InviteId != shownInviteId || shownInviteId == null) return;

        if (ImGui.Button("Accept##PoseKitToastAcceptPairing"))
            listener.AcceptPending();
        ImGui.SameLine();
        if (ImGui.Button("Dismiss##PoseKitToastDismissPairing"))
            state.DismissPendingInvite();
    }

    private void CloseToast()
    {
        if (toast != null)
        {
            toast.DrawActions -= DrawActions;
            toast.DismissNow();
        }
        toast = null;
        shownInviteId = null;
    }

    public void Dispose()
    {
        state.Changed -= OnChanged;
        CloseToast();
    }
}
