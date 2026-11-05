using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PoseKit.Bones;
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
    private static readonly LibraryTypeFilter[] TypeFilters =
        [LibraryTypeFilter.All, LibraryTypeFilter.Solo, LibraryTypeFilter.Couple, LibraryTypeFilter.FxF, LibraryTypeFilter.MxF];

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
        if (PoseKitUi.TintedHeader("Adjust##PoseKitLibraryAdjust", PoseKitUi.HeaderAdjust, ImGuiTreeNodeFlags.DefaultOpen))
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
                PoseKitUi.TintedHeader($"{peer.Name} picked: {theirs}##PoseKitLibraryPartnerPick", PoseKitUi.HeaderPartnerPick))
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
        if (PoseKitUi.TintedHeader($"{title}  ({presets.Count})##PoseKitLibraryPresets", PoseKitUi.HeaderPresets, ImGuiTreeNodeFlags.DefaultOpen))
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
        var tree = view.GetTree(plugin.LibraryIndex, plugin.LibraryIndexVersion, plugin.PairingTags.Version, plugin.Configuration, context, search, filter);
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        var expandReason = ExpandReason(filter);

        var anyRecent = DrawRecent(context, collectionId, expandReason);

        var favorites = view.Favorites;
        if (favorites.Count > 0)
        {
            PoseKitUi.OpenOnce("library|favorites", expandReason);
            if (PoseKitUi.TintedHeader($"Favorites  ({favorites.Count})##PoseKitLibraryFavorites", PoseKitUi.HeaderFavorites, ImGuiTreeNodeFlags.DefaultOpen))
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

        if (LibraryBuckets.SectionsActive(filter))
        {
            DrawSection($"Fits {filter}", "Fits", PoseKitUi.HeaderFits, tree, collectionId, expandReason, ImGuiTreeNodeFlags.DefaultOpen);
            DrawSection("Doesn't fit", "DoesntFit", PoseKitUi.HeaderDoesntFit, view.DoesntFit, collectionId, null, ImGuiTreeNodeFlags.None,
                "Tagged as something else. They can still be played, and you can click a tag to correct it.");
            return anyRecent || tree.Count + view.DoesntFit.Count > 0;
        }

        if (tree.Count > 0) PoseKitUi.SectionHeader("Animations");
        DrawMods(tree, collectionId, expandReason, "Tree");
        return anyRecent || tree.Count > 0;
    }

    /// One filter section, absent when empty, counting options rather than mods.
    private void DrawSection(string title, string id, Vector4 color, List<LibraryView.ModBlock> mods, Guid? collectionId, string? expandReason,
        ImGuiTreeNodeFlags flags, string? tooltip = null)
    {
        if (mods.Count == 0) return;

        var count = mods.Sum(mod => mod.Animations.Sum(animation => animation.Rows.Count));
        PoseKitUi.OpenOnce($"library|section|{id}", expandReason);
        var open = PoseKitUi.TintedHeader($"{title}  ({count})###PoseKitLibrarySection{id}", color, flags);
        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        if (!open) return;

        ImGui.PushID(id);
        ImGui.Indent(4);
        DrawMods(mods, collectionId, expandReason, id);
        ImGui.Unindent(4);
        ImGui.PopID();
    }

    private void DrawMods(List<LibraryView.ModBlock> mods, Guid? collectionId, string? expandReason, string section)
    {
        foreach (var mod in mods)
        {
            PoseKitUi.OpenOnce($"library|{section}|mod|{mod.Mod.ModDirectory}", expandReason);
            if (!ImGui.CollapsingHeader($"{mod.Mod.ModName}  ({mod.Animations.Count})##PoseKitLibraryMod{mod.Mod.ModDirectory}"))
                continue;

            ImGui.PushID(mod.Mod.ModDirectory);
            foreach (var animation in mod.Animations)
                DrawAnimation(animation, mod.Mod, collectionId, expandReason, section);
            ImGui.PopID();
        }
    }

    /// What was last played with this partner, or alone when unpaired or playing solo.
    private bool DrawRecent(ILibraryContext context, Guid? collectionId, string? expandReason)
    {
        var pairing = plugin.PairingState;
        var partner = pairing.Active && !pairing.SoloPlayEnabled ? pairing.Peer : null;
        var rows = view.GetRecent(plugin.LibraryIndex, plugin.LibraryIndexVersion, plugin.RecentPlays.Get(partner), plugin.Configuration, context, search);
        if (rows.Count == 0) return false;

        PoseKitUi.OpenOnce("library|recent", expandReason);
        var title = partner is { } peer ? $"Recent with {peer.Name}" : "Recent";
        if (PoseKitUi.TintedHeader($"{title}  ({rows.Count})###PoseKitLibraryRecent", PoseKitUi.HeaderRecent, ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.PushID("PoseKitRecent");
            foreach (var row in rows)
            {
                ImGui.PushID(row.Entry.Id);
                DrawRow(row, collectionId, Favorites.Context(row.Entry.Mod, row.Entry.Group));
                ImGui.PopID();
            }
            ImGui.PopID();
        }
        return true;
    }

    /// An option group with its options under it. Collapsed by default unless it has a single option,
    /// so a long pack reads as a list of animations rather than a wall of roles.
    private void DrawAnimation(LibraryView.Animation animation, Penumbra.PoseModInfo mod, Guid? collectionId, string? expandReason, string section)
    {
        ImGui.PushID(animation.Group.Name);
        PoseKitUi.OpenOnce($"library|{section}|animation|{mod.ModDirectory}|{animation.Group.Name}", expandReason);

        // The node only carries the arrow and the click; the name is drawn beside it so it can wrap.
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowItemOverlap |
                    (animation.Rows.Count == 1 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        var open = ImGui.TreeNodeEx("##PoseKitLibraryAnimation", flags);
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(animation.Rows.Count > 1 ? $"{animation.Group.Name}  ({animation.Rows.Count})" : animation.Group.Name);
        ImGui.PopTextWrapPos();

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
        DrawChip(entry);
        ImGui.SameLine(0, RowItemSpacing);
        using (ImRaii.PushColor(ImGuiCol.Text, PoseKitUi.Favorite, row.Favorite))
            ImGui.TextUnformatted(entry.DisplayName);
        ImGui.PopTextWrapPos();
        if (ImGui.IsItemHovered() && entry.Option.Description.Trim() is { Length: > 0 } description)
            ImGui.SetTooltip(description);
        OpenTagMenuOnRightClick();
        DrawTagMenu(entry);

        if (entry.Scene is { } scene) DrawScene(row, scene, rowRight);

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

    private const string TagMenuId = "##PoseKitTagMenu";

    private static void OpenTagMenuOnRightClick()
    {
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup(TagMenuId);
    }

    /// The animation's pairing as a chip in front of its name. Clicking it opens the tag menu.
    private static void DrawChip(LibraryEntry entry)
    {
        if (PoseKitUi.PairingChip("PoseKitLibraryChip", entry.Pairing))
            ImGui.OpenPopup(TagMenuId);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(entry.PairingSource switch
            {
                PairingSource.Player => "You set this. Click to change it.",
                PairingSource.Alignment => "From the body parts you aligned on this animation. Click to change it.",
                PairingSource.ActWords => "Its name states the act. Click to change it.",
                PairingSource.ReliableWords => "Read from its name. Click to change it.",
                PairingSource.Author => "Declared by the mod's author. Click to change it.",
                PairingSource.IdenticalAnimation => "Declared by the mod's author for the same animation in another mod. Click to change it.",
                PairingSource.Name => "Stated in the animation's name. Click to change it.",
                _ => "Assumed from its name, which doesn't say for sure. Click to change it.",
            });
        }
        OpenTagMenuOnRightClick();
    }

    private static readonly (string Label, CouplePairing State)[] TagChoices =
    [
        ("FxF", CouplePairing.FxF),
        ("MxF", CouplePairing.MxF),
        ("M/F x F", CouplePairing.Either),
        ("Solo", CouplePairing.Solo),
    ];

    private void DrawTagMenu(LibraryEntry entry)
    {
        if (!ImGui.BeginPopup(TagMenuId)) return;

        if (entry.AnimationKey == null)
        {
            ImGui.TextColored(PoseKitUi.Muted, "Still reading this animation's files...");
        }
        else
        {
            if (plugin.ContactObservations.TryGet(entry.AnimationKey) is { } measured && measured.Suggestion.TagText() is var suggested)
            {
                ImGui.TextColored(PoseKitUi.Muted,
                    $"Measured: {BodyParts.DisplayName(measured.A)} near {BodyParts.DisplayName(measured.B)}");
                if (ImGui.Selectable($"Use suggested tag: {suggested}"))
                    plugin.PairingTags.SetPlayerTag(entry, measured.Suggestion);
                ImGui.Separator();
            }

            foreach (var (label, state) in TagChoices)
            {
                if (ImGui.Selectable(label, entry.Pairing == state && entry.PairingSource == PairingSource.Player))
                    plugin.PairingTags.SetPlayerTag(entry, state);
            }
            using (ImRaii.Disabled(entry.PairingSource != PairingSource.Player))
            {
                if (ImGui.Selectable("Clear my tag"))
                    plugin.PairingTags.SetPlayerTag(entry, null);
            }
        }
        ImGui.EndPopup();
    }

    /// Scenes the player swapped this session, so a choice survives the list redrawing.
    private readonly HashSet<string> swappedScenes = [];

    /// Who plays what, with one click to offer it. The trigger buttons below stay for playing along
    /// the usual way.
    private void DrawScene(LibraryView.Row row, Scene scene, float rowRight)
    {
        var pairing = plugin.PairingState;
        if (!pairing.Active || pairing.SoloPlayEnabled || pairing.Peer is not { } peer) return;

        var entry = row.Entry;
        var swapped = swappedScenes.Contains(entry.Id);
        var own = swapped ? scene.Other : scene.Dominant;
        var theirs = swapped ? scene.Dominant : scene.Other;

        ImGui.TextColored(PoseKitUi.Muted, $"You: {own.Label} ({PlayDispatch.TriggerButtonText(own.Trigger)})");
        const float gap = 8;
        var partnerText = $"{peer.Name}: {theirs.Label} ({PlayDispatch.TriggerButtonText(theirs.Trigger)})";
        SameLineIfFits(ImGui.CalcTextSize(partnerText).X, rowRight, gap);
        ImGui.TextColored(PoseKitUi.Muted, partnerText);

        var canPlay = row.Triggers.Contains(own.Trigger);
        using (ImRaii.Disabled(!canPlay || plugin.CoupleRelayOutbox.PendingPresetName != null))
        {
            if (ImGui.SmallButton("Play together##PoseKitLibraryPlayTogether"))
                PlayDispatch.OfferScene(plugin, entry.Mod, entry.Group, entry.Option, own, theirs);
        }
        if (!canPlay && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Your side of this animation can't play from where you are. Try swapping.");

        ImGui.SameLine(0, RowItemSpacing);
        if (ImGui.SmallButton("Swap##PoseKitLibrarySwapScene"))
        {
            if (!swappedScenes.Add(entry.Id))
                swappedScenes.Remove(entry.Id);
        }
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
