import { SELF } from "cloudflare:test";
import { bytesToBase64Url, deviceKeyIdFor, type EcPublicKeyJwk, sha256Hex } from "../src/lib/crypto";
import { socketAuthBaseString } from "../src/lib/constants";

/** A test-side live connection that queues every message it receives. */
export class TestSocket {
  private readonly queue: Record<string, unknown>[] = [];
  private waiters: ((m: Record<string, unknown>) => void)[] = [];
  closed: { code: number; reason: string } | null = null;

  constructor(readonly ws: WebSocket) {
    ws.addEventListener("message", (event) => {
      let message: Record<string, unknown>;
      try { message = JSON.parse(event.data as string) as Record<string, unknown>; } catch { message = { raw: event.data }; }
      const waiter = this.waiters.shift();
      if (waiter) waiter(message);
      else this.queue.push(message);
    });
    ws.addEventListener("close", (event) => { this.closed = { code: event.code, reason: event.reason }; });
  }

  next(timeoutMs = 2000): Promise<Record<string, unknown>> {
    const queued = this.queue.shift();
    if (queued) return Promise.resolve(queued);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((w) => w !== done);
        reject(new Error("no message"));
      }, timeoutMs);
      const done = (m: Record<string, unknown>) => { clearTimeout(timer); resolve(m); };
      this.waiters.push(done);
    });
  }

  async maybeNext(ms = 300): Promise<Record<string, unknown> | null> {
    try { return await this.next(ms); } catch { return null; }
  }

  send(message: unknown): void {
    this.ws.send(typeof message === "string" ? message : JSON.stringify(message));
  }

  close(): void {
    try { this.ws.close(1000, "done"); } catch { /* already closed */ }
  }
}

export interface Device {
  id: string;
  publicKey: EcPublicKeyJwk;
  privateKey: CryptoKey;
}

export async function newDevice(): Promise<Device> {
  const pair = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const jwk = (await crypto.subtle.exportKey("jwk", pair.publicKey)) as JsonWebKey;
  const publicKey: EcPublicKeyJwk = { kty: "EC", crv: "P-256", x: jwk.x!, y: jwk.y! };
  return { id: await deviceKeyIdFor(publicKey), publicKey, privateKey: pair.privateKey };
}

export async function sign(device: Device, message: string): Promise<string> {
  const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, device.privateKey, new TextEncoder().encode(message));
  return bytesToBase64Url(new Uint8Array(sig));
}

export async function openRaw(deviceId: string): Promise<TestSocket> {
  const response = await SELF.fetch(`https://relay.test/v1/socket?device=${deviceId}`, { headers: { Upgrade: "websocket" } });
  if (!response.webSocket) throw new Error(`no websocket (${response.status})`);
  response.webSocket.accept();
  return new TestSocket(response.webSocket);
}

/** Opens and authenticates a device's socket; returns it after auth-ok. */
export async function connect(device: Device): Promise<TestSocket> {
  const socket = await openRaw(device.id);
  const challenge = await socket.next();
  socket.send({ type: "auth", publicKey: device.publicKey, signature: await sign(device, socketAuthBaseString(device.id, challenge.nonce as string)) });
  const ok = await socket.next();
  if (ok.type !== "auth-ok") throw new Error(`auth failed: ${JSON.stringify(ok)}`);
  return socket;
}

export function inboxIdFor(nameAtWorld: string): Promise<string> {
  return sha256Hex(`posekit-inbox-v1|${nameAtWorld}`);
}

export function frame(seq = 1, sentAt = Math.floor(Date.now() / 1000)) {
  return { v: 1, seq, sentAt, nonce: "AAAAAAAAAAAAAAAA", ciphertext: "QUFBQUFBQUFBQUFBQUFBQUFBQUFB" };
}

let counter = 0;
export function messageId(): string {
  return `m${++counter}`;
}
