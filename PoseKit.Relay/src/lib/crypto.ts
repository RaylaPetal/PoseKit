export interface EcPublicKeyJwk {
  kty: "EC";
  crv: "P-256";
  x: string;
  y: string;
}

const COORD = /^[A-Za-z0-9_-]{43}$/;
const SIGNATURE = /^[A-Za-z0-9_-]{86}$/;

export function parsePublicKey(value: unknown): EcPublicKeyJwk | null {
  if (typeof value !== "object" || value === null) return null;
  const { kty, crv, x, y } = value as Record<string, unknown>;
  if (kty !== "EC" || crv !== "P-256" || typeof x !== "string" || typeof y !== "string") return null;
  if (!COORD.test(x) || !COORD.test(y)) return null;
  return { kty, crv, x, y };
}

/** protocol/constants.json `deviceKeyId`: SHA-256 of the key's canonical JSON, so the id itself names the key. */
export async function deviceKeyIdFor(key: EcPublicKeyJwk): Promise<string> {
  return sha256Hex(`{"crv":"P-256","kty":"EC","x":"${key.x}","y":"${key.y}"}`);
}

export async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Raw ECDSA P-256/SHA-256 signature (r||s), base64url. */
export async function verifySignature(key: EcPublicKeyJwk, signature: string, message: string): Promise<boolean> {
  if (!SIGNATURE.test(signature)) return false;
  let imported: CryptoKey;
  try {
    imported = await crypto.subtle.importKey("jwk", { ...key, ext: true, key_ops: ["verify"] },
      { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  } catch {
    return false;
  }
  const bytes = base64UrlToBytes(signature);
  if (bytes.byteLength !== 64) return false;
  return crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, imported, bytes, new TextEncoder().encode(message));
}

export function base64UrlToBytes(value: string): Uint8Array {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

export function bytesToBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function randomBase64Url(bytes: number): string {
  return bytesToBase64Url(crypto.getRandomValues(new Uint8Array(bytes)));
}
