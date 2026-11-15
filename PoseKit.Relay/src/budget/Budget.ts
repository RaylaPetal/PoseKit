import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { utcDay } from "../lib/constants";

/**
 * The relay's one daily cap on live work (socket opens, frames, invites). Hubs charge it by sampling, so it sees about
 * 1 in N units. It stops a bug or a flood from running up the account's bill; it is not a cost plan.
 */
export class Budget extends DurableObject<Env> {
  private readonly sql: SqlStorage;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS days (day TEXT PRIMARY KEY, used INTEGER NOT NULL) WITHOUT ROWID`);
  }

  /** Adds `units` to today's total; false once today's total is over the cap. */
  async charge(units: number): Promise<boolean> {
    const day = utcDay();
    this.sql.exec(`INSERT INTO days (day, used) VALUES (?, ?) ON CONFLICT (day) DO UPDATE SET used = used + excluded.used`, day, units);
    this.sql.exec(`DELETE FROM days WHERE day < ?`, day);
    const used = this.sql.exec<{ used: number }>(`SELECT used FROM days WHERE day = ?`, day).one().used;
    return used <= Number(this.env.DAILY_BUDGET);
  }
}

/** Charges one live unit, sampling 1 in N. Returns false when the day's cap is spent. */
export class BudgetMeter {
  private overDay: string | null = null;

  constructor(private readonly env: Env, private readonly random: () => number = Math.random) {}

  async charge(): Promise<boolean> {
    const day = utcDay();
    const sampling = Math.max(1, Number(this.env.BUDGET_SAMPLING) || 1);
    if (this.random() < 1 / sampling) {
      const ok = await this.env.BUDGET.get(this.env.BUDGET.idFromName("global")).charge(sampling);
      this.overDay = ok ? null : day;
      return ok;
    }
    return this.overDay !== day;
  }
}
