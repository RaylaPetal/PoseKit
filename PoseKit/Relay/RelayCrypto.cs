using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace PoseKit.Relay;

/// A P-256 public key as JWK coordinates (base64url, 32 bytes each).
public sealed record EcPublicKey(string X, string Y);

/// A device's P-256 key pair: it signs the relay's socket challenge and agrees pair keys with ECDH.
public sealed class DeviceKey
{
    internal ECPrivateKeyParameters Private { get; init; } = null!;
    internal ECPublicKeyParameters Public { get; init; } = null!;
}

/// <summary>
/// The relay's algorithms, fixed by PoseKit.Relay/protocol/constants.json and checked against protocol/vectors.json.
/// EC work uses BouncyCastle, not the BCL's ECDsa/ECDiffieHellman: those are CNG-backed and Wine's CNG can't generate
/// EC keys, which would break pairing on Linux (as found in Oathbound). HKDF, AES-GCM and SHA-256 stay on the BCL.
/// </summary>
public static class RelayCrypto
{
    private static readonly Org.BouncyCastle.Asn1.X9.X9ECParameters Curve = Org.BouncyCastle.Asn1.Nist.NistNamedCurves.GetByName("P-256");
    private static readonly ECDomainParameters Domain = new(Curve.Curve, Curve.G, Curve.N, Curve.H);

    public const int AeadNonceBytes = 12;
    public const int AeadTagBytes = 16;

    public static string Sha256Hex(string utf8) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(utf8)));

    public static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    private static byte[] Fixed32(BigInteger value) => BigIntegers.AsUnsignedByteArray(32, value);

    // ---- keys ----

    public static DeviceKey GenerateDeviceKey()
    {
        var generator = new ECKeyPairGenerator("EC");
        generator.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return new DeviceKey { Private = (ECPrivateKeyParameters)pair.Private, Public = (ECPublicKeyParameters)pair.Public };
    }

    public static DeviceKey ImportDeviceKey(string d, EcPublicKey publicKey) => new()
    {
        Private = new ECPrivateKeyParameters("EC", new BigInteger(1, Base64UrlDecode(d)), Domain),
        Public = ImportPublic(publicKey),
    };

    public static string ExportPrivateD(DeviceKey key) => Base64UrlEncode(Fixed32(key.Private.D));

    public static EcPublicKey ExportPublic(DeviceKey key)
    {
        var point = key.Public.Q.Normalize();
        return new EcPublicKey(Base64UrlEncode(Fixed32(point.AffineXCoord.ToBigInteger())), Base64UrlEncode(Fixed32(point.AffineYCoord.ToBigInteger())));
    }

    /// Throws on a point that isn't on the curve, so a forged key is never used for ECDH.
    public static ECPublicKeyParameters ImportPublic(EcPublicKey key)
    {
        var point = Curve.Curve.ValidatePoint(new BigInteger(1, Base64UrlDecode(key.X)), new BigInteger(1, Base64UrlDecode(key.Y)));
        return new ECPublicKeyParameters("EC", point, Domain);
    }

    /// protocol `deviceKeyId`: SHA-256 of the canonical JWK, so the id names the key and the relay needs no registry.
    public static string DeviceKeyId(EcPublicKey key) => Sha256Hex($"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{key.X}\",\"y\":\"{key.Y}\"}}");

    /// protocol `inboxId`: where invites to a character are addressed, without the relay needing the name to route.
    public static string InboxId(string nameAtWorld) => Sha256Hex($"posekit-inbox-v1|{nameAtWorld}");

    // ---- signing ----

    /// Raw r||s (64 bytes), base64url, deterministic (RFC 6979), never DER.
    public static string Sign(DeviceKey key, string message)
    {
        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, key.Private);
        var rs = signer.GenerateSignature(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
        var bytes = new byte[64];
        Buffer.BlockCopy(Fixed32(rs[0]), 0, bytes, 0, 32);
        Buffer.BlockCopy(Fixed32(rs[1]), 0, bytes, 32, 32);
        return Base64UrlEncode(bytes);
    }

    public static string SocketAuthBaseString(string deviceKeyId, string nonce) => $"posekit-socket-v1\n{deviceKeyId}\n{nonce}";

    // ---- key agreement and sealing ----

    public static byte[] SharedSecret(DeviceKey own, EcPublicKey peer)
    {
        var agreement = new ECDHBasicAgreement();
        agreement.Init(own.Private);
        return Fixed32(agreement.CalculateAgreement(ImportPublic(peer)));
    }

    public static byte[] Hkdf(byte[] secret, byte[] salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.UTF8.GetBytes(info));

    /// Tag appended, matching WebCrypto's output.
    public static byte[] AesGcmEncrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
    {
        var output = new byte[plaintext.Length + AeadTagBytes];
        using var aes = new AesGcm(key, AeadTagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), aad);
        return output;
    }

    public static byte[] AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertextWithTag, byte[] aad)
    {
        if (ciphertextWithTag.Length < AeadTagBytes) throw new CryptographicException("Ciphertext shorter than its tag.");
        var length = ciphertextWithTag.Length - AeadTagBytes;
        var plaintext = new byte[length];
        using var aes = new AesGcm(key, AeadTagBytes);
        aes.Decrypt(nonce, ciphertextWithTag.AsSpan(0, length), ciphertextWithTag.AsSpan(length), plaintext, aad);
        return plaintext;
    }
}
