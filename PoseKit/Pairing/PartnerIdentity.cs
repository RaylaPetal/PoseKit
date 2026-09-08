namespace PoseKit.Pairing;

using System;

/// <summary>A character name + home world, the addressing unit for pairing and couple presets — the
/// same "Name Surname@World" shape a /tell target already needs.</summary>
public readonly record struct PartnerIdentity(string Name, string World)
{
    public string TellAddress => $"{Name}@{World}";

    public override string ToString() => TellAddress;

    /// Parses a user-typed "Name Surname@World" string. Cannot and does not check whether the
    /// character actually exists — that information isn't available to the plugin — this only
    /// catches structural mistakes (no '@', empty name/world) before they become a `/tell` the game
    /// silently rejects with no plugin-visible feedback.
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
