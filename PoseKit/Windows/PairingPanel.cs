using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PoseKit.Pairing;

namespace PoseKit.Windows;

/// <summary>Couple pairing status and controls — who you're paired with, pending invites and couple
/// presets, both sides' picks, and unpair — independent of any particular preset. Presets don't name
/// a partner; whether clicking one queues (paired) or plays immediately (not paired) is decided purely
/// by PairingState, which this panel is the one place to see and change. Drawn inside a narrow card
/// (MainWindow's Pairing column), so every row wraps or fills the width rather than assuming a
/// wide line.</summary>
public static class PairingPanel
{
    private static string inviteAddress = "";

    public static void Draw(Plugin plugin)
    {
        var state = plugin.PairingState;

        if (state.Active && state.Peer is { } peer)
        {
            DrawPaired(plugin, state, peer);
            return;
        }

        if (state.PendingInvite is { } pending)
        {
            ImGui.TextColored(PoseKitUi.Info, "PAIRING INVITE");
            ImGui.TextWrapped($"{pending.Sender} wants to pair with you.");
            ImGui.Spacing();
            if (HalfButton("Accept##PoseKitAcceptPairing", first: true))
                plugin.PairingListener.AcceptPending();
            if (HalfButton("Dismiss##PoseKitDismissPairing", first: false))
                state.DismissPendingInvite();
            return;
        }

        if (state.OutgoingInvite is { } outgoing)
        {
            ImGui.TextColored(PoseKitUi.Muted, "INVITE SENT");
            ImGui.TextWrapped($"Waiting for {outgoing.Target} to accept...");
            ImGui.Spacing();
            if (PoseKitUi.WideButton("Cancel invite##PoseKitCancelInvite"))
                state.CancelOutgoingInvite();
            return;
        }

        ImGui.TextColored(PoseKitUi.Muted, "NO ACTIVE PAIRING");
        ImGui.TextWrapped("Pair with a partner to queue poses and play couple presets together.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##PoseKitInviteAddress", "Name Surname@World", ref inviteAddress, 64);

        var valid = PartnerIdentity.TryParse(inviteAddress, out var target, out var error);
        using (ImRaii.Disabled(!valid))
        {
            if (PoseKitUi.WideButton("Send pairing invite##PoseKitSendInvite"))
            {
                plugin.PairingListener.InitiatePairing(target);
                inviteAddress = "";
            }
        }

        if (!valid && inviteAddress.Trim().Length > 0)
            ImGui.TextColored(PoseKitUi.Bad, error);

        ImGui.Spacing();
        PoseKitUi.TextWrappedDisabled("Once paired, clicking a preset or animation queues it and highlights it for " +
                                       "both of you; when you've both picked, both play together.");
    }

    private static void DrawPaired(Plugin plugin, PairingState state, PartnerIdentity peer)
    {
        ImGui.TextColored(PoseKitUi.Good, "●");
        ImGui.SameLine(0, 6);
        ImGui.TextWrapped($"Paired with {peer}");

        var relay = plugin.CoupleRelayInbox;
        if (relay.Sender is { } relaySender && relay.PresetName is { } relayPresetName)
        {
            PoseKitUi.SectionHeader("Incoming");
            ImGui.PushStyleColor(ImGuiCol.Text, PoseKitUi.Info);
            ImGui.TextWrapped($"{relaySender.Name} wants to play \"{relayPresetName}\" with you.");
            ImGui.PopStyleColor();
            if (HalfButton("Accept##PoseKitAcceptCoupleRelay", first: true))
            {
                if (relay.Accept() is { } captured)
                    plugin.ApplyCapturedPartnerState(captured);
            }
            if (HalfButton("Deny##PoseKitDenyCoupleRelay", first: false))
                relay.Deny();
        }

        var outbox = plugin.CoupleRelayOutbox;
        if (outbox.PendingPresetName is { } outgoingName)
        {
            PoseKitUi.SectionHeader("Outgoing");
            ImGui.PushStyleColor(ImGuiCol.Text, PoseKitUi.Info);
            ImGui.TextWrapped($"Waiting for your partner to accept \"{outgoingName}\"...");
            ImGui.PopStyleColor();
            if (PoseKitUi.WideButton("Cancel##PoseKitCancelCoupleRelay"))
                outbox.Cancel();
        }

        PoseKitUi.SectionHeader("Picks");
        var queue = plugin.CoupleQueueService;
        DrawPickRow(PoseKitUi.Accent, "You", queue.QueuedSelectionName);
        DrawPickRow(PoseKitUi.Info, "Partner", queue.PartnerSelectionName);

        // Only while nothing's queued on this side yet — once it is, both plays fire immediately and
        // PartnerSelectionName clears, so there'd be nothing left to pick.
        if (queue.PartnerSelectionName is { } theirs && queue.QueuedSelectionName == null)
        {
            ImGui.Spacing();
            DrawQuickPick(plugin, theirs);
        }

        PoseKitUi.SectionHeader("Options");
        var overrideEnabled = state.LocalOverrideEnabled;
        if (ImGui.Checkbox("Override queue##PoseKitOverrideQueue", ref overrideEnabled))
        {
            state.SetLocalOverrideEnabled(overrideEnabled);
            plugin.PairingListener.SendOverrideToggle(overrideEnabled);
        }

        if (state.MutualOverrideActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, PoseKitUi.Good);
            ImGui.TextWrapped("Override active — picking a second item plays your first pick and forces theirs.");
            ImGui.PopStyleColor();
        }
        else if (state.LocalOverrideEnabled)
            PoseKitUi.TextWrappedDisabled("Waiting on your partner to enable override too — until then, a second click just replaces your own pick.");

        // Unlike "Override queue" above (mutual, forces a pick onto the partner), this is one-sided
        // and sends nothing at all — the partner is never told and never affected.
        var soloPlayEnabled = state.SoloPlayEnabled;
        if (ImGui.Checkbox("Play solo##PoseKitSoloPlay", ref soloPlayEnabled))
            state.SetSoloPlayEnabled(soloPlayEnabled);

        if (state.SoloPlayEnabled)
            PoseKitUi.TextWrappedDisabled("Clicks play immediately for you only — nothing is queued, forced, or sent to your partner.");

        ImGui.Spacing();
        PoseKitUi.TextWrappedDisabled("Click any preset or animation to queue it — once you've both picked something, both play together.");

        ImGui.Spacing();
        if (PoseKitUi.WideButton("Unpair##PoseKitUnpair"))
            plugin.PairingListener.Unpair();
    }

    /// "● You   Sit Pose 2" — a colored dot and muted role label, then the pick (or a muted
    /// placeholder), wrapping under itself when the name is long.
    private static void DrawPickRow(Vector4 color, string who, string? pick)
    {
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, 6);
        ImGui.TextColored(PoseKitUi.Muted, who);
        ImGui.SameLine(0, 8);
        if (pick != null)
            ImGui.TextWrapped(pick);
        else
            ImGui.TextColored(PoseKitUi.Muted, "nothing yet");
    }

    /// One of a pair of buttons splitting the current row evenly.
    private static bool HalfButton(string label, bool first)
    {
        if (!first) ImGui.SameLine();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = first ? (ImGui.GetContentRegionAvail().X - spacing) / 2f : ImGui.GetContentRegionAvail().X;
        return ImGui.Button(label, new Vector2(width, 0));
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
