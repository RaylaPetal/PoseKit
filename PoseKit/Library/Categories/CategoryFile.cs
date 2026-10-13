using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PoseKit.Library.Categories;

/// <summary>A keyword rule: adds its themes and acts to every option whose option or group name matches.</summary>
public sealed class CategoryRuleDto
{
    public string Match { get; set; } = "";
    public List<string>? Themes { get; set; }
    public List<string>? Acts { get; set; }
}

/// <summary>
/// Categories for the options matching a mod, group and option name pattern. Themes and acts replace
/// what earlier layers gave; null leaves them as they are. A missing mod falls back to the file's
/// <see cref="CategoryFileDto.Mod"/>; a missing group matches any group.
/// </summary>
public sealed class CategoryEntryDto
{
    public string? Mod { get; set; }

    /// Needed where a mod reuses an option name across groups ("Ears On", "Enabled").
    public string? Group { get; set; }

    public string Option { get; set; } = "*";
    public List<string>? Themes { get; set; }
    public List<string>? Acts { get; set; }
    public bool? Hide { get; set; }
}

public sealed class CategoryFileDto
{
    /// The mod pattern for entries that don't name one, so a file for one mod needn't repeat it.
    public string? Mod { get; set; }

    public List<CategoryRuleDto>? Rules { get; set; }
    public List<CategoryEntryDto>? Animations { get; set; }
}

/// <summary>
/// The parsed form of the player's categories.json: its rules add, then its entries replace.
/// </summary>
public sealed class CategorySource
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keeps names like "Nightlife+" and "N&S" readable for hand-editing.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The number must be set apart ("176 ", "121. ", "3)"), so "3some" or "2B" keep theirs.
    private static readonly Regex LeadingNumber = new(@"^\s*\d+(?:[.)]\s*|\s+)", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private List<(Regex Pattern, CategoryRuleDto Rule)> rules = [];
    private List<(Regex Mod, Regex Group, Regex Option, CategoryEntryDto Entry)> entries = [];

    /// For notices, e.g. "categories.json".
    public string Name { get; }

    public CategoryFileDto File { get; }

    public CategorySource(string name, CategoryFileDto file, List<string> notices)
    {
        Name = name;
        File = file;
        Compile(notices);
    }

    /// Null when the text isn't valid JSON; the reason is added to <paramref name="notices"/>.
    public static CategorySource? TryParse(string name, string text, List<string> notices)
    {
        try
        {
            var dto = string.IsNullOrWhiteSpace(text)
                ? new CategoryFileDto()
                : JsonSerializer.Deserialize<CategoryFileDto>(text, JsonOptions) ?? new CategoryFileDto();
            return new CategorySource(name, dto, notices);
        }
        catch (JsonException ex)
        {
            notices.Add($"{name} couldn't be read ({ex.Message}) and was skipped.");
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't read {name}; skipping it.");
            return null;
        }
    }

    /// Re-compiles after File changed. Bad rule patterns are skipped and reported.
    public void Compile(List<string> notices)
    {
        rules = [];
        foreach (var rule in File.Rules ?? [])
        {
            try
            {
                rules.Add((new Regex(rule.Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), rule));
            }
            catch (ArgumentException ex)
            {
                notices.Add($"{Name}: rule \"{rule.Match}\" skipped: {ex.Message}");
            }
        }

        entries = (File.Animations ?? [])
            .Select(entry => (Wildcard(entry.Mod ?? File.Mod ?? "*"), Wildcard(entry.Group ?? "*"),
                Wildcard(NormalizeName(entry.Option)), entry))
            .ToList();
    }

    /// Adds this file's rule categories, then applies its entries. Returns the hidden flag its
    /// entries set, or null when none set one.
    public bool? Apply(string modName, string groupName, string optionName, HashSet<string> themes, HashSet<string> acts)
    {
        foreach (var (pattern, rule) in rules)
        {
            if (!pattern.IsMatch(optionName) && !pattern.IsMatch(groupName)) continue;
            themes.UnionWith(Clean(rule.Themes));
            acts.UnionWith(Clean(rule.Acts));
        }

        bool? hidden = null;
        var normalized = NormalizeName(optionName);
        foreach (var (mod, group, option, entry) in entries)
        {
            if (!mod.IsMatch(modName) || !group.IsMatch(groupName) || !option.IsMatch(normalized)) continue;
            if (entry.Themes != null) { themes.Clear(); themes.UnionWith(Clean(entry.Themes)); }
            if (entry.Acts != null) { acts.Clear(); acts.UnionWith(Clean(entry.Acts)); }
            if (entry.Hide is { } hide) hidden = hide;
        }
        return hidden;
    }

    /// The option name without a leading number ("176 ", "121. ") and with runs of spaces collapsed,
    /// so entries survive renumbering and match across mods that number differently. A name that is
    /// only a number keeps it, so options "1", "2" and "3" don't all become "" and match each other.
    public static string NormalizeName(string name)
    {
        var collapsed = Whitespace.Replace(name, " ").Trim();
        var stripped = LeadingNumber.Replace(collapsed, "").Trim();
        return stripped.Length > 0 ? stripped : collapsed;
    }

    /// A whole-name, case-insensitive pattern where * matches anything.
    private static Regex Wildcard(string pattern) =>
        new("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<string> Clean(IEnumerable<string>? names) =>
        (names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0);
}

/// <summary>
/// The player's categories.json: keyword rules and per-animation entries, applied after automatic
/// categories.
///
/// An unreadable file leaves no player categories, and is renamed to .bad on the next in-game edit
/// rather than overwritten. Saves go through a temp file. Saving rewrites the whole file, so
/// hand-written comments don't survive an in-game edit.
/// </summary>
public sealed class CategoryFile
{
    private const string FileName = "categories.json";

    private readonly string path;
    private readonly string configDirectory;
    private bool loadFailed;

    /// Problems from the last load, for the library header.
    public List<string> Notices { get; } = [];

    public CategorySource Source { get; private set; } = new(FileName, new CategoryFileDto(), []);

    public CategoryFile(string configDirectory)
    {
        this.configDirectory = configDirectory;
        path = Path.Combine(configDirectory, FileName);
        Load();
    }

    public string FilePath => path;

    /// Re-reads the file. A missing file means no player categories.
    public void Load()
    {
        Notices.Clear();
        loadFailed = false;

        string text;
        try { text = File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't read {path}.");
            text = "";
            loadFailed = true;
            Notices.Add($"{FileName} couldn't be read ({ex.Message}).");
        }

        if (CategorySource.TryParse(FileName, text, Notices) is { } source)
        {
            Source = source;
            return;
        }
        loadFailed = true;
        Source = new CategorySource(FileName, new CategoryFileDto(), Notices);
    }

    /// Sets one option's full themes, acts or hidden flag (null leaves a field as it is), in an
    /// entry for exactly that mod, group and option, moved to the end so it wins over wildcard entries.
    public void SaveEntry(string modName, string groupName, string optionName, IEnumerable<string>? themes,
        IEnumerable<string>? acts, bool? hide)
    {
        var option = CategorySource.NormalizeName(optionName);
        var list = Source.File.Animations ??= [];
        var existing = list.FirstOrDefault(e =>
            string.Equals(e.Mod, modName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Group, groupName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CategorySource.NormalizeName(e.Option), option, StringComparison.OrdinalIgnoreCase));

        var entry = existing ?? new CategoryEntryDto { Mod = modName, Group = groupName, Option = option };
        list.Remove(entry);
        list.Add(entry);
        if (themes != null) entry.Themes = themes.ToList();
        if (acts != null) entry.Acts = acts.ToList();
        // An explicit false, so unhiding wins over an earlier wildcard entry that hides.
        if (hide != null) entry.Hide = hide.Value;

        // The saved file is exactly this source, so only its own problems remain, plus a failed save.
        Notices.Clear();
        Source.Compile(Notices);
        if (!Save())
            Notices.Add($"{FileName} couldn't be saved; see /xllog.");
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(configDirectory);
            if (loadFailed && File.Exists(path))
                File.Move(path, path + ".bad", overwrite: true);
            loadFailed = false;

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Source.File, CategorySource.JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PoseKit] Couldn't save categories to {path}.");
            return false;
        }
    }
}
