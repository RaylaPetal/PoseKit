import type { Env } from "../src/env";

declare module "cloudflare:test" {
  // eslint-disable-next-line @typescript-eslint/no-empty-object-type -- the pool's documented way to type `env`
  interface ProvidedEnv extends Env {}
}
