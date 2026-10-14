using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PoseKit.Pairing;

namespace PoseKit.Windows;

/// <summary>Pairing status and controls: invites, couple preset requests, both sides' picks, and
/// options.</summary>
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

        if (DrawPendingInvite(plugin, compact: false) || DrawOutgoingInvite(plugin, compact: false))
            return;

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

        DrawIncomingRelay(plugin, compact: false);
        DrawOutgoingRelay(plugin, compact: false);

        PoseKitUi.SectionHeader("Picks");
        var queue = plugin.CoupleQueueService;
        DrawPickRow(PoseKitUi.Accent, "You", queue.QueuedSelectionName);
        DrawPickRow(PoseKitUi.Info, "Partner", queue.PartnerSelectionName);

        if (queue.PartnerSelectionName is { } theirs && queue.QueuedSelectionName == null)
        {
            ImGui.Spacing();
            DrawQuickPick(plugin, theirs);
        }

        PoseKitUi.SectionHeader("Options");
        DrawOverrideToggle(plugin, "Override queue");

        if (state.MutualOverrideActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, PoseKitUi.Good);
            ImGui.TextWrapped("Override active — picking a second item plays your first pick and forces theirs.");
            ImGui.PopStyleColor();
        }
        else if (state.LocalOverrideEnabled)
            PoseKitUi.TextWrappedDisabled("Waiting on your partner to enable override too — until then, a second click just replaces your own pick.");

        // One-sided; nothing is sent to the partner.
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

    // The invite and relay prompts below are shared by this panel (full) and the quick library's
    // pairing strip (compact), so both windows act the same.

    /// The shared override toggle, also in the quick library. Turning it on tells the partner, whose
    /// PoseKit mirrors it; with both on, a second pick plays the first and forces it on the partner.
    public static void DrawOverrideToggle(Plugin plugin, string label)
    {
        var state = plugin.PairingState;
        var overrideEnabled = state.LocalOverrideEnabled;
        if (ImGui.Checkbox($"{label}##PoseKitOverrideQueue", ref overrideEnabled))
        {
            state.SetLocalOverrideEnabled(overrideEnabled);
            plugin.PairingListener.SendOverrideToggle(overrideEnabled);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(state.MutualOverrideActive
                ? "Override active: your first pick queues, your second plays both and forces the first on your partner."
                : state.LocalOverrideEnabled
                    ? "Waiting on your partner to turn override on too."
                    : "Picks queue until you've both chosen, then both play. Turn on to choose for both of you.");
    }

    /// A pairing invite waiting on this side. False when there's none.
    public static bool DrawPendingInvite(Plugin plugin, bool compact)
    {
        var state = plugin.PairingState;
        if (state.PendingInvite is not { } pending) return false;

        if (!compact) ImGui.TextColored(PoseKitUi.Info, "PAIRING INVITE");
        Prompt(PoseKitUi.Info, compact ? $"{pending.Sender} wants to pair." : $"{pending.Sender} wants to pair with you.", compact);
        switch (Choice("Accept##PoseKitAcceptPairing", "Dismiss##PoseKitDismissPairing", compact))
        {
            case 1: plugin.PairingListener.AcceptPending(); break;
            case 2: state.DismissPendingInvite(); break;
        }
        return true;
    }

    /// This side's invite waiting on the other player. False when there's none.
    public static bool DrawOutgoingInvite(Plugin plugin, bool compact)
    {
        var state = plugin.PairingState;
        if (state.OutgoingInvite is not { } outgoing) return false;

        if (!compact) ImGui.TextColored(PoseKitUi.Muted, "INVITE SENT");
        Prompt(PoseKitUi.Muted, $"Waiting for {outgoing.Target} to accept...", compact);
        if (Single(compact ? "Cancel##PoseKitCancelInvite" : "Cancel invite##PoseKitCancelInvite", compact))
            state.CancelOutgoingInvite();
        return true;
    }

    /// The partner's couple preset or library role waiting on this side.
    public static void DrawIncomingRelay(Plugin plugin, bool compact)
    {
        var inbox = plugin.CoupleRelayInbox;
        if (inbox.Pending is not { } incoming) return;

        if (!compact) PoseKitUi.SectionHeader("Incoming");
        Prompt(PoseKitUi.Info, $"{incoming.Sender.Name} wants to play \"{incoming.Name}\" with you.", compact);
        switch (Choice("Accept##PoseKitAcceptCoupleRelay", "Decline##PoseKitDenyCoupleRelay", compact))
        {
            case 1:
                if (inbox.Accept() is { } request)
                    plugin.ApplyRelayRequest(request);
                break;
            case 2: inbox.Deny(); break;
        }
    }

    /// This side's couple preset or library role waiting on the partner.
    public static void DrawOutgoingRelay(Plugin plugin, bool compact)
    {
        var outbox = plugin.CoupleRelayOutbox;
        if (outbox.PendingPresetName is not { } outgoing) return;

        if (!compact) PoseKitUi.SectionHeader("Outgoing");
        Prompt(compact ? PoseKitUi.Muted : PoseKitUi.Info, $"Waiting for your partner to accept \"{outgoing}\"...", compact);
        if (Single("Cancel##PoseKitCancelCoupleRelay", compact))
            outbox.Cancel();
    }

    /// Wrapped, since option names run long.
    private static void Prompt(Vector4 color, string text, bool compact)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(text);
        if (!compact) ImGui.Spacing();
    }

    /// Two buttons: small side by side when compact, else splitting the row. 1 or 2 for the one
    /// clicked, else 0.
    private static int Choice(string first, string second, bool compact)
    {
        if (compact)
        {
            var clicked = ImGui.SmallButton(first) ? 1 : 0;
            ImGui.SameLine();
            return ImGui.SmallButton(second) ? 2 : clicked;
        }
        var firstClicked = HalfButton(first, first: true);
        return HalfButton(second, first: false) ? 2 : firstClicked ? 1 : 0;
    }

    private static bool Single(string label, bool compact) =>
        compact ? ImGui.SmallButton(label) : PoseKitUi.WideButton(label);

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

    /// This side's matching preset or animation for the partner's pick, if any.
    /// The partner's pick, with this side's options to play along (also shown in the quick library).
    public static void DrawQuickPick(Plugin plugin, string partnerSelectionName)
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
