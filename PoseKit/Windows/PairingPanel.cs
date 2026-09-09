using System.Linq;
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

            var relay = plugin.CoupleRelayInbox;
            if (relay.Sender is { } relaySender && relay.PresetName is { } relayPresetName)
            {
                ImGui.TextColored(PoseKitUi.Info, $"{relaySender} wants to play \"{relayPresetName}\" with you.");
                if (ImGui.Button("Accept##PoseKitAcceptCoupleRelay"))
                {
                    if (relay.Accept() is { } captured)
                        plugin.ApplyCapturedPartnerState(captured);
                }
                ImGui.SameLine();
                if (ImGui.Button("Deny##PoseKitDenyCoupleRelay"))
                    relay.Deny();
            }

            var queue = plugin.CoupleQueueService;
            if (queue.QueuedSelectionName is { } own)
                ImGui.TextColored(PoseKitUi.Accent, $"You picked: {own}");
            else
                ImGui.TextUnformatted("You picked: (nothing yet)");

            if (queue.PartnerSelectionName is { } theirs)
            {
                ImGui.TextColored(PoseKitUi.Info, $"They picked: {theirs}");
                // Only while nothing's queued on this side yet — once it is, both plays fire
                // immediately and PartnerSelectionName clears, so there'd be nothing left to pick.
                if (queue.QueuedSelectionName == null)
                    DrawQuickPick(plugin, theirs);
            }
            else
                ImGui.TextUnformatted("They picked: (nothing yet)");

            var overrideEnabled = state.LocalOverrideEnabled;
            if (ImGui.Checkbox("Override queue##PoseKitOverrideQueue", ref overrideEnabled))
            {
                state.SetLocalOverrideEnabled(overrideEnabled);
                plugin.PairingListener.SendOverrideToggle(overrideEnabled);
            }

            if (state.MutualOverrideActive)
                ImGui.TextColored(PoseKitUi.Good, "Override active — picking a second item plays your first pick and forces theirs.");
            else if (state.LocalOverrideEnabled)
                PoseKitUi.TextWrappedDisabled("Waiting on partner to enable override too — until then, a second click just replaces your own pick.");

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
            if (ImGui.Button("Cancel##PoseKitCancelInvite"))
                state.CancelOutgoingInvite();
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

    /// Offers "your half" of whatever the partner just picked right here, so picking it doesn't
    /// require scrolling down to find the matching preset or animation elsewhere: a saved preset by
    /// the same name if there is one, otherwise the matching Penumbra mod+option's own trigger
    /// button(s) if this side has that mod discovered. Silently shows nothing found rather than
    /// erroring — the partner's pick may simply not exist on this side yet.
    private static void DrawQuickPick(Plugin plugin, string partnerSelectionName)
    {
        var preset = plugin.PresetManager.Presets.FirstOrDefault(p => p.Name == partnerSelectionName);
        if (preset != null)
        {
            PresetButtonsPanel.DrawPresetEntry(plugin, preset);
            return;
        }

        if (!PenumbraPosePanel.TryDrawQuickTriggerButtons(plugin, partnerSelectionName))
            PoseKitUi.TextWrappedDisabled($"No matching preset or animation found on your side for \"{partnerSelectionName}\".");
    }
}
