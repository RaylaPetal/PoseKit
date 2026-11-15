import protocol from "../../protocol/constants.json";

export const PROTOCOL_VERSION = protocol.protocolVersion;
export const SOCKET_AUTH_TIMEOUT_SECONDS = protocol.socketAuth.timeoutSeconds;
export const FRAME_CIPHERTEXT_MAX_CHARS = protocol.limits.frameCiphertextMaxChars;
export const INVITE_BODY_MAX_CHARS = protocol.limits.inviteBodyMaxChars;
export const HELD_FRAMES_PER_SENDER = protocol.limits.heldFramesPerSender;
export const HOLD_SECONDS = protocol.limits.holdSeconds;
export const TIMESTAMP_TOLERANCE_SECONDS = protocol.limits.timestampToleranceSeconds;
export const DEVICE_BURST_FRAMES = protocol.limits.deviceBurstFrames;
export const DEVICE_BURST_WINDOW_SECONDS = protocol.limits.deviceBurstWindowSeconds;
export const DEVICE_HOURLY_FRAMES = protocol.limits.deviceHourlyFrames;

/** Largest client message the hub parses: a frame plus its envelope. */
export const MAX_CLIENT_MESSAGE_CHARS = FRAME_CIPHERTEXT_MAX_CHARS + 1024;

export const HEX64 = /^[0-9a-f]{64}$/;

export function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

/** The UTC day a budget charge counts towards. */
export function utcDay(nowMs = Date.now()): string {
  return new Date(nowMs).toISOString().slice(0, 10);
}

export function socketAuthBaseString(deviceKeyId: string, nonce: string): string {
  return protocol.socketAuth.baseString.replace("{deviceKeyId}", deviceKeyId).replace("{nonce}", nonce);
}
