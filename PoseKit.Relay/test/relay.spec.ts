import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { socketAuthBaseString } from "../src/lib/constants";
import { connect, frame, inboxIdFor, messageId, newDevice, openRaw, sign } from "./helpers";

async function ack(socket: Awaited<ReturnType<typeof connect>>, id: string) {
  for (;;) {
    const m = await socket.next();
    if (m.type === "ack" && m.id === id) return m.result;
  }
}

describe("health", () => {
  it("reports the service", async () => {
    const response = await SELF.fetch("https://relay.test/");
    expect(await response.json()).toMatchObject({ service: "posekit-relay", protocol: 1 });
  });
});

describe("socket auth", () => {
  it("accepts a client that signs with the key its id names", async () => {
    const device = await newDevice();
    const socket = await connect(device);
    socket.close();
  });

  it("rejects a key that doesn't hash to the requested id", async () => {
    const owner = await newDevice();
    const impostor = await newDevice();
    const socket = await openRaw(owner.id);
    const challenge = await socket.next();
    socket.send({ type: "auth", publicKey: impostor.publicKey, signature: await sign(impostor, socketAuthBaseString(owner.id, challenge.nonce as string)) });
    expect(await socket.next()).toMatchObject({ type: "error", code: "unauthorized" });
  });

  it("rejects a bad signature", async () => {
    const device = await newDevice();
    const socket = await openRaw(device.id);
    await socket.next();
    socket.send({ type: "auth", publicKey: device.publicKey, signature: await sign(device, "something else") });
    expect(await socket.next()).toMatchObject({ type: "error", code: "unauthorized" });
  });

  it("refuses to send before auth", async () => {
    const device = await newDevice();
    const socket = await openRaw(device.id);
    await socket.next();
    socket.send({ type: "send", id: "x", to: device.id, frame: frame() });
    expect(await socket.next()).toMatchObject({ type: "error", code: "unauthorized" });
  });
});

describe("frames", () => {
  it("delivers to a connected device", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    const sb = await connect(b);
    const id = messageId();
    sa.send({ type: "send", id, to: b.id, frame: frame() });
    expect(await ack(sa, id)).toBe("delivered");
    expect(await sb.next()).toMatchObject({ type: "frame", from: a.id, seq: 1 });
  });

  it("reports offline for a device that never connected", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    const id = messageId();
    sa.send({ type: "send", id, to: b.id, frame: frame() });
    expect(await ack(sa, id)).toBe("offline");
  });

  it("holds a frame for a device that just dropped and flushes it on reconnect", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    const sb = await connect(b);
    sb.close();
    await new Promise((r) => setTimeout(r, 50));

    const id = messageId();
    sa.send({ type: "send", id, to: b.id, frame: frame() });
    expect(await ack(sa, id)).toBe("held");

    const again = await connect(b);
    expect(await again.next()).toMatchObject({ type: "frame", from: a.id });
    expect(await ack(sa, id)).toBe("delivered");
  });

  it("refuses a stale or malformed frame", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    await connect(b);
    const stale = messageId();
    sa.send({ type: "send", id: stale, to: b.id, frame: frame(1, Math.floor(Date.now() / 1000) - 3600) });
    expect(await ack(sa, stale)).toBe("refused");
    const bad = messageId();
    sa.send({ type: "send", id: bad, to: b.id, frame: { ...frame(), nonce: "short" } });
    expect(await ack(sa, bad)).toBe("refused");
  });
});

describe("character inboxes", () => {
  it("delivers an invite to the device registered for a character", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    const sb = await connect(b);
    const inbox = await inboxIdFor("Bob Example@Lich");
    sb.send({ type: "register", inbox });
    expect(await sb.next()).toMatchObject({ type: "registered", inbox });

    const id = messageId();
    sa.send({ type: "invite", id, inbox, body: "aW52aXRl", sentAt: Math.floor(Date.now() / 1000) });
    expect(await ack(sa, id)).toBe("delivered");
    expect(await sb.next()).toMatchObject({ type: "invite", from: a.id, body: "aW52aXRl" });
  });

  it("reports offline when nobody is registered", async () => {
    const a = await newDevice();
    const sa = await connect(a);
    const id = messageId();
    sa.send({ type: "invite", id, inbox: await inboxIdFor("Nobody Here@Lich"), body: "eA", sentAt: Math.floor(Date.now() / 1000) });
    expect(await ack(sa, id)).toBe("offline");
  });

  it("forgets a device once it disconnects", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    const sb = await connect(b);
    const inbox = await inboxIdFor("Bob Example@Lich");
    sb.send({ type: "register", inbox });
    await sb.next();
    sb.close();
    await new Promise((r) => setTimeout(r, 50));

    const id = messageId();
    sa.send({ type: "invite", id, inbox, body: "eA", sentAt: Math.floor(Date.now() / 1000) });
    expect(await ack(sa, id)).toBe("offline");
  });
});

describe("limits", () => {
  it("refuses a burst over the per-device limit", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    await connect(b);
    const results: unknown[] = [];
    for (let i = 1; i <= 21; i++) {
      const id = messageId();
      sa.send({ type: "send", id, to: b.id, frame: frame(i) });
      results.push(await ack(sa, id));
    }
    expect(results.slice(0, 20).every((r) => r === "delivered")).toBe(true);
    expect(results[20]).toBe("over_limit");
  });

  it("refuses frames once the daily cap is spent", async () => {
    const a = await newDevice();
    const b = await newDevice();
    const sa = await connect(a);
    await connect(b);
    await env.BUDGET.get(env.BUDGET.idFromName("global")).charge(Number(env.DAILY_BUDGET));
    const id = messageId();
    sa.send({ type: "send", id, to: b.id, frame: frame() });
    expect(await ack(sa, id)).toBe("over_budget");
  });
});
