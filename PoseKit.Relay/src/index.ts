import type { Env } from "./env";
import { HEX64, PROTOCOL_VERSION } from "./lib/constants";

export { Budget } from "./budget/Budget";
export { DeviceHub } from "./hub/DeviceHub";
export { CharacterInbox } from "./inbox/CharacterInbox";

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/" && request.method === "GET") {
      return Response.json({ service: "posekit-relay", protocol: PROTOCOL_VERSION, environment: env.RELAY_ENVIRONMENT });
    }

    if (url.pathname === "/v1/socket" && request.method === "GET") {
      if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return new Response("expected websocket", { status: 426 });
      // The device parameter only routes the upgrade; the hub accepts nothing until the client proves the key.
      const device = url.searchParams.get("device");
      if (!device || !HEX64.test(device)) return new Response("bad device", { status: 400 });
      const headers = new Headers(request.headers);
      headers.set("x-hub-device", device);
      const hub = env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(device));
      return hub.fetch(new Request(request.url, { method: "GET", headers }));
    }

    return new Response("not found", { status: 404 });
  },
} satisfies ExportedHandler<Env>;
