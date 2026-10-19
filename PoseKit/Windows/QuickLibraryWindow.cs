using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PoseKit.Library;
using PoseKit.Pairing;

namespace PoseKit.Windows;

/// <summary>
/// A compact picker over the discovered animations and saved presets: the main window's mods and
/// option groups, collapsed and filtered to what the character can play where it is (see
/// ILibraryContext), favorites first. Plays exactly like the main window, through PlayDispatch.
/// </summary>
public sealed class QuickLibraryWindow : Window, IDisposable
{
    private static readonly LibraryTypeFilter[] TypeFilters = [LibraryTypeFilter.All, LibraryTypeFilter.Solo, LibraryTypeFilter.Couple];

    private readonly Plugin plugin;
    private readonly LibraryView view = new();
    private string search = "";

    public QuickLibraryWindow(Plugin plugin) : base("PoseKit Library###PoseKitQuickLibrary")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 300),
            MaximumSize = new Vector2(900, 1400),
        };
        Size = new Vector2(380, 560);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    private bool Searching => search.Trim().Length > 0;

    /// While searching or filtering, a new search or filter opens everything once; null otherwise.
    private string? ExpandReason(LibraryTypeFilter filter) =>
        Searching || filter != LibraryTypeFilter.All ? $"{search.Trim()}|{filter}" : null;

    public override void Draw()
    {
        PoseKitUi.PaintWindowBackground();
        using var theme = PoseKitUi.PushTheme();

        DrawPairingStrip();

        var context = LibraryContextResolver.Resolve(plugin);
        PoseKitUi.CardTitle("Library", context.Name);
        PoseKitUi.CardTitleRule();

        if (context is FurnitureContextBase furniture)
            SeatPanel.Draw(plugin, furniture);

        // The main window's gizmo and Bone Align (with its quick turns), so a pose can be adjusted
        // without opening it.
        if (ImGui.CollapsingHeader("Adjust##PoseKitLibraryAdjust", ImGuiTreeNodeFlags.DefaultOpen))
        {
            PresetButtonsPanel.DrawGizmoToggle(plugin);
            if (plugin.PairingState.Active)
            {
                // Right-aligned on the gizmo line.
                const string overrideLabel = "Override";
                var width = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(overrideLabel).X;
                ImGui.SameLine();
                var x = ImGui.GetWindowContentRegionMax().X - width;
                if (ImGui.GetCursorPosX() < x) ImGui.SetCursorPosX(x);
                PairingPanel.DrawOverrideToggle(plugin, overrideLabel);
            }
            BoneAlignPanel.Draw(plugin, compact: true);
            ImGui.Spacing();
        }

        DrawSearchRow();

        if (ImGui.BeginChild("##PoseKitLibraryScroll", Vector2.Zero, false, ImGuiWindowFlags.None))
            DrawContents(context);
        ImGui.EndChild();
    }

    private void DrawPairingStrip()
    {
        var state = plugin.PairingState;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;

        if (state.Active && state.Peer is { } peer)
        {
            PoseKitUi.StatusDot(PoseKitUi.Good, $"PAIRED WITH {peer}");
            const string unpairLabel = "Unpair##PoseKitLibraryUnpair";
            ImGui.SameLine();
            var unpairX = ImGui.GetWindowContentRegionMax().X - PoseKitUi.ButtonWidth(unpairLabel);
            if (ImGui.GetCursorPosX() < unpairX) ImGui.SetCursorPosX(unpairX);
            if (ImGui.SmallButton(unpairLabel))
                plugin.PairingListener.Unpair();

            PairingPanel.DrawIncomingRelay(plugin, compact: true);
            PairingPanel.DrawOutgoingRelay(plugin, compact: true);

            // The partner picked and this side hasn't: their animation's options, to play along.
            var queue = plugin.CoupleQueueService;
            if (queue.PartnerSelectionName is { } theirs && queue.QueuedSelectionName == null &&
                ImGui.CollapsingHeader($"{peer.Name} picked: {theirs}##PoseKitLibraryPartnerPick"))
            {
                PairingPanel.DrawQuickPick(plugin, theirs);
            }
        }
        else if (PairingPanel.DrawPendingInvite(plugin, compact: true) || PairingPanel.DrawOutgoingInvite(plugin, compact: true))
        {
            // Drawn by the shared invite prompts.
        }
        else if (PairingTargets.TryGetTargetedIdentity(localPlayer) is { } target)
        {
            if (PoseKitUi.WideButton($"Pair with {target.Name}##PoseKitLibraryPairTarget"))
                plugin.PairingListener.InitiatePairing(target);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Send a PoseKit pairing invite to {target}.");
        }
        else
        {
            return;
        }

        ImGui.Spacing();
    }

    /// Search, the Solo/Couple filter and Rescan on one line.
    private void DrawSearchRow()
    {
        const float filterWidth = 80f;
        const string rescanLabel = "Rescan##PoseKitLibraryRescan";
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var rescanWidth = PoseKitUi.ButtonWidth(rescanLabel);
        ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - filterWidth - rescanWidth - spacing * 2));
        ImGui.InputTextWithHint("##PoseKitLibrarySearch", "Search animations and presets...", ref search, 128);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(filterWidth);
        var filter = plugin.Configuration.LibraryTypeFilter;
        if (ImGui.BeginCombo("##PoseKitLibraryTypeFilter", filter.ToString()))
        {
            foreach (var option in TypeFilters)
            {
                if (ImGui.Selectable(option.ToString(), option == filter) && option != filter)
                {
                    plugin.Configuration.LibraryTypeFilter = option;
                    plugin.Configuration.Save();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button(rescanLabel))
            plugin.RefreshPenumbraPoses();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-scan your selected Penumbra mods for poses and animations, e.g. after adding or updating a mod.");
        ImGui.Spacing();
    }

    private void DrawContents(ILibraryContext context)
    {
        var filter = plugin.Configuration.LibraryTypeFilter;
        var anyListed = DrawPresets(context, filter);

        if (!plugin.PenumbraIpc.IsAvailable)
        {
            PoseKitUi.TextWrappedDisabled("Penumbra not found — animation discovery unavailable.");
            return;
        }

        anyListed |= DrawTree(context, filter);

        if (anyListed) return;
        if (Searching || filter != LibraryTypeFilter.All)
            PoseKitUi.TextWrappedDisabled("Nothing matches your search or filter.");
        else if (plugin.DiscoveredPoses.Count == 0)
            PoseKitUi.TextWrappedDisabled("No mods selected — pick some in PoseKit's Settings.");
        else
            PoseKitUi.TextWrappedDisabled("Nothing here can play from where you are.");
    }

    /// Saved presets in their own section and color, as wrapping chips, apart from the mods.
    private bool DrawPresets(ILibraryContext context, LibraryTypeFilter filter)
    {
        var presets = LibraryView.GetPresets(plugin.PresetManager.Presets, context, search, filter);
        if (presets.Count == 0) return false;

        PoseKitUi.OpenOnce("library|presets", ExpandReason(filter));
        var title = context.PresetsTitle == "Presets" ? "Presets" : $"Presets · {context.PresetsTitle}";
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, PoseKitUi.Preset))
            open = ImGui.CollapsingHeader($"{title}  ({presets.Count})##PoseKitLibraryPresets", ImGuiTreeNodeFlags.DefaultOpen);
        if (open)
        {
            var rowRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            for (var i = 0; i < presets.Count; i++)
                DrawPresetChip(presets[i], i > 0, rowRight);
            ImGui.Spacing();
        }
        return true;
    }

    /// The Favorites section (every starred animation playable here, open by default), then the mods,
    /// collapsed, each listing its animations.
    private bool DrawTree(ILibraryContext context, LibraryTypeFilter filter)
    {
        var tree = view.GetTree(plugin.LibraryIndex, plugin.LibraryIndexVersion, plugin.Configuration, context, search, filter);
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        var expandReason = ExpandReason(filter);

        var favorites = view.Favorites;
        if (favorites.Count > 0)
        {
            PoseKitUi.OpenOnce("library|favorites", expandReason);
            bool favoritesOpen;
            using (ImRaii.PushColor(ImGuiCol.Text, PoseKitUi.Favorite))
                favoritesOpen = ImGui.CollapsingHeader($"Favorites  ({favorites.Count})##PoseKitLibraryFavorites", ImGuiTreeNodeFlags.DefaultOpen);
            if (favoritesOpen)
            {
                ImGui.PushID("PoseKitFavorites");
                foreach (var row in favorites)
                {
                    ImGui.PushID(row.Entry.Id);
                    DrawRow(row, collectionId, Favorites.Context(row.Entry.Mod, row.Entry.Group));
                    ImGui.PopID();
                }
                ImGui.PopID();
            }
        }

        if (tree.Count > 0) PoseKitUi.SectionHeader("Animations");
        foreach (var mod in tree)
        {
            PoseKitUi.OpenOnce($"library|mod|{mod.Mod.ModDirectory}", expandReason);
            if (!ImGui.CollapsingHeader($"{mod.Mod.ModName}  ({mod.Animations.Count})##PoseKitLibraryMod{mod.Mod.ModDirectory}"))
                continue;

            ImGui.PushID(mod.Mod.ModDirectory);
            foreach (var animation in mod.Animations)
                DrawAnimation(animation, mod.Mod, collectionId, expandReason);
            ImGui.PopID();
        }
        return tree.Count > 0;
    }

    /// An option group with its options under it. Collapsed by default unless it has a single option,
    /// so a long pack reads as a list of animations rather than a wall of roles.
    private void DrawAnimation(LibraryView.Animation animation, Penumbra.PoseModInfo mod, Guid? collectionId, string? expandReason)
    {
        ImGui.PushID(animation.Group.Name);
        PoseKitUi.OpenOnce($"library|animation|{mod.ModDirectory}|{animation.Group.Name}", expandReason);

        // The node only carries the arrow and the click; the name is drawn beside it so it can wrap.
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowItemOverlap |
                    (animation.Rows.Count == 1 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        var open = ImGui.TreeNodeEx("##PoseKitLibraryAnimation", flags);
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(animation.Rows.Count > 1 ? $"{animation.Group.Name}  ({animation.Rows.Count})" : animation.Group.Name);
        ImGui.PopTextWrapPos();
        if (animation.IsCouple)
        {
            ImGui.SameLine(0, RowItemSpacing);
            ImGui.TextColored(PoseKitUi.Info, "Couple");
        }

        if (open)
        {
            foreach (var row in animation.Rows)
            {
                ImGui.PushID(row.Entry.Id);
                DrawRow(row, collectionId);
                ImGui.PopID();
            }
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private static readonly Vector4 RowPanel = new(1f, 1f, 1f, 0.035f);
    private static readonly Vector4 FavoritePanel = new(1f, 0.8f, 0.3f, 0.09f);

    /// One option as a card: the star and its name as the mod has it, then its play buttons indented
    /// under the name. The author's description is in the name's tooltip.
    /// <param name="groupName">Shown above the name, for favorites listed outside their mod.</param>
    private void DrawRow(LibraryView.Row row, Guid? collectionId, string? groupName = null) =>
        PoseKitUi.Panel(row.Favorite ? FavoritePanel : RowPanel, () => DrawRowBody(row, collectionId, groupName));

    private void DrawRowBody(LibraryView.Row row, Guid? collectionId, string? groupName)
    {
        var entry = row.Entry;
        var rowRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X - 4;

        // Indented past the star, so the name's wrapped lines and the buttons line up under the name.
        var starStart = ImGui.GetCursorPosX();
        if (PoseKitUi.FavoriteStar("LibraryOption", row.Favorite))
            Favorites.Toggle(plugin.Configuration, entry.Mod, entry.Group, entry.Option);
        ImGui.SameLine(0, 4);
        var indent = ImGui.GetCursorPosX() - starStart;
        ImGui.Indent(indent);
        ImGui.SetCursorPosX(starStart + indent);

        ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 4);
        if (groupName != null)
        {
            ImGui.TextColored(PoseKitUi.Muted, groupName);
            ImGui.SetCursorPosX(starStart + indent);
        }
        using (ImRaii.PushColor(ImGuiCol.Text, PoseKitUi.Favorite, row.Favorite))
            ImGui.TextUnformatted(entry.DisplayName);
        ImGui.PopTextWrapPos();
        if (ImGui.IsItemHovered() && entry.Option.Description.Trim() is { Length: > 0 } description)
            ImGui.SetTooltip(description);

        for (var t = 0; t < row.Triggers.Count; t++)
        {
            var trigger = row.Triggers[t];
            var pick = PoseKitUi.GetPickState(plugin, PlayDispatch.DescribeTriggerLabel(entry.Mod, entry.Option, trigger));
            var text = PlayDispatch.TriggerButtonText(trigger);
            if (t > 0) SameLineIfFits(ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2, rowRight);
            using (PoseKitUi.PushPickButtonStyle(pick))
            {
                if (ImGui.SmallButton($"{text}##PoseKitLibraryPlay{t}"))
                {
                    // Selection is read at click time, since a play may have just changed it.
                    var beforePlay = PlayDispatch.SelectOptionBeforePlay(plugin, entry.Mod, entry.Group, entry.Option, collectionId);
                    PlayDispatch.ClickTrigger(plugin, entry.Mod, entry.Group, entry.Option, trigger, collectionId, beforePlay);
                }
            }
            PoseKitUi.DrawPickBadge(pick);
        }
        ImGui.Unindent(indent);
    }

    private const float RowItemSpacing = 6;

    /// Continues the line only when an item this wide still fits before the row's right edge.
    private static void SameLineIfFits(float width, float rowRight, float spacing = RowItemSpacing)
    {
        if (ImGui.GetItemRectMax().X + spacing + width <= rowRight)
            ImGui.SameLine(0, spacing);
    }

    /// A preset as a chip in the preset color; a couple preset says so.
    private void DrawPresetChip(Presets.NamedPose preset, bool sameLine, float rowRight)
    {
        var pick = PoseKitUi.GetPickState(plugin, preset.Name);
        var text = preset.PartnerHalf != null ? $"{preset.Name}  · couple" : preset.Name;
        if (sameLine) SameLineIfFits(ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2, rowRight);
        using (pick == PoseKitUi.PickState.None ? PoseKitUi.PushPresetButtonStyle() : null)
        using (PoseKitUi.PushPickButtonStyle(pick))
        {
            if (ImGui.SmallButton($"{text}##PoseKitLibraryPreset{preset.GetHashCode()}"))
                PlayDispatch.ClickPreset(plugin, preset);
        }
        PoseKitUi.DrawPickBadge(pick);
    }
}
