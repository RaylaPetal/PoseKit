import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { HEX64, nowSeconds } from "../lib/constants";

/**
 * One per character (addressed by its inbox id, a hash the client computes from Name@World). Knows which device hubs
 * are logged in as that character, so an invite addressed by name reaches them. Anyone can register for any name:
 * that only lets them receive invites, which carry nothing secret; the clients verify who's who by targeting.
 */
export class CharacterInbox extends DurableObject<Env> {
  private readonly sql: SqlStorage;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS devices (device TEXT PRIMARY KEY, registered_at INTEGER NOT NULL) WITHOUT ROWID`);
  }

  async register(device: string): Promise<void> {
    if (!HEX64.test(device)) return;
    this.sql.exec(`INSERT OR REPLACE INTO devices (device, registered_at) VALUES (?, ?)`, device, nowSeconds());
  }

  async unregister(device: string): Promise<void> {
    this.sql.exec(`DELETE FROM devices WHERE device = ?`, device);
  }

  /**
   * Hands an invite to every registered hub with a live connection. A hub that has none is dropped from the list,
   * which replaces a periodic sweep: stale entries are cleaned the first time they're used.
   */
  async invite(sender: string, body: string, sentAt: number): Promise<"delivered" | "offline"> {
    const devices = this.sql.exec<{ device: string }>(`SELECT device FROM devices`).toArray().map((r) => r.device);
    let delivered = false;
    await Promise.all(devices.filter((d) => d !== sender).map(async (device) => {
      const hub = this.env.DEVICE_HUB.get(this.env.DEVICE_HUB.idFromName(device));
      const live = await hub.deliverInvite(sender, body, sentAt).catch(() => false);
      if (live) delivered = true;
      else this.sql.exec(`DELETE FROM devices WHERE device = ?`, device);
    }));
    return delivered ? "delivered" : "offline";
  }
}
