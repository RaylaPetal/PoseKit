import type { Budget } from "./budget/Budget";
import type { DeviceHub } from "./hub/DeviceHub";
import type { CharacterInbox } from "./inbox/CharacterInbox";

export interface Env {
  RELAY_ENVIRONMENT: string;
  /** Live units (socket opens, frames, invites) allowed per UTC day across the whole relay. */
  DAILY_BUDGET: string;
  /** 1 in N live units is charged to the Budget object (×N), so most frames cost no extra request. */
  BUDGET_SAMPLING: string;
  DEVICE_HUB: DurableObjectNamespace<DeviceHub>;
  CHARACTER_INBOX: DurableObjectNamespace<CharacterInbox>;
  BUDGET: DurableObjectNamespace<Budget>;
}
