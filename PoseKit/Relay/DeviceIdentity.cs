namespace PoseKit.Relay;

/// <summary>
/// This install's P-256 key for the pairing relay, generated on first use and kept in Configuration. Its hash is the
/// device id: the relay hub it connects to, and what remembered partners know it by.
/// </summary>
public sealed class DeviceIdentity(Configuration configuration)
{
    private DeviceKey? key;

    public DeviceKey Key => key ??= LoadOrCreate();
    public EcPublicKey PublicKey => RelayCrypto.ExportPublic(Key);
    public string Id => RelayCrypto.DeviceKeyId(PublicKey);

    private DeviceKey LoadOrCreate()
    {
        if (configuration.RelayDeviceKeyD.Length > 0)
        {
            try
            {
                return RelayCrypto.ImportDeviceKey(configuration.RelayDeviceKeyD,
                    new EcPublicKey(configuration.RelayDeviceKeyX, configuration.RelayDeviceKeyY));
            }
            catch (System.Exception ex)
            {
                // A damaged key only costs remembered partners their shortcut; they re-verify by targeting.
                Plugin.Log.Warning(ex, "[PoseKit] Relay device key unreadable; making a new one.");
            }
        }

        var created = RelayCrypto.GenerateDeviceKey();
        var pub = RelayCrypto.ExportPublic(created);
        configuration.RelayDeviceKeyD = RelayCrypto.ExportPrivateD(created);
        configuration.RelayDeviceKeyX = pub.X;
        configuration.RelayDeviceKeyY = pub.Y;
        configuration.Save();
        return created;
    }
}
