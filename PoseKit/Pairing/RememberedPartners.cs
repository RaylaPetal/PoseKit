namespace PoseKit.Pairing;

using System;
using System.Collections.Generic;
using System.Linq;
using PoseKit.Relay;

/// A partner this side has paired with before, and the device key they paired with.
[Serializable]
public sealed class RememberedPartner
{
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
    public string KeyX { get; set; } = "";
    public string KeyY { get; set; } = "";
    public DateTime LastPairedUtc { get; set; }

    public PartnerIdentity Identity => new(Name, World);
    public string DeviceKeyId => RelayCrypto.DeviceKeyId(new EcPublicKey(KeyX, KeyY));
}

/// <summary>
/// Partners whose device key this side already trusts, so pairing again needs no targeting and works from anywhere.
/// A partner whose key changed (reinstall, lost config) no longer matches and goes back to the targeting check.
/// Entries stay until the player forgets them.
/// </summary>
public static class RememberedPartners
{
    public static IEnumerable<RememberedPartner> All(Configuration configuration) =>
        configuration.RememberedPartners.OrderByDescending(p => p.LastPairedUtc);

    public static RememberedPartner? Find(Configuration configuration, PartnerIdentity identity) =>
        configuration.RememberedPartners.FirstOrDefault(p => identity.Matches(p.Name, p.World));

    /// True when this device is the one remembered for that character.
    public static bool Trusts(Configuration configuration, PartnerIdentity identity, string deviceKeyId) =>
        Find(configuration, identity) is { } known && known.DeviceKeyId == deviceKeyId;

    public static void Remember(Configuration configuration, PartnerIdentity identity, EcPublicKey key)
    {
        var entry = Find(configuration, identity);
        if (entry == null)
        {
            entry = new RememberedPartner { Name = identity.Name, World = identity.World };
            configuration.RememberedPartners.Add(entry);
        }
        entry.KeyX = key.X;
        entry.KeyY = key.Y;
        entry.LastPairedUtc = DateTime.UtcNow;
        configuration.Save();
    }

    public static void Forget(Configuration configuration, PartnerIdentity identity)
    {
        if (configuration.RememberedPartners.RemoveAll(p => identity.Matches(p.Name, p.World)) > 0)
            configuration.Save();
    }
}
