using Dalamud.Bindings.ImGui;
using PoseKit.Pairing;

namespace PoseKit.Windows;

/// <summary>Couple pairing status and controls — who you're paired with, pending invites, and
/// unpair — as its own dedicated section, independent of any particular preset. Presets don't name a
/// partner; whether clicking one queues (paired) or plays immediately (not paired) is decided purely
/// by PairingState, which this panel is the one place to see and change.</summary>
public static class PairingPanel
{
    private static string inviteAddress = "";

    public static void Draw(Plugin plugin)
    {
        var state = plugin.PairingState;
        PoseKitUi.SectionHeader("Couple Pairing");

        if (state.Active && state.Peer is { } peer)
        {
            ImGui.TextUnformatted($"Paired with {peer}.");
            ImGui.SameLine();
            if (ImGui.Button("Unpair##PoseKitUnpair"))
                plugin.PairingListener.Unpair();

            var queue = plugin.CoupleQueueService;
            ImGui.TextUnformatted($"You picked: {queue.QueuedSelectionName ?? "(nothing yet)"}");
            ImGui.TextUnformatted($"They picked: {queue.PartnerSelectionName ?? "(nothing yet)"}");

            PoseKitUi.TextWrappedDisabled("Click any preset or animation to queue it — once you've both picked something, both play together.");
            return;
        }

        if (state.PendingInvite is { } pending)
        {
            ImGui.TextUnformatted($"{pending.Sender} wants to pair.");
            if (ImGui.Button("Accept##PoseKitAcceptPairing"))
                plugin.PairingListener.AcceptPending();
            ImGui.SameLine();
            if (ImGui.Button("Dismiss##PoseKitDismissPairing"))
                state.DismissPendingInvite();
            return;
        }

        if (state.OutgoingInvite is { } outgoing)
        {
            ImGui.TextUnformatted($"Invite sent to {outgoing.Target}, waiting for them to accept...");
            return;
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##PoseKitInviteAddress", "Name Surname@World", ref inviteAddress, 64);
        ImGui.SameLine();

        var valid = PartnerIdentity.TryParse(inviteAddress, out var target, out var error);
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!valid))
        {
            if (ImGui.Button("Send Pairing Invite##PoseKitSendInvite"))
            {
                plugin.PairingListener.InitiatePairing(target);
                inviteAddress = "";
            }
        }

        if (!valid && inviteAddress.Trim().Length > 0)
            ImGui.TextColored(PoseKitUi.Bad, error);

        PoseKitUi.TextWrappedDisabled("Pair with a partner to queue presets together — once paired, clicking a preset " +
                                       "queues it and highlights it for both of you; the second selection plays both automatically.");
    }
}
