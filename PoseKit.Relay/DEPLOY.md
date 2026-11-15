# Deploying the PoseKit relay

The relay is one Cloudflare Worker, `posekit-relay`, with three Durable Object classes (`DeviceHub`, `CharacterInbox`,
`Budget`) and no D1 or R2. Released plugin builds connect to the fixed address in `PoseKit/Relay/RelaySocket.cs`
(`RelayOrigin`): `https://posekit-relay.oathbound.workers.dev`. Players can't change it.

It runs on the same Cloudflare account as Oathbound's relay but is a separate Worker: deploying or breaking one never
touches the other. There is no separate testing relay; try changes with `npm test` and `npm run dev` first.

Run everything from `PoseKit.Relay/`:

```bash
npm install
```

## 1. Log in

```bash
npx wrangler login
npx wrangler whoami
```

`whoami` should show the account Oathbound's relay is on.

## 2. Check

```bash
npm run typecheck
npm run lint
npm test
```

`npm test` runs the relay in the local Workers runtime, including `test/vectors.spec.ts`, which checks the shared
protocol vectors the C# client is also checked against.

## 3. Deploy

```bash
npm run deploy
```

It prints `https://posekit-relay.oathbound.workers.dev` at the end. A new Durable Object class needs a new
`[[migrations]]` entry in `wrangler.toml`; never edit or remove an existing one.

## 4. Watch it

```bash
npx wrangler tail
```

or Cloudflare dashboard → Workers & Pages → `posekit-relay` → Logs. Logs are sampled at 10%.

## Limits

`wrangler.toml` sets `DAILY_BUDGET` (live units per UTC day across the whole relay: socket opens, frames, invites) and
`BUDGET_SAMPLING` (1 in N units is charged). Per-device limits (20 frames per 10 s, 600 per hour) are in
`protocol/constants.json`. Over a limit, clients show "slow down" or "pairing service busy" instead of failing silently.

## Protocol vectors

`protocol/vectors.json` is generated once by `node scripts/make-vectors.mjs` and committed. Re-running it makes new
keys and nonces; only do that when the protocol changes, and update the C# check with it.
