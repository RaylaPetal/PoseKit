using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PoseKit.Library;
using PoseKit.Library.Categories;
using PoseKit.Pairing;

namespace PoseKit.Windows;

/// <summary>
/// A compact picker over the discovered animations and saved presets, browsed by theme and act and
/// filtered to what the character is doing (see ILibraryContext). Plays exactly like the main window,
/// through PlayDispatch. Right-clicking an animation tags it into categories.json.
/// </summary>
public sealed class QuickLibraryWindow : Window, IDisposable
{
    private static readonly LibraryTypeFilter[] TypeFilters = [LibraryTypeFilter.All, LibraryTypeFilter.Solo, LibraryTypeFilter.Couple];

    private readonly Plugin plugin;
    private readonly LibraryView view = new();
    private string search = "";
    private CategoryPath path = CategoryPath.Home;

    // The tagging menu's new-name fields.
    private string newTheme = "";
    private string newAct = "";

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

    /// While searching or filtering, a new search, filter or level opens everything once; null otherwise.
    private string? ExpandReason(LibraryTypeFilter filter) =>
        Searching || filter != LibraryTypeFilter.All ? $"{search.Trim()}|{filter}|{path}" : null;

    /// "Lesbian > Rough", for the search hint.
    private string PathLabel => path.Level switch
    {
        CategoryLevel.All => "All animations",
        CategoryLevel.Hidden => "Hidden",
        _ => path.Category is { } category ? $"{path.Root} > {category}" : path.Root ?? "",
    };

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
            BoneAlignPanel.Draw(plugin, compact: true);
            ImGui.Spacing();
        }

        DrawSearchRow();
        DrawNavigationRow();

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
            PairingPanel.DrawIncomingRelay(plugin, compact: true);
            PairingPanel.DrawOutgoingRelay(plugin, compact: true);
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

    private void DrawSearchRow()
    {
        const float filterWidth = 90f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - filterWidth - spacing));
        var hint = path.Level == CategoryLevel.Home ? "Search animations and presets..." : $"Search in {PathLabel}...";
        ImGui.InputTextWithHint("##PoseKitLibrarySearch", hint, ref search, 128);

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
    }

    /// Breadcrumb ("Home > Straight > BDSM"), each step clickable, then Reload and any category notice.
    private void DrawNavigationRow()
    {
        // Search stays within where the player is, so the breadcrumb stays too.
        if (Crumb("Home", path.Level != CategoryLevel.Home))
            path = CategoryPath.Home;

        var rootLabel = path.Level switch
        {
            CategoryLevel.All => "All animations",
            CategoryLevel.Hidden => "Hidden",
            CategoryLevel.Root => path.Root,
            _ => null,
        };
        if (rootLabel != null)
        {
            Separator();
            if (Crumb(rootLabel, path.Category != null))
                path = path with { Category = null };
        }
        if (path.Category != null)
        {
            Separator();
            Crumb(path.Category, false);
        }

        const string rescanLabel = "Rescan##PoseKitLibraryRescan";
        const string reloadLabel = "Reload categories##PoseKitReloadCategories";
        ImGui.SameLine();
        var buttonsX = ImGui.GetWindowContentRegionMax().X - PoseKitUi.ButtonWidth(rescanLabel)
                       - ImGui.GetStyle().ItemSpacing.X - PoseKitUi.ButtonWidth(reloadLabel);
        if (ImGui.GetCursorPosX() < buttonsX) ImGui.SetCursorPosX(buttonsX);
        if (ImGui.SmallButton(rescanLabel))
            plugin.RefreshPenumbraPoses();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-scan your selected Penumbra mods for poses and animations, e.g. after adding or updating a mod.");
        ImGui.SameLine();
        if (ImGui.SmallButton(reloadLabel))
            plugin.ReloadCategories();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Re-read {plugin.Categories.FilePath}.\nTagging in game rewrites that file, so comments in it aren't kept.");

        var notices = plugin.CategoryNotices;
        if (notices.Count > 0)
        {
            ImGui.TextColored(PoseKitUi.Bad, $"Categories have {notices.Count} problem(s) — hover for details.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", notices));
        }
        ImGui.Spacing();
    }

    private static bool Crumb(string label, bool clickable)
    {
        if (!clickable)
        {
            ImGui.TextUnformatted(label);
            return false;
        }
        return ImGui.SmallButton($"{label}##PoseKitCrumb{label}");
    }

    private static void Separator()
    {
        ImGui.SameLine(0, 4);
        ImGui.TextColored(PoseKitUi.Muted, ">");
        ImGui.SameLine(0, 4);
    }

    private void DrawContents(ILibraryContext context)
    {
        var filter = plugin.Configuration.LibraryTypeFilter;
        var counts = view.GetCounts(plugin.LibraryIndex, plugin.LibraryIndexVersion, plugin.CategoryVersion, context, filter);

        // A path can go stale after a category edit or reload removed what it pointed at.
        if (path.Level == CategoryLevel.Root && !counts.Roots.Any(r => string.Equals(r.Name, path.Root, StringComparison.OrdinalIgnoreCase)))
            path = CategoryPath.Home;
        if (path.Level == CategoryLevel.Hidden && !counts.AnyHidden)
            path = CategoryPath.Home;
        if (path is { Level: CategoryLevel.Root, Category: { } category } && !CategoryExists(counts, path.Root!, category))
            path = path with { Category = null };

        var anyListed = false;
        if (path.Level == CategoryLevel.Home)
            anyListed |= DrawPresets(context, filter);

        if (!plugin.PenumbraIpc.IsAvailable)
        {
            PoseKitUi.TextWrappedDisabled("Penumbra not found — animation discovery unavailable.");
            return;
        }

        if (!Searching && path.Level == CategoryLevel.Home)
        {
            DrawHome(counts);
            return;
        }

        // A pairing root lists its categories first; Solo, Idles and Dancing show their rows directly.
        if (!Searching && path is { Level: CategoryLevel.Root, Category: null } && AutoCategories.PairingRoots.Contains(path.Root!))
        {
            DrawCategories(counts, path.Root!);
            return;
        }

        anyListed |= DrawTree(context, filter, counts);

        if (anyListed) return;
        if (Searching || filter != LibraryTypeFilter.All)
            PoseKitUi.TextWrappedDisabled("Nothing matches your search or filter.");
        else if (plugin.DiscoveredPoses.Count == 0)
            PoseKitUi.TextWrappedDisabled("No mods selected — pick some in PoseKit's Settings.");
        else
            PoseKitUi.TextWrappedDisabled("Nothing here can play from where you are.");
    }

    private static bool CategoryExists(LibraryView.CategoryCounts counts, string root, string category) =>
        category == LibraryView.OtherCategory
            ? counts.Other.ContainsKey(root)
            : counts.Categories.TryGetValue(root, out var categories) &&
              categories.Any(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase));

    private bool DrawPresets(ILibraryContext context, LibraryTypeFilter filter)
    {
        var presets = LibraryView.GetPresets(plugin.PresetManager.Presets, context, search, filter);
        if (presets.Count == 0) return false;

        PoseKitUi.OpenOnce("library|presets", ExpandReason(filter));
        if (ImGui.CollapsingHeader($"{context.PresetsTitle}  ({presets.Count})##PoseKitLibraryPresets", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var preset in presets)
                DrawPresetRow(preset);
        }
        return true;
    }

    private void DrawHome(LibraryView.CategoryCounts counts)
    {
        PoseKitUi.SectionHeader("Browse");
        foreach (var (root, count) in counts.Roots)
        {
            if (CategoryButton(root, count, $"Root{root}"))
                path = new CategoryPath(CategoryLevel.Root, root);
        }

        ImGui.Spacing();
        if (CategoryButton("All animations", counts.All, "All"))
            path = new CategoryPath(CategoryLevel.All);
        if (counts.AnyHidden && CategoryButton("Hidden", counts.Hidden, "Hidden"))
            path = new CategoryPath(CategoryLevel.Hidden);
    }

    private void DrawCategories(LibraryView.CategoryCounts counts, string root)
    {
        if (counts.Categories.TryGetValue(root, out var categories))
        {
            foreach (var (category, count) in categories)
            {
                if (CategoryButton(category, count, $"Category{category}"))
                    path = path with { Category = category };
            }
        }
        if (counts.Other.TryGetValue(root, out var other) && CategoryButton(LibraryView.OtherCategory, other, "CategoryOther"))
            path = path with { Category = LibraryView.OtherCategory };
    }

    /// A full-width entry with its count; dimmed when nothing in it can play here.
    private static bool CategoryButton(string name, int count, string id)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, PoseKitUi.Muted, count == 0))
            return ImGui.Selectable($"{name}  ({count})##PoseKitCategory{id}");
    }

    private bool DrawTree(ILibraryContext context, LibraryTypeFilter filter, LibraryView.CategoryCounts counts)
    {
        var tree = view.GetTree(plugin.LibraryIndex, plugin.LibraryIndexVersion, plugin.CategoryVersion, context,
            search, filter, path);
        var collectionId = plugin.PenumbraIpc.TryGetLocalPlayerCollectionId();
        var expandReason = ExpandReason(filter);

        foreach (var mod in tree)
        {
            PoseKitUi.OpenOnce($"library|mod|{mod.Mod.ModDirectory}", expandReason);
            if (!ImGui.CollapsingHeader($"{mod.Mod.ModName}  ({mod.Count})##PoseKitLibraryMod{mod.Mod.ModDirectory}",
                    ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.PushID(mod.Mod.ModDirectory);
            foreach (var animation in mod.Animations)
                DrawAnimation(animation, mod.Mod.ModDirectory, collectionId, counts, expandReason);
            ImGui.PopID();
        }
        return tree.Count > 0;
    }

    /// An option group with its options under it. Collapsed by default unless it has a single option,
    /// so a long pack reads as a list of animations rather than a wall of roles.
    private void DrawAnimation(LibraryView.Animation animation, string modDirectory, Guid? collectionId,
        LibraryView.CategoryCounts counts, string? expandReason)
    {
        ImGui.PushID(animation.Group.Name);
        PoseKitUi.OpenOnce($"library|animation|{modDirectory}|{animation.Group.Name}", expandReason);

        // The node only carries the arrow and the click; the name is drawn beside it so it can wrap.
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowItemOverlap |
                    (animation.Rows.Count == 1 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        var open = ImGui.TreeNodeEx("##PoseKitLibraryAnimation", flags);
        // Roles and variants of one animation share its categories, so the animation is tagged as a
        // whole; in a list of separate animations each option is tagged on its own.
        var perOption = animation.Rows.All(r => r.Entry.AnimationKey == r.Entry.Id);
        if (!perOption)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Right-click to set this animation's themes and acts, or hide it.");
            DrawAnimationTagMenu(animation, counts);
        }
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
                // By option rather than position, so an open tag menu stays with its option when an edit
                // reorders or shortens the list.
                ImGui.PushID(row.Entry.Id);
                DrawRow(row, collectionId, counts, perOption);
                ImGui.PopID();
            }
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    /// <param name="tagMenu">Whether the option carries its own tagging menu (see DrawAnimation).</param>
    private void DrawRow(LibraryView.Row row, Guid? collectionId, LibraryView.CategoryCounts counts, bool tagMenu)
    {
        var entry = row.Entry;
        var rowLeft = ImGui.GetCursorScreenPos().X;
        var rowRight = rowLeft + ImGui.GetContentRegionAvail().X;

        // A selectable covering the wrapped name, so long names stay readable and still carry the
        // tagging menu. The name is drawn over it.
        var namePos = ImGui.GetCursorPos();
        var nameSize = ImGui.CalcTextSize(entry.DisplayName, false, rowRight - rowLeft);
        if (tagMenu)
        {
            ImGui.Selectable("##PoseKitLibraryName", false, ImGuiSelectableFlags.None,
                new Vector2(Math.Min(nameSize.X, rowRight - rowLeft), nameSize.Y));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Right-click to set its themes and acts, or hide it.");
            DrawTagMenu(entry.DisplayName, entry.Themes, entry.Acts, entry.Hidden, counts,
                (themes, acts, hide) => Save(entry.Mod.ModName, entry.Group.Name, entry.Option.Name, themes, acts, hide));
        }
        else
        {
            ImGui.Dummy(new Vector2(Math.Min(nameSize.X, rowRight - rowLeft), nameSize.Y));
        }
        var afterName = ImGui.GetCursorPos();
        ImGui.SetCursorPos(namePos);
        using (ImRaii.PushColor(ImGuiCol.Text, PoseKitUi.Muted))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(entry.DisplayName);
            ImGui.PopTextWrapPos();
        }
        ImGui.SetCursorPos(afterName);

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
                    PlayDispatch.ClickTrigger(plugin, entry.Mod, entry.Group, entry.Option, trigger, collectionId, beforePlay,
                        offerPartnerRole: entry.IsCouple);
                }
            }
            PoseKitUi.DrawPickBadge(pick);
        }
        ImGui.Spacing();
    }

    private const float RowItemSpacing = 10;

    /// Continues the line only when an item this wide still fits before the row's right edge.
    private static void SameLineIfFits(float width, float rowRight)
    {
        if (ImGui.GetItemRectMax().X + RowItemSpacing + width <= rowRight)
            ImGui.SameLine(0, RowItemSpacing);
    }

    /// Tags every option of the animation at once, as one entry for its group: the menu shows what any
    /// of its options has, and a change applies to all of them, including ones not listed here.
    private void DrawAnimationTagMenu(LibraryView.Animation animation, LibraryView.CategoryCounts counts)
    {
        var entries = animation.Rows.Select(r => r.Entry).ToList();
        var first = entries[0];
        var themes = new HashSet<string>(entries.SelectMany(e => e.Themes), StringComparer.OrdinalIgnoreCase);
        var acts = new HashSet<string>(entries.SelectMany(e => e.Acts), StringComparer.OrdinalIgnoreCase);
        DrawTagMenu(animation.Group.Name, themes, acts, entries.All(e => e.Hidden), counts,
            (newThemes, newActs, hide) => Save(first.Mod.ModName, first.Group.Name, "*", newThemes, newActs, hide));
    }

    /// Themes and acts as checkboxes plus a new-name field each, and Hide/Unhide. Every change is
    /// saved to categories.json through <paramref name="save"/>.
    private void DrawTagMenu(string title, IReadOnlySet<string> themes, IReadOnlySet<string> acts, bool hidden,
        LibraryView.CategoryCounts counts, Action<IEnumerable<string>?, IEnumerable<string>?, bool?> save)
    {
        if (!ImGui.BeginPopupContextItem("##PoseKitTagMenu")) return;

        ImGui.TextColored(PoseKitUi.Muted, title);
        ImGui.Separator();

        if (ImGui.BeginMenu("Themes##PoseKitTagThemes"))
        {
            if (DrawNameToggles(themes, counts.KnownThemes, ref newTheme, "Theme") is { } changed)
                save(changed, null, null);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Acts##PoseKitTagActs"))
        {
            if (DrawNameToggles(acts, counts.KnownActs, ref newAct, "Act") is { } changed)
                save(null, changed, null);
            ImGui.EndMenu();
        }

        ImGui.Separator();
        if (ImGui.MenuItem(hidden ? "Unhide" : "Hide"))
            save(null, null, !hidden);

        ImGui.EndPopup();
    }

    /// The new set when the player changed it this frame, else null.
    private static HashSet<string>? DrawNameToggles(IReadOnlySet<string> current, List<string> known, ref string newName, string kind)
    {
        HashSet<string>? changed = null;
        foreach (var name in known.Concat(current).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var on = current.Contains(name);
            if (!ImGui.Checkbox($"{name}##PoseKitTag{kind}{name}", ref on)) continue;
            changed = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            if (on) changed.Add(name); else changed.Remove(name);
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(140);
        var submitted = ImGui.InputTextWithHint($"##PoseKitTagNew{kind}", $"New {kind.ToLowerInvariant()}...", ref newName, 48,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.SmallButton($"Add##PoseKitTagAdd{kind}") || submitted) && newName.Trim().Length > 0)
        {
            changed = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase) { newName.Trim() };
            newName = "";
        }
        return changed;
    }

    private void Save(string modName, string groupName, string optionName, IEnumerable<string>? themes,
        IEnumerable<string>? acts, bool? hide)
    {
        plugin.Categories.SaveEntry(modName, groupName, optionName, themes, acts, hide);
        plugin.RebuildCategories();
    }

    private void DrawPresetRow(Presets.NamedPose preset)
    {
        var pick = PoseKitUi.GetPickState(plugin, preset.Name);
        using (PoseKitUi.PushPickButtonStyle(pick))
        {
            if (ImGui.SmallButton($"{preset.Name}##PoseKitLibraryPreset{preset.GetHashCode()}"))
                PlayDispatch.ClickPreset(plugin, preset);
        }
        PoseKitUi.DrawPickBadge(pick);
        if (preset.PartnerHalf != null)
        {
            ImGui.SameLine(0, 10);
            ImGui.TextColored(PoseKitUi.Info, "Couple");
        }
    }
}
