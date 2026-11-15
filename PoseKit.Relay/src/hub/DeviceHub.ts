import { DurableObject } from "cloudflare:workers";
import { BudgetMeter } from "../budget/Budget";
import type { Env } from "../env";
import type { CharacterInbox } from "../inbox/CharacterInbox";
import {
  DEVICE_BURST_FRAMES,
  DEVICE_BURST_WINDOW_SECONDS,
  DEVICE_HOURLY_FRAMES,
  FRAME_CIPHERTEXT_MAX_CHARS,
  HELD_FRAMES_PER_SENDER,
  HEX64,
  HOLD_SECONDS,
  INVITE_BODY_MAX_CHARS,
  MAX_CLIENT_MESSAGE_CHARS,
  SOCKET_AUTH_TIMEOUT_SECONDS,
  TIMESTAMP_TOLERANCE_SECONDS,
  nowSeconds,
  socketAuthBaseString,
} from "../lib/constants";
import { deviceKeyIdFor, parsePublicKey, randomBase64Url, verifySignature } from "../lib/crypto";

export type DeliveryResult = "delivered" | "held" | "offline" | "refused" | "over_limit" | "over_budget";

/** A sealed peer frame. Everything but `sentAt` is opaque to the relay. */
export interface PeerFrame {
  v: number;
  seq: number;
  sentAt: number;
  nonce: string;
  ciphertext: string;
}

interface Attachment {
  state: "pending" | "authed";
  challenge: string;
  openedAt: number;
}

const AEAD_NONCE = /^[A-Za-z0-9_-]{16}$/;
const CIPHERTEXT = new RegExp(`^[A-Za-z0-9_-]{22,${FRAME_CIPHERTEXT_MAX_CHARS}}$`);
const INVITE_BODY = new RegExp(`^[A-Za-z0-9_-]{1,${INVITE_BODY_MAX_CHARS}}$`);
const MESSAGE_ID = /^[A-Za-z0-9_-]{1,32}$/;

/**
 * One per device (addressed by its device key id). Holds that device's live connection with the hibernation API, so an
 * idle connection costs nothing; forwards its frames to other hubs and its invites to character inboxes, and briefly
 * holds frames for it while it reconnects. Ported from Oathbound's DeviceHub, without its D1 pair rows: PoseKit's
 * clients decide whom to trust, so the hub delivers to any device the sender names.
 */
export class DeviceHub extends DurableObject<Env> {
  private readonly sql: SqlStorage;
  // Per-device send limits live in memory: a hub that hibernated wasn't sending, so losing them is harmless.
  private sendTimes: number[] = [];
  // Sends are chained so one device's frames stay in order, which the receiver's seq check relies on.
  private sendChain: Promise<void> = Promise.resolve();
  private readonly budget: BudgetMeter;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.budget = new BudgetMeter(env);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS held_frames (
      id INTEGER PRIMARY KEY AUTOINCREMENT, sender TEXT NOT NULL, message_id TEXT NOT NULL, wire TEXT NOT NULL,
      expires_at INTEGER NOT NULL)`);
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  // ---- connection lifecycle -------------------------------------------------------------------------------------

  override async fetch(request: Request): Promise<Response> {
    const device = request.headers.get("x-hub-device");
    if (!device || !HEX64.test(device) || request.headers.get("upgrade")?.toLowerCase() !== "websocket") {
      return new Response(null, { status: 400 });
    }
    this.setMeta("device", device);

    const { 0: client, 1: server } = new WebSocketPair();
    const attachment: Attachment = { state: "pending", challenge: randomBase64Url(16), openedAt: nowSeconds() };
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment(attachment);
    server.send(JSON.stringify({ type: "challenge", nonce: attachment.challenge, serverTime: attachment.openedAt }));
    await this.scheduleAlarm(attachment.openedAt + SOCKET_AUTH_TIMEOUT_SECONDS);
    return new Response(null, { status: 101, webSocket: client });
  }

  override async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message !== "string" || message.length > MAX_CLIENT_MESSAGE_CHARS) return this.reject(ws, "invalid_request");
    let body: Record<string, unknown>;
    try {
      const parsed: unknown = JSON.parse(message);
      if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) throw new Error();
      body = parsed as Record<string, unknown>;
    } catch {
      return this.reject(ws, "invalid_request");
    }

    const attachment = ws.deserializeAttachment() as Attachment | null;
    if (!attachment) return this.reject(ws, "unauthorized");
    if (attachment.state === "pending") return this.authenticate(ws, attachment, body);
    switch (body.type) {
      case "register":
        return this.register(ws, body);
      case "send":
      case "invite": {
        const run = this.sendChain.then(() => (body.type === "send" ? this.send(ws, body) : this.invite(ws, body)));
        this.sendChain = run.catch(() => undefined);
        return run;
      }
      default:
        return this.reject(ws, "invalid_request");
    }
  }

  override async webSocketClose(ws: WebSocket, code: number): Promise<void> {
    try { ws.close(code === 1005 || code === 1006 ? 1000 : code, "closed"); } catch { /* already closed */ }
    await this.leaveInboxIfGone(ws);
  }

  override async webSocketError(ws: WebSocket): Promise<void> {
    try { ws.close(1011, "error"); } catch { /* already closed */ }
    await this.leaveInboxIfGone(ws);
  }

  private async authenticate(ws: WebSocket, attachment: Attachment, body: Record<string, unknown>): Promise<void> {
    const device = this.getMeta("device");
    const key = parsePublicKey(body.publicKey);
    if (body.type !== "auth" || !key || typeof body.signature !== "string" || !device) return this.reject(ws, "unauthorized");
    if (nowSeconds() - attachment.openedAt > SOCKET_AUTH_TIMEOUT_SECONDS) return this.reject(ws, "unauthorized");
    // The id is the key's hash, so a client can only answer for the hub its own key names.
    if ((await deviceKeyIdFor(key)) !== device) return this.reject(ws, "unauthorized");
    if (!(await verifySignature(key, body.signature, socketAuthBaseString(device, attachment.challenge)))) {
      return this.reject(ws, "unauthorized");
    }

    for (const other of this.authedSockets()) {
      if (other === ws) continue;
      try {
        other.send(JSON.stringify({ type: "error", code: "replaced" }));
        other.close(4000, "replaced");
      } catch { /* already gone */ }
    }
    ws.serializeAttachment({ state: "authed", challenge: randomBase64Url(16), openedAt: attachment.openedAt } satisfies Attachment);
    if (!(await this.budget.charge())) {
      ws.send(JSON.stringify({ type: "error", code: "over_budget" }));
    }
    ws.send(JSON.stringify({ type: "auth-ok", deviceKeyId: device, serverTime: nowSeconds() }));
    await this.flushHeld(ws);
  }

  private reject(ws: WebSocket, code: "unauthorized" | "invalid_request"): void {
    try {
      ws.send(JSON.stringify({ type: "error", code }));
      ws.close(4001, code);
    } catch { /* already closed */ }
  }

  private authedSockets(): WebSocket[] {
    return this.ctx.getWebSockets().filter((s) => (s.deserializeAttachment() as Attachment | null)?.state === "authed");
  }

  // ---- character inbox ------------------------------------------------------------------------------------------

  /** `inbox: null` leaves the current one (logged out to the title screen). */
  private async register(ws: WebSocket, body: Record<string, unknown>): Promise<void> {
    const inbox = body.inbox;
    if (inbox !== null && (typeof inbox !== "string" || !HEX64.test(inbox))) return this.reject(ws, "invalid_request");
    const device = this.getMeta("device")!;
    const previous = this.getMeta("inbox");
    if (previous && previous !== inbox) await this.inbox(previous).unregister(device);
    if (inbox) {
      await this.inbox(inbox).register(device);
      this.setMeta("inbox", inbox);
    } else {
      this.deleteMeta("inbox");
    }
    ws.send(JSON.stringify({ type: "registered", inbox }));
  }

  private async leaveInboxIfGone(closing: WebSocket): Promise<void> {
    if (this.authedSockets().some((s) => s !== closing && s.readyState === WebSocket.OPEN)) return;
    const inbox = this.getMeta("inbox");
    if (!inbox) return;
    this.deleteMeta("inbox");
    await this.inbox(inbox).unregister(this.getMeta("device")!).catch(() => undefined);
  }

  private inbox(inboxId: string): DurableObjectStub<CharacterInbox> {
    return this.env.CHARACTER_INBOX.get(this.env.CHARACTER_INBOX.idFromName(inboxId));
  }

  // ---- sending --------------------------------------------------------------------------------------------------

  private async send(ws: WebSocket, body: Record<string, unknown>): Promise<void> {
    const id = typeof body.id === "string" && MESSAGE_ID.test(body.id) ? body.id : null;
    if (!id) return this.reject(ws, "invalid_request");
    const ack = (result: DeliveryResult) => ws.send(JSON.stringify({ type: "ack", id, result }));

    const to = body.to;
    const frame = parseFrame(body.frame);
    const self = this.getMeta("device")!;
    if (typeof to !== "string" || !HEX64.test(to) || to === self || !frame || !fresh(frame.sentAt)) return ack("refused");
    if (!this.withinLimits()) return ack("over_limit");
    if (!(await this.budget.charge())) return ack("over_budget");

    ack(await this.hub(to).deliver(frame, self, id));
  }

  private async invite(ws: WebSocket, body: Record<string, unknown>): Promise<void> {
    const id = typeof body.id === "string" && MESSAGE_ID.test(body.id) ? body.id : null;
    if (!id) return this.reject(ws, "invalid_request");
    const ack = (result: DeliveryResult) => ws.send(JSON.stringify({ type: "ack", id, result }));

    const { inbox, body: inviteBody, sentAt } = body;
    if (typeof inbox !== "string" || !HEX64.test(inbox)) return ack("refused");
    if (typeof inviteBody !== "string" || !INVITE_BODY.test(inviteBody)) return ack("refused");
    if (!Number.isSafeInteger(sentAt) || !fresh(sentAt as number)) return ack("refused");
    if (!this.withinLimits()) return ack("over_limit");
    if (!(await this.budget.charge())) return ack("over_budget");

    ack(await this.inbox(inbox).invite(this.getMeta("device")!, inviteBody, sentAt as number));
  }

  private withinLimits(): boolean {
    const now = nowSeconds();
    this.sendTimes = this.sendTimes.filter((t) => now - t < 3600);
    const burst = this.sendTimes.filter((t) => now - t < DEVICE_BURST_WINDOW_SECONDS).length;
    if (this.sendTimes.length >= DEVICE_HOURLY_FRAMES || burst >= DEVICE_BURST_FRAMES) return false;
    this.sendTimes.push(now);
    return true;
  }

  private hub(deviceKeyId: string): DurableObjectStub<DeviceHub> {
    return this.env.DEVICE_HUB.get(this.env.DEVICE_HUB.idFromName(deviceKeyId));
  }

  // ---- receiving (RPC from other hubs and inboxes) --------------------------------------------------------------

  /** A sealed frame for this hub's device from `sender`'s hub. */
  async deliver(frame: PeerFrame, sender: string, messageId: string): Promise<DeliveryResult> {
    const wire = JSON.stringify({ type: "frame", from: sender, ...frame });
    if (this.push(wire)) return "delivered";
    // A device that never connected (or not for a long time) has nothing to hold for.
    if (this.getMeta("everConnected") !== "1") return "offline";

    const held = this.sql.exec<{ n: number }>(`SELECT COUNT(*) AS n FROM held_frames WHERE sender = ?`, sender).one().n;
    if (held >= HELD_FRAMES_PER_SENDER) return "offline";
    const expiresAt = Math.min(frame.sentAt, nowSeconds()) + HOLD_SECONDS;
    this.sql.exec(`INSERT INTO held_frames (sender, message_id, wire, expires_at) VALUES (?, ?, ?, ?)`, sender, messageId, wire, expiresAt);
    await this.scheduleAlarm(expiresAt);
    return "held";
  }

  /** An invite from a character inbox. Never held: false tells the inbox this device is gone. */
  async deliverInvite(sender: string, body: string, sentAt: number): Promise<boolean> {
    return this.push(JSON.stringify({ type: "invite", from: sender, body, sentAt }));
  }

  /** The final result of a frame this device sent that the peer's hub was holding. */
  async deliveryUpdate(messageId: string, result: "delivered" | "offline"): Promise<void> {
    this.push(JSON.stringify({ type: "ack", id: messageId, result }));
  }

  private push(wire: string): boolean {
    let sent = false;
    for (const s of this.authedSockets()) {
      // A socket that's closing throws; the frame then counts as not delivered.
      try { s.send(wire); sent = true; } catch { /* closing */ }
    }
    return sent;
  }

  private async flushHeld(ws: WebSocket): Promise<void> {
    this.setMeta("everConnected", "1");
    const now = nowSeconds();
    const rows = this.sql
      .exec<{ sender: string; message_id: string; wire: string; expires_at: number }>(
        `SELECT sender, message_id, wire, expires_at FROM held_frames ORDER BY id`)
      .toArray();
    if (rows.length === 0) return;
    this.sql.exec(`DELETE FROM held_frames`);
    for (const row of rows) {
      const live = row.expires_at > now;
      if (live) ws.send(row.wire);
      this.ctx.waitUntil(this.hub(row.sender).deliveryUpdate(row.message_id, live ? "delivered" : "offline").catch(() => undefined));
    }
  }

  // ---- alarm: auth timeouts and held-frame expiry ---------------------------------------------------------------

  override async alarm(): Promise<void> {
    const now = nowSeconds();
    for (const s of this.ctx.getWebSockets()) {
      const a = s.deserializeAttachment() as Attachment | null;
      if (a?.state === "pending" && now - a.openedAt >= SOCKET_AUTH_TIMEOUT_SECONDS) this.reject(s, "unauthorized");
    }

    const expired = this.sql
      .exec<{ sender: string; message_id: string }>(`SELECT sender, message_id FROM held_frames WHERE expires_at <= ?`, now)
      .toArray();
    if (expired.length > 0) {
      this.sql.exec(`DELETE FROM held_frames WHERE expires_at <= ?`, now);
      await Promise.all(expired.map((row) => this.hub(row.sender).deliveryUpdate(row.message_id, "offline").catch(() => undefined)));
    }

    const nextHeld = this.sql.exec<{ t: number | null }>(`SELECT MIN(expires_at) AS t FROM held_frames`).one().t;
    const nextAuth = this.ctx.getWebSockets()
      .map((s) => s.deserializeAttachment() as Attachment | null)
      .filter((a): a is Attachment => a?.state === "pending")
      .map((a) => a.openedAt + SOCKET_AUTH_TIMEOUT_SECONDS);
    const next = [nextHeld, ...nextAuth].filter((t): t is number => typeof t === "number");
    if (next.length > 0) await this.ctx.storage.setAlarm(Math.max(Math.min(...next), now + 1) * 1000);
  }

  private async scheduleAlarm(atSeconds: number): Promise<void> {
    const current = await this.ctx.storage.getAlarm();
    if (current === null || current > atSeconds * 1000) await this.ctx.storage.setAlarm(atSeconds * 1000);
  }

  private getMeta(key: string): string | null {
    return this.sql.exec<{ value: string }>(`SELECT value FROM meta WHERE key = ?`, key).toArray()[0]?.value ?? null;
  }

  private setMeta(key: string, value: string): void {
    if (this.getMeta(key) === value) return;
    this.sql.exec(`INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)`, key, value);
  }

  private deleteMeta(key: string): void {
    this.sql.exec(`DELETE FROM meta WHERE key = ?`, key);
  }
}

function fresh(sentAt: number): boolean {
  return Math.abs(nowSeconds() - sentAt) <= HOLD_SECONDS + TIMESTAMP_TOLERANCE_SECONDS;
}

function parseFrame(value: unknown): PeerFrame | null {
  if (typeof value !== "object" || value === null) return null;
  const { v, seq, sentAt, nonce, ciphertext } = value as Record<string, unknown>;
  if (!Number.isSafeInteger(v) || (v as number) < 1) return null;
  if (!Number.isSafeInteger(seq) || (seq as number) < 1) return null;
  if (!Number.isSafeInteger(sentAt) || (sentAt as number) < 0) return null;
  if (typeof nonce !== "string" || !AEAD_NONCE.test(nonce)) return null;
  if (typeof ciphertext !== "string" || !CIPHERTEXT.test(ciphertext)) return null;
  return { v: v as number, seq: seq as number, sentAt: sentAt as number, nonce, ciphertext };
}
