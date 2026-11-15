import { defineWorkersConfig } from "@cloudflare/vitest-pool-workers/config";

export default defineWorkersConfig({
  test: {
    poolOptions: {
      workers: {
        // Each test gets fresh Durable Object storage.
        isolatedStorage: true,
        wrangler: { configPath: "./wrangler.toml", environment: "local" },
      },
    },
  },
});
