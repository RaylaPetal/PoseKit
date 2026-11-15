using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoseKit.Relay;

/// A verified partner: their device key, and the pair key both sides derived for this pairing.
public sealed class PeerSession(string peerId, EcPublicKey peerKey, byte[] pairKey)
{
    public string PeerId { get; } = peerId;
    public EcPublicKey PeerKey { get; } = peerKey;
    internal byte[] PairKey { get; } = pairKey;
    internal long NextSeq = 1;
    internal long LastAcceptedSeq;
}

/// <summary>
/// What an invite or acceptance carries (protocol `inviteBody`, base64url JSON). It can't be sealed, since the two sides
/// don't share a key yet, so it holds nothing secret: who's asking, their device key and their pairing nonce.
/// </summary>
public sealed class InviteBody
{
    public const string InviteKind = "invite";
    public const string AcceptKind = "accept";

    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = InviteKind;
    [JsonPropertyName("inviteId")] public string InviteId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("world")] public string World { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("x")] public string X { get; set; } = "";
    [JsonPropertyName("y")] public string Y { get; set; } = "";

    public EcPublicKey Key => new(X, Y);
}

/// <summary>
/// Sends and receives through the relay socket: sealed lines to a verified partner, and unsealed invite bodies to a
/// character's inbox. A received frame is accepted only from the session's device, with a seq above the last one
/// accepted and no older than <see cref="PeerCrypto.MaxAgeSeconds"/>, so the relay can't replay, reorder or reflect it.
/// Each pairing derives a new key from fresh nonces, so seq can restart at 1 per session.
/// </summary>
public sealed class PeerMessenger
{
    private readonly RelaySocket socket;
    private readonly DeviceIdentity identity;
    private readonly Dictionary<string, Action<string>> pendingAcks = new();
    private long nextMessageId;

    public PeerMessenger(RelaySocket socket, DeviceIdentity identity)
    {
        this.socket = socket;
        this.identity = identity;
        socket.MessageReceived += OnMessage;
    }

    /// The current partner; frames from any other device are ignored.
    public PeerSession? Session { get; set; }

    /// A sealed protocol line from the session's partner.
    public event Action<PeerSession, string>? LineReceived;

    /// An invite or acceptance; the relay vouches only for `fromDeviceId`, which the body's key must hash to.
    public event Action<string, InviteBody>? InviteReceived;

    /// A relay-level error such as over_budget.
    public event Action<string>? RelayError;

    /// Seals `line` for the session's partner. `onResult` gets the relay's delivery result, or "unavailable".
    public void SendLine(PeerSession session, string line, Action<string>? onResult = null)
    {
        var header = new FrameHeader(identity.Id, session.PeerId, session.NextSeq++, socket.ServerNow);
        var (nonce, ciphertext) = PeerCrypto.Seal(session.PairKey, header, line);
        Send(new
        {
            type = "send",
            id = NewId(onResult),
            to = session.PeerId,
            frame = new { v = 1, seq = header.Seq, sentAt = header.SentAt, nonce, ciphertext },
        }, onResult);
    }

    public void SendInvite(string inboxId, InviteBody body, Action<string>? onResult = null)
    {
        var encoded = RelayCrypto.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(body));
        Send(new { type = "invite", id = NewId(onResult), inbox = inboxId, body = encoded, sentAt = socket.ServerNow }, onResult);
    }

    public void Register(string? inboxId) => _ = socket.SendAsync(JsonSerializer.Serialize(new { type = "register", inbox = inboxId }));

    private string NewId(Action<string>? onResult)
    {
        var id = $"m{++nextMessageId}";
        if (onResult != null) pendingAcks[id] = onResult;
        return id;
    }

    private void Send(object message, Action<string>? onResult)
    {
        var json = JsonSerializer.Serialize(message);
        var id = JsonDocument.Parse(json).RootElement.GetProperty("id").GetString()!;
        _ = socket.SendAsync(json).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result) return;
            Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (pendingAcks.Remove(id, out var callback)) callback("unavailable");
            });
        });
    }

    private void OnMessage(JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "ack":
                var id = message.GetProperty("id").GetString() ?? "";
                var result = message.GetProperty("result").GetString() ?? "";
                // "held" is followed later by the final delivered/offline update for the same id.
                if (result == "held") return;
                if (pendingAcks.Remove(id, out var callback)) callback(result);
                break;
            case "frame":
                OnFrame(message);
                break;
            case "invite":
                OnInvite(message);
                break;
            case "error":
                if (message.TryGetProperty("code", out var code) && code.GetString() is { } c) RelayError?.Invoke(c);
                break;
        }
    }

    private void OnFrame(JsonElement message)
    {
        if (Session is not { } session) return;
        var from = message.GetProperty("from").GetString();
        if (from != session.PeerId || from == identity.Id) return;
        var seq = message.GetProperty("seq").GetInt64();
        var sentAt = message.GetProperty("sentAt").GetInt64();
        if (seq <= session.LastAcceptedSeq) return;
        if (Math.Abs(socket.ServerNow - sentAt) > PeerCrypto.MaxAgeSeconds + 5) return;

        var line = PeerCrypto.Open(session.PairKey, new FrameHeader(from, identity.Id, seq, sentAt),
            message.GetProperty("nonce").GetString() ?? "", message.GetProperty("ciphertext").GetString() ?? "");
        if (line is null) return;
        session.LastAcceptedSeq = seq;
        LineReceived?.Invoke(session, line);
    }

    private void OnInvite(JsonElement message)
    {
        var from = message.GetProperty("from").GetString() ?? "";
        InviteBody? body;
        try
        {
            body = JsonSerializer.Deserialize<InviteBody>(Encoding.UTF8.GetString(RelayCrypto.Base64UrlDecode(message.GetProperty("body").GetString() ?? "")));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return;
        }
        if (body is not { Version: 1 } || body.Name.Length == 0 || body.World.Length == 0) return;
        // The body's key must be the one the relay authenticated, or anyone could put someone else's key in an invite.
        if (RelayCrypto.DeviceKeyId(body.Key) != from || from == identity.Id) return;
        InviteReceived?.Invoke(from, body);
    }
}
