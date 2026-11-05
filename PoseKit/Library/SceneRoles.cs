using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PoseKit.Penumbra;

namespace PoseKit.Library;

/// <summary>One side of a scene: its role as the name labels it, and the trigger that plays it.</summary>
public readonly record struct SceneRole(string Label, PoseTriggerHint Trigger);

/// <summary>A two-role animation whose name says which trigger is whose.</summary>
public sealed record Scene(SceneRole Dominant, SceneRole Other)
{
    /// The other side of a scene from the given trigger; null when it isn't one of this scene's.
    public SceneRole? OppositeOf(PoseTriggerHint trigger) =>
        Dominant.Trigger == trigger ? Other : Other.Trigger == trigger ? Dominant : null;
}

/// <summary>
/// Reads a scene from a name such as "(Dom-Confirm/Sub-Sweep)" or "[Sub-Gsit1, Dom-Gsit2]". Anything
/// less clear isn't a scene and keeps the normal pick-and-queue flow: a mistake here would play the
/// wrong sides, while a miss only costs a click. That includes several triggers per role, which
/// variant packs write as "Dom-B,S,dances".
/// </summary>
public static class SceneRoles
{
    private static readonly Regex Enclosed = new(@"[\(\[]([^()\[\]]*)[\)\]]", RegexOptions.Compiled);

    private static readonly Regex RolePart = new(@"^\s*(f?dom\d*|top|hugger|sub\d*|bot|hugged)\s*-\s*([a-z0-9]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PoseToken = new(@"^(csit|chairsit|chair|sit|gsit|groundsit|bedpose|bed|doze)0*(\d)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleNoise = new(@"\[[^\]]*\]|\([^)]*\)|[-]", RegexOptions.Compiled);

    /// Shorthand mod authors use for emote commands.
    private static readonly Dictionary<string, string[]> EmoteAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bdance"] = ["balldance"],
        ["hdance"] = ["harvestdance"],
        ["sdance"] = ["stepdance"],
        ["gdance"] = ["golddance"],
        ["tdance"] = ["thavnairiandance"],
        ["beeknees"] = ["beesknees"],
    };

    /// The names to read, most specific first. A mod's name counts because standalone mods put the
    /// roles there; the triggers must still match, so it can't mislabel another animation.
    public static Scene? TryParse(string optionName, string groupName, string modName, IReadOnlyList<PoseTriggerHint> triggers)
    {
        foreach (var name in new[] { optionName, groupName, modName })
        {
            if (FromName(name, triggers) is { } scene) return scene;
        }
        return null;
    }

    private static Scene? FromName(string name, IReadOnlyList<PoseTriggerHint> triggers)
    {
        foreach (Match enclosed in Enclosed.Matches(name))
        {
            var parts = enclosed.Groups[1].Value.Split(['/', ',']);
            if (parts.Length != 2) continue;

            var first = RolePart.Match(parts[0]);
            var second = RolePart.Match(parts[1]);
            if (!first.Success || !second.Success) continue;

            var firstIsDominant = IsDominant(first.Groups[1].Value);
            if (firstIsDominant == IsDominant(second.Groups[1].Value)) continue;

            if (Resolve(first.Groups[2].Value, triggers) is not { } firstTrigger ||
                Resolve(second.Groups[2].Value, triggers) is not { } secondTrigger ||
                firstTrigger == secondTrigger)
                continue;

            var firstRole = new SceneRole(LabelOf(first.Groups[1].Value), firstTrigger);
            var secondRole = new SceneRole(LabelOf(second.Groups[1].Value), secondTrigger);
            return firstIsDominant ? new Scene(firstRole, secondRole) : new Scene(secondRole, firstRole);
        }
        return null;
    }

    private static bool IsDominant(string label) =>
        label.StartsWith("dom", StringComparison.OrdinalIgnoreCase) ||
        label.StartsWith("fdom", StringComparison.OrdinalIgnoreCase) ||
        label.Equals("top", StringComparison.OrdinalIgnoreCase) ||
        label.Equals("hugger", StringComparison.OrdinalIgnoreCase);

    private static string LabelOf(string label)
    {
        var withoutNumber = label.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return withoutNumber.Equals("fdom", StringComparison.OrdinalIgnoreCase)
            ? "FDom"
            : char.ToUpperInvariant(withoutNumber[0]) + withoutNumber[1..].ToLowerInvariant();
    }

    private static PoseTriggerHint? Resolve(string token, IReadOnlyList<PoseTriggerHint> triggers)
    {
        var pose = PoseToken.Match(token);
        if (pose.Success)
        {
            var mode = pose.Groups[1].Value.ToLowerInvariant() switch
            {
                "gsit" or "groundsit" => LibraryIndex.GroundSitMode,
                "bedpose" or "bed" or "doze" => LibraryIndex.DozeMode,
                _ => LibraryIndex.ChairSitMode,
            };
            var state = byte.Parse(pose.Groups[2].Value);
            foreach (var trigger in triggers)
            {
                if (trigger.PoseIdentifier is { } id && id.EmoteModeId == mode && id.CPoseState == state) return trigger;
            }
            return null;
        }

        var commands = EmoteAliases.TryGetValue(token, out var aliases) ? aliases.Append(token) : [token];
        foreach (var command in commands)
        {
            foreach (var trigger in triggers)
            {
                if (string.Equals(trigger.SlashCommand, command, StringComparison.OrdinalIgnoreCase)) return trigger;
            }
        }
        return null;
    }

    /// An animation's name for offers: without its number, credits, emotes or the author's notes, and
    /// short enough to fit in a tell.
    public static string OfferTitle(string name)
    {
        const int maxLength = 60;

        var notes = name.IndexOfAny(['■', '▼']);
        var text = TitleNoise.Replace(notes >= 0 ? name[..notes] : name, " ");
        text = Regex.Replace(Regex.Replace(text, @"^\s*\d+[.)]?\s", ""), @"\s+", " ").Trim();
        if (text.Length == 0) text = name.Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }
}
