import { describe, expect, it } from "vitest";
import vectors from "../protocol/vectors.json";
import { socketAuthBaseString } from "../src/lib/constants";
import { base64UrlToBytes, deviceKeyIdFor, sha256Hex } from "../src/lib/crypto";

const enc = new TextEncoder();
const hex = (bytes: Uint8Array) => [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
const fromHex = (value: string) => new Uint8Array(value.match(/../g)!.map((h) => parseInt(h, 16)));

describe("protocol vectors", () => {
  it("device key ids hash the canonical public key", async () => {
    for (const key of vectors.deviceKeys) {
      expect(await deviceKeyIdFor({ kty: "EC", crv: "P-256", x: key.x, y: key.y })).toBe(key.deviceKeyId);
    }
  });

  it("socket auth base string", () => {
    expect(socketAuthBaseString(vectors.socketAuth.deviceKeyId, vectors.socketAuth.nonce)).toBe(vectors.socketAuth.baseString);
  });

  it("inbox ids", async () => {
    for (const inbox of vectors.inboxIds) expect(await sha256Hex(`posekit-inbox-v1|${inbox.character}`)).toBe(inbox.inboxId);
  });

  it("ECDH, HKDF and AES-GCM peer message", async () => {
    const [low, high] = vectors.deviceKeys as [typeof vectors.deviceKeys[0], typeof vectors.deviceKeys[0]];
    const priv = await crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: low.x, y: low.y, d: low.d },
      { name: "ECDH", namedCurve: "P-256" }, false, ["deriveBits"]);
    const pub = await crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: high.x, y: high.y },
      { name: "ECDH", namedCurve: "P-256" }, false, []);
    const shared = new Uint8Array(await crypto.subtle.deriveBits(
      // The runtime takes the standard `public` field; workers-types spells it `$public`.
      { name: "ECDH", public: pub } as unknown as SubtleCryptoDeriveKeyAlgorithm, priv, 256));
    const m = vectors.peerMessage;
    expect(hex(shared)).toBe(m.sharedSecretHex);

    const salt = new Uint8Array([...base64UrlToBytes(m.nonceLow), ...base64UrlToBytes(m.nonceHigh)]);
    const hkdf = await crypto.subtle.importKey("raw", shared, "HKDF", false, ["deriveBits"]);
    const key = new Uint8Array(await crypto.subtle.deriveBits(
      { name: "HKDF", hash: "SHA-256", salt, info: enc.encode("posekit-relay-message-v1") }, hkdf, 256));
    expect(hex(key)).toBe(m.keyHex);

    expect(m.aad).toBe(`posekit-frame-v1\n${m.fromDeviceKeyId}\n${m.toDeviceKeyId}\n${m.seq}\n${m.sentAt}`);
    const aes = await crypto.subtle.importKey("raw", fromHex(m.keyHex), "AES-GCM", false, ["decrypt"]);
    const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv: base64UrlToBytes(m.iv), additionalData: enc.encode(m.aad) },
      aes, base64UrlToBytes(m.ciphertext));
    expect(new TextDecoder().decode(plain)).toBe(m.plaintext);
  });
});
