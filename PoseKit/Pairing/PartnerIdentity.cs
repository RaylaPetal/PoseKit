namespace PoseKit.Pairing;

using System;
using Dalamud.Game.ClientState.Objects.SubKinds;

/// <summary>A character name and home world, in the "Name Surname@World" form /tell uses.</summary>
public readonly record struct PartnerIdentity(string Name, string World)
{
    public string TellAddress => $"{Name}@{World}";

    public override string ToString() => TellAddress;

    /// Uses the home world, not the world the character is visiting, since tells need it.
    public static PartnerIdentity FromCharacter(IPlayerCharacter character) =>
        new(character.Name.TextValue, character.HomeWorld.Value.Name.ExtractText());

    /// Null when either part is missing, as it can be on some context-menu targets.
    public static PartnerIdentity? FromMenuTarget(string? name, string? homeWorld) =>
        string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(homeWorld)
            ? null
            : new PartnerIdentity(name.Trim(), homeWorld.Trim());

    /// Checks the format only; whether the character exists can't be known.
    public static bool TryParse(string text, out PartnerIdentity identity, out string error)
    {
        var trimmed = text.Trim();
        var atIndex = trimmed.IndexOf('@');
        if (atIndex < 0 || trimmed.IndexOf('@', atIndex + 1) >= 0)
        {
            identity = default;
            error = "Target must be in the form \"Name Surname@World\" — exactly one '@' was expected.";
            return false;
        }

        var name = trimmed[..atIndex].Trim();
        var world = trimmed[(atIndex + 1)..].Trim();
        if (name.Length == 0)
        {
            identity = default;
            error = "A character name is required before '@'.";
            return false;
        }
        if (world.Length == 0)
        {
            identity = default;
            error = "A world name is required after '@'.";
            return false;
        }

        identity = new PartnerIdentity(name, world);
        error = "";
        return true;
    }

    public bool Matches(string? name, string? world) =>
        name is not null && world is not null &&
        string.Equals(name, Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(world, World, StringComparison.OrdinalIgnoreCase);
}
