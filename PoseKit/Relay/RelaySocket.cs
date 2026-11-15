using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PoseKit.Relay;

public enum RelaySocketState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// This install's one live connection to the pairing relay (PoseKit.Relay/protocol/constants.json `socketAuth`), ported
/// from Oathbound's. Opens while <see cref="Wanted"/> is true, answers the relay's challenge with the device key, and
/// reconnects with a growing, randomized delay. Events are raised through `dispatch` (the framework thread).
/// </summary>
public sealed class RelaySocket : IDisposable
{
    /// Release-pinned; not user-configurable.
    public const string RelayOrigin = "https://posekit-relay.oathbound.workers.dev";

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    /// A connection that stayed up this long resets the backoff.
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);
    private const int MaxMessageBytes = 32 * 1024;

    private readonly Uri socketBase = new(RelayOrigin.Replace("https://", "wss://", StringComparison.Ordinal) + "/v1/socket");
    private readonly DeviceIdentity identity;
    private readonly Action<Action> dispatch;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private ClientWebSocket? socket;
    private volatile bool wanted;
    private volatile RelaySocketState state;
    private Task? loop;

    public RelaySocket(DeviceIdentity identity, Action<Action> dispatch)
    {
        this.identity = identity;
        this.dispatch = dispatch;
    }

    public RelaySocketState State => state;

    /// Relay clock minus ours, from the last challenge; used to judge a frame's age.
    public long ServerOffsetSeconds { get; private set; }

    public long ServerNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ServerOffsetSeconds;

    public event Action<RelaySocketState>? StateChanged;
    public event Action<JsonElement>? MessageReceived;

    /// Connect while true, disconnect while false. Cheap to set every frame.
    public bool Wanted
    {
        get => wanted;
        set
        {
            if (wanted == value) return;
            wanted = value;
            if (value && loop is null)
                loop = Task.Run(() => RunAsync(lifetime.Token));
            else if (!value)
                _ = CloseCurrentAsync();
            wake.Release();
        }
    }

    /// False when there's no authenticated connection to send on.
    public async Task<bool> SendAsync(string json, CancellationToken ct = default)
    {
        var ws = socket;
        if (ws is null || state != RelaySocketState.Connected) return false;
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) return false;
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            if (!wanted)
            {
                try { await wake.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }

            var startedAt = DateTime.UtcNow;
            try
            {
                await ConnectAndReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"[PoseKit] Relay connection dropped: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SetState(RelaySocketState.Disconnected);
                var old = Interlocked.Exchange(ref socket, null);
                old?.Dispose();
            }

            if (!wanted) continue;
            if (DateTime.UtcNow - startedAt >= StableAfter) backoff = MinBackoff;
            // Full jitter, so a relay restart doesn't bring every client back in the same second.
            var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * backoff.TotalMilliseconds);
            backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
            try { await wake.WaitAsync(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        var device = identity.Id;
        SetState(RelaySocketState.Connecting);
        var ws = new ClientWebSocket();
        // Protocol-level keep-alive: the relay answers these without waking or billing.
        ws.Options.KeepAliveInterval = KeepAlive;
        socket = ws;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ws.ConnectAsync(new Uri($"{socketBase}?device={device}"), connectTimeout.Token).ConfigureAwait(false);
        }

        using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            authTimeout.CancelAfter(AuthTimeout);
            using var challenge = await ReceiveAsync(ws, authTimeout.Token).ConfigureAwait(false)
                ?? throw new WebSocketException("closed before the challenge");
            var root = challenge.RootElement;
            if (root.GetProperty("type").GetString() != "challenge") throw new WebSocketException("expected a challenge");
            var nonce = root.GetProperty("nonce").GetString() ?? throw new WebSocketException("challenge without nonce");
            ServerOffsetSeconds = root.GetProperty("serverTime").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // The relay keeps no key registry: the key itself proves it hashes to the device id.
            var pub = identity.PublicKey;
            var auth = JsonSerializer.Serialize(new
            {
                type = "auth",
                publicKey = new { kty = "EC", crv = "P-256", x = pub.X, y = pub.Y },
                signature = RelayCrypto.Sign(identity.Key, RelayCrypto.SocketAuthBaseString(device, nonce)),
            });
            await ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, authTimeout.Token).ConfigureAwait(false);

            // The relay may send an over_budget notice before auth-ok; skip to the answer.
            while (true)
            {
                using var answer = await ReceiveAsync(ws, authTimeout.Token).ConfigureAwait(false)
                    ?? throw new WebSocketException("closed during auth");
                var type = answer.RootElement.GetProperty("type").GetString();
                if (type == "auth-ok") break;
                if (type == "error" && answer.RootElement.TryGetProperty("code", out var code) && code.GetString() == "over_budget")
                {
                    var element = answer.RootElement.Clone();
                    if (MessageReceived is { } early) dispatch(() => early(element));
                    continue;
                }
                throw new WebSocketException($"auth refused: {answer.RootElement.GetRawText()}");
            }
        }

        SetState(RelaySocketState.Connected);
        while (!ct.IsCancellationRequested && wanted)
        {
            using var message = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (message is null) return;
            var element = message.RootElement.Clone();
            if (element.ValueKind == JsonValueKind.Object && MessageReceived is { } handler)
                dispatch(() => handler(element));
        }
    }

    /// Null when the relay closed the connection.
    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            var total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    if (buffer.Length >= MaxMessageBytes) throw new WebSocketException("message too large");
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                total += result.Count;
                if (!result.EndOfMessage) continue;
                if (result.MessageType != WebSocketMessageType.Text) { total = 0; continue; }
                try
                {
                    return JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, total));
                }
                catch (JsonException)
                {
                    // The relay's plain "pong" or anything else that isn't JSON.
                    total = 0;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task CloseCurrentAsync()
    {
        var ws = socket;
        if (ws is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            ws.Abort();
        }
    }

    private void SetState(RelaySocketState next)
    {
        if (state == next) return;
        state = next;
        if (StateChanged is { } handler)
            dispatch(() => handler(next));
    }

    public void Dispose()
    {
        wanted = false;
        lifetime.Cancel();
        socket?.Abort();
        wake.Release();
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        socket?.Dispose();
        lifetime.Dispose();
    }
}
