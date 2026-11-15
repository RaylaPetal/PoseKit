using System;
using System.Security.Cryptography;
using System.Text;

namespace PoseKit.Relay;

/// The frame fields bound into the AEAD tag; they travel in the clear so the receiver can rebuild the AAD.
public readonly record struct FrameHeader(string From, string To, long Seq, long SentAt);

/// <summary>
/// Seals and opens messages between two partners (protocol/constants.json `peerMessage`). The pair key comes from static
/// ECDH between the two device keys, salted with both sides' pairing nonces, so only those two installs can read a
/// frame and a new pairing gets a new key.
/// </summary>
public static class PeerCrypto
{
    private const string Info = "posekit-relay-message-v1";
    public const int MaxAgeSeconds = 120;

    /// Salt is both 16-byte nonces, the lower device key id's first, so both sides derive the same key.
    public static byte[] DeriveKey(DeviceKey own, string ownId, byte[] ownNonce, EcPublicKey peer, string peerId, byte[] peerNonce)
    {
        var ownFirst = string.CompareOrdinal(ownId, peerId) < 0;
        var salt = new byte[ownNonce.Length + peerNonce.Length];
        Buffer.BlockCopy(ownFirst ? ownNonce : peerNonce, 0, salt, 0, 16);
        Buffer.BlockCopy(ownFirst ? peerNonce : ownNonce, 0, salt, 16, 16);
        return RelayCrypto.Hkdf(RelayCrypto.SharedSecret(own, peer), salt, Info);
    }

    public static byte[] Aad(FrameHeader h) => Encoding.UTF8.GetBytes($"posekit-frame-v1\n{h.From}\n{h.To}\n{h.Seq}\n{h.SentAt}");

    public static (string Nonce, string Ciphertext) Seal(byte[] key, FrameHeader header, string plaintext)
    {
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceBytes);
        var sealedBytes = RelayCrypto.AesGcmEncrypt(key, nonce, Encoding.UTF8.GetBytes(plaintext), Aad(header));
        return (RelayCrypto.Base64UrlEncode(nonce), RelayCrypto.Base64UrlEncode(sealedBytes));
    }

    /// Null when it doesn't open under this key and header (wrong key, tampered, or a header that was changed).
    public static string? Open(byte[] key, FrameHeader header, string nonce, string ciphertext)
    {
        try
        {
            return Encoding.UTF8.GetString(RelayCrypto.AesGcmDecrypt(key, RelayCrypto.Base64UrlDecode(nonce), RelayCrypto.Base64UrlDecode(ciphertext), Aad(header)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
