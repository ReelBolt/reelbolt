/**
 * The streamable-HTTP transport, plus the two OAuth resource-server answers the
 * contract adds on top of it (docs/design/mcp-oauth.md §7):
 *
 *   - `GET /.well-known/oauth-protected-resource` — the RFC 9728 document, served
 *     unauthenticated, because a client has no token yet when it fetches it.
 *   - an **HTTP 401 + `WWW-Authenticate`** for a request with no bearer token, and for
 *     one whose token the Inference API rejected with 401. Every other non-2xx from the
 *     API keeps today's behaviour (a JSON-RPC error, HTTP 200).
 *
 * That second point is a deliberate amendment to `mcp-tool-bridge.md` §5, which said a
 * 401 from the API is surfaced as a JSON-RPC error. Nothing else about the bridge
 * changes: the token is still forwarded verbatim and never parsed or verified (§9), and
 * `MCP_TRANSPORT=stdio` is untouched (see server.ts).
 */
import * as http from "node:http";
import { randomUUID } from "node:crypto";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { isInitializeRequest } from "@modelcontextprotocol/sdk/types.js";
import type { ReelBoltApiClient } from "./apiClient.js";
import { makeProxyServer, readBearerToken } from "./bridge.js";
import { principalOf } from "./runSubscriptions.js";
import {
  PROTECTED_RESOURCE_METADATA_PATH,
  bearerChallengeValue,
  buildProtectedResourceMetadata,
  createApiAuthFailureState,
  protectedResourceMetadataUrl,
  runInAuthFailureScope,
  withAuthFailureTracking,
  type ApiAuthFailureState,
  type OAuthResourceConfig,
} from "./oauth.js";

/** The one body this layer ever writes itself. */
export const AUTH_FAILURE_BODY = JSON.stringify({
  error: "invalid_token",
  error_description:
    "The ReelBolt access token was rejected. Fetch the protected-resource metadata to " +
    "discover the authorization server, then retry with a bearer token.",
});

export interface HttpTransportOptions {
  apiClient: ReelBoltApiClient;
  /** Both public URLs, read from the environment by server.ts. Never hardcoded. */
  oauth: OAuthResourceConfig;
  /** Test seams for the idle reaper. */
  now?: () => number;
  idleMs?: number;
  /** Open sessions in the whole process. Default `MCP_MAX_SESSIONS`, else 2000. */
  maxSessions?: number;
  /** Open sessions per client address. Default `MCP_MAX_SESSIONS_PER_IP`, else 200. */
  maxSessionsPerIp?: number;
  /** Largest request body buffered, in bytes. Default `MCP_MAX_BODY_BYTES`, else 1 MiB. */
  maxBodyBytes?: number;
}

function positiveEnv(name: string, fallback: number): number {
  const n = Number(process.env[name]);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

/**
 * The key the per-address session cap counts on: the address nginx saw (`X-Real-IP`, which
 * nginx overwrites on every proxied request), else the socket peer. An IPv4 address is its
 * own key and an IPv6 address is keyed on its /64, as nginx's per-address zones are.
 *
 * A caller that reaches this port without nginx can send any `X-Real-IP`; the process-wide
 * cap does not depend on this value.
 */
export function clientAddressKey(req: http.IncomingMessage): string {
  const header = req.headers["x-real-ip"];
  const fromHeader = (Array.isArray(header) ? header[0] : header)?.trim();
  let address = (fromHeader || req.socket.remoteAddress || "unknown").toLowerCase();
  if (address.startsWith("::ffff:") && address.includes(".")) {
    address = address.slice("::ffff:".length);
  }
  if (!address.includes(":")) {
    return address;
  }
  // Expand `::` so the first four groups (the /64) can be read off.
  const [head, tail] = address.split("::");
  const headGroups = head ? head.split(":") : [];
  const tailGroups = tail ? tail.split(":") : [];
  const groups =
    tail === undefined
      ? headGroups
      : [...headGroups, ...Array(Math.max(0, 8 - headGroups.length - tailGroups.length)).fill("0"), ...tailGroups];
  return groups
    .slice(0, 4)
    .map((group) => group.replace(/^0+(?=.)/, ""))
    .join(":") + "::/64";
}

function sendJsonRpcError(res: http.ServerResponse, status: number, message: string): void {
  sendJson(res, status, JSON.stringify({ jsonrpc: "2.0", error: { code: -32000, message }, id: null }));
}

function sendJson(res: http.ServerResponse, status: number, body: string): void {
  res.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body),
  });
  res.end(body);
}

/** The 401 the contract pins: status + `WWW-Authenticate`, and nothing parsed as JSON-RPC. */
function sendUnauthorized(
  res: http.ServerResponse,
  challenge: string,
  body: string | undefined
): void {
  res.writeHead(401, {
    "Content-Type": "application/json",
    "Content-Length": body === undefined ? 0 : Buffer.byteLength(body),
    "WWW-Authenticate": challenge,
  });
  res.end(body ?? "");
}

/**
 * Does this request body ask the Inference API for something?
 *
 * `tools/list` and `tools/call` are the only two methods the bridge proxies (they are the
 * only ones that reach the API at all), so they are the only requests whose response head
 * has to wait for a verdict. `initialize`, `ping`, `notifications/*` and the standalone
 * GET stream are answered by the SDK alone and are left exactly as they were.
 */
export function expectsApiCall(body: unknown): boolean {
  const messages = Array.isArray(body) ? body : [body];
  return messages.some((message) => {
    if (message === null || typeof message !== "object") {
      return false;
    }
    const method = (message as { method?: unknown }).method;
    return method === "tools/list" || method === "tools/call";
  });
}

/**
 * Is this request URI the protected-resource metadata?
 *
 * Two spellings, both of which must answer, and both of which return the same document:
 *
 *   - `/.well-known/oauth-protected-resource` — the path this server and nginx publish,
 *     and the one the `WWW-Authenticate` challenge advertises;
 *   - `/.well-known/oauth-protected-resource/<path…>` — the URL RFC 9728 §3 builds for a
 *     resource that itself carries a path: the well-known string is inserted between the
 *     host and the resource's path components, so a resource of `https://host/mcp` has a
 *     metadata URL of `https://host/.well-known/oauth-protected-resource/mcp`. A strict
 *     client that ignores the `resource_metadata` link in the challenge derives that URL
 *     for itself, and 404ing it would leave that client unable to start a login at all.
 *
 * This deployment publishes exactly ONE resource, so every suffix yields the same
 * document, whose `resource` field names the resource either way.
 */
export function isProtectedResourceMetadataPath(path: string): boolean {
  return (
    path === PROTECTED_RESOURCE_METADATA_PATH ||
    path.startsWith(`${PROTECTED_RESOURCE_METADATA_PATH}/`)
  );
}

/**
 * The response gate for one HTTP request: it defers the response head, when it must, so
 * that an API 401 can still become an HTTP 401 challenge.
 *
 * Why deferring is required (this is the whole reason the function is not three lines):
 * for a tool request the SDK transport answers with `200 text/event-stream` immediately
 * and streams the JSON-RPC result into it later — the status is committed before the
 * tool handler has an answer from the API. Rewriting "at writeHead time" therefore wins
 * only a race, and loses it against any API slower than one event-loop turn.
 *
 * So, for a request that expects an API verdict, the first response head and everything
 * written before that verdict are captured instead of being sent. When the verdict
 * arrives the response is released in one of two shapes:
 *
 *   - the API rejected the token → `401` with the `WWW-Authenticate` challenge, and the
 *     transport's own body is dropped: a JSON-RPC error under a 401 is precisely the
 *     message that must not reach a client that needs to start a login;
 *   - anything else → the captured response is replayed byte for byte (status, headers,
 *     chunks), so a success, a JSON-RPC error or an SSE stream is indistinguishable from
 *     the transport writing it directly.
 *
 * A request with no API verdict coming (`initialize`, `ping`, the GET stream) is never
 * held: it takes the pass-through path, with only the 401 rewrite guarded at writeHead.
 */
export function installAuthAwareResponse(
  res: http.ServerResponse,
  state: ApiAuthFailureState,
  challenge: string
): void {
  const originalWriteHead = res.writeHead.bind(res);
  const originalWrite = res.write.bind(res);
  const originalEnd = res.end.bind(res);
  const originalFlushHeaders =
    typeof res.flushHeaders === "function" ? res.flushHeaders.bind(res) : undefined;

  type WriteHead = (...args: unknown[]) => unknown;
  let released = false;
  let finished = false;
  let head: unknown[] | undefined;
  /** An `end` seen while the head was held: nothing more could be decided by then. */
  let heldEnd: unknown[] | undefined;

  const pendingApiCalls = (): number => state.apiCallsStarted - state.apiCallsSettled;
  /**
   * Hold while a verdict is expected and none has arrived. `apiCallsStarted === 0` covers
   * the window before the handler has even reached its API call (the head really is
   * written that early).
   */
  const shouldHold = (): boolean =>
    !released &&
    !finished &&
    state.expectedApiCall &&
    (state.apiCallsStarted === 0 || pendingApiCalls() > 0);

  /** Send the challenge instead of the captured response. Idempotent. */
  function sendChallenge(): void {
    if (finished) {
      return;
    }
    finished = true;
    released = true;
    head = undefined;
    heldEnd = undefined;
    originalWriteHead(401, {
      "Content-Type": "application/json",
      "Content-Length": Buffer.byteLength(AUTH_FAILURE_BODY),
      "WWW-Authenticate": challenge,
    });
    originalEnd(AUTH_FAILURE_BODY);
  }

  /** Send the captured head (and a captured end) exactly as the transport wrote them. */
  function release(): void {
    if (released || finished) {
      return;
    }
    if (state.unauthorized) {
      sendChallenge();
      return;
    }
    released = true;
    if (head) {
      (originalWriteHead as WriteHead)(...head);
    }
    if (heldEnd) {
      finished = true;
      (originalEnd as WriteHead)(...heldEnd);
    }
  }

  // The tracker calls this the moment an API call settles, so a held head is released (or
  // challenged) without waiting for the next write.
  state.onVerdict = () => {
    if (!shouldHold()) {
      release();
    }
  };

  res.writeHead = ((...args: unknown[]) => {
    if (finished) {
      return res;
    }
    if (state.unauthorized) {
      // The verdict is already in: challenge now, whatever the transport wanted to say.
      sendChallenge();
      return res;
    }
    if (shouldHold()) {
      head = args;
      return res;
    }
    return (originalWriteHead as WriteHead)(...args);
  }) as unknown as typeof res.writeHead;

  res.write = ((...args: unknown[]): boolean => {
    if (finished) {
      return true;
    }
    if (state.unauthorized) {
      sendChallenge();
      return true;
    }
    if (released) {
      // A held response was already released; the rest of it streams through normally.
      return (originalWrite as (...a: unknown[]) => boolean)(...args);
    }
    if (shouldHold()) {
      // Data is flowing although no verdict has landed. That is the transport's own SSE
      // keep-alive (15s), which only fires when a tool call is genuinely still running —
      // an API that was going to reject the token did so in milliseconds. So this is a
      // long, accepted call: releasing here keeps bytes flowing to the client and to any
      // proxy in front of it (an unbroken SSE stream is what the transport promises),
      // at the price of a 401 arriving after 15s degrading to a JSON-RPC error.
      release();
      if (finished) {
        return true;
      }
      return (originalWrite as (...a: unknown[]) => boolean)(...args);
    }
    return (originalWrite as (...a: unknown[]) => boolean)(...args);
  }) as unknown as typeof res.write;

  res.end = ((...args: unknown[]): http.ServerResponse => {
    if (finished) {
      return res;
    }
    if (state.unauthorized) {
      sendChallenge();
      return res;
    }
    if (released) {
      finished = true;
      return (originalEnd as WriteHead)(...args) as http.ServerResponse;
    }
    if (shouldHold()) {
      // The response is over: nothing more can be decided, so replay it verbatim.
      heldEnd = args;
      release();
      return res;
    }
    finished = true;
    return (originalEnd as WriteHead)(...args) as http.ServerResponse;
  }) as unknown as typeof res.end;

  if (originalFlushHeaders) {
    res.flushHeaders = ((...args: unknown[]) => {
      if (finished || shouldHold()) {
        // A held head must not be flushed implicitly by Node.
        return;
      }
      (originalFlushHeaders as WriteHead)(...args);
    }) as typeof res.flushHeaders;
  }
}

/**
 * Build the HTTP server (not yet listening, so a test can bind an ephemeral port).
 */
export function createHttpServer(options: HttpTransportOptions): http.Server {
  const challenge = bearerChallengeValue(protectedResourceMetadataUrl(options.oauth));
  const metadataDocument = JSON.stringify(buildProtectedResourceMetadata(options.oauth));

  // Wrapped HERE, not by the caller: the 401 challenge is this transport's behaviour, so
  // "an API 401 marks the request" must not depend on someone remembering to wrap the
  // client at the composition root. Tracking is a no-op outside a request scope.
  const apiClient = withAuthFailureTracking(options.apiClient);

  // One Server + transport PER MCP SESSION, keyed by the id the transport mints at
  // initialize. A single shared stateful transport accepts the first client and then
  // answers every later `initialize` with "Invalid state: Server already initialized"
  // for the whole life of the process, which is wrong for a long-lived container that
  // several clients may reach. Sessions are dropped when their transport closes.
  //
  // The token is still read PER REQUEST from the Authorization header and forwarded
  // verbatim, so no token state lives in the session.
  const sessions = new Map<string, StreamableHTTPServerTransport>();
  // Session -> principal (hash of the initialize request's bearer token), for the per-principal
  // session cap. Only the hash is kept. Concurrent initializes can briefly overshoot the cap.
  const sessionPrincipals = new Map<string, string>();
  const maxSessionsPerPrincipal = (() => {
    const n = Number(process.env.MCP_MAX_SESSIONS_PER_PRINCIPAL);
    return Number.isFinite(n) && n > 0 ? n : 20;
  })();
  // The per-principal cap is keyed on the token's hash and `initialize` never calls the API,
  // so a new made-up token per request walks past it. These two do not depend on the token:
  // one for the whole process (the memory bound) and one per client address (so one address
  // cannot use up the process-wide one). Requests still opening a session hold a slot too,
  // so a burst of concurrent initializes cannot overshoot either cap.
  const maxSessions = options.maxSessions ?? positiveEnv("MCP_MAX_SESSIONS", 2000);
  const maxSessionsPerIp = options.maxSessionsPerIp ?? positiveEnv("MCP_MAX_SESSIONS_PER_IP", 200);
  const maxBodyBytes = options.maxBodyBytes ?? positiveEnv("MCP_MAX_BODY_BYTES", 1024 * 1024);
  const sessionAddresses = new Map<string, string>();
  const openByAddress = new Map<string, number>();
  let openTotal = 0;
  function holdSlot(address: string): void {
    openTotal++;
    openByAddress.set(address, (openByAddress.get(address) ?? 0) + 1);
  }
  function freeSlot(address: string): void {
    openTotal--;
    const left = (openByAddress.get(address) ?? 1) - 1;
    if (left > 0) openByAddress.set(address, left);
    else openByAddress.delete(address);
  }

  // Idle reaper: a client that never sends DELETE would otherwise hold its slot (and a
  // per-principal 429) forever. A session with live run subscriptions is spared; those end
  // by their own TTL, after which the session is reapable.
  const now = options.now ?? Date.now;
  const idleMs =
    options.idleMs ??
    (Number.isFinite(Number(process.env.MCP_SESSION_IDLE_MINUTES)) && Number(process.env.MCP_SESSION_IDLE_MINUTES) > 0
      ? Number(process.env.MCP_SESSION_IDLE_MINUTES)
      : 30) * 60_000;
  const lastActivity = new Map<string, number>();
  const probes = new Map<string, { hasLiveSubscriptions?: () => boolean }>();
  const reaper = setInterval(() => {
    for (const [sid, transport] of [...sessions]) {
      if (now() - (lastActivity.get(sid) ?? 0) < idleMs) continue;
      if (probes.get(sid)?.hasLiveSubscriptions?.()) continue;
      void transport.close().catch(() => undefined);
    }
  }, Math.max(1000, Math.min(idleMs, 60_000)));
  reaper.unref();

  async function createSession(
    principal: string | undefined,
    address: string,
    onRegistered: () => void
  ): Promise<StreamableHTTPServerTransport> {
    // Assigned before any request can initialize, so the closure below always sees it.
    let transport!: StreamableHTTPServerTransport;
    transport = new StreamableHTTPServerTransport({
      sessionIdGenerator: () => randomUUID(),
      onsessioninitialized: (sid) => {
        sessions.set(sid, transport);
        if (principal) sessionPrincipals.set(sid, principal);
        // The slot the opening request held now belongs to the session, until it closes.
        sessionAddresses.set(sid, address);
        onRegistered();
        lastActivity.set(sid, now());
        probes.set(sid, probe);
      },
    });
    const probe: { hasLiveSubscriptions?: () => boolean } = {};
    const server = makeProxyServer({
      probe,
      apiClient,
      getAuthToken: (extra) => readBearerToken(extra?.requestInfo?.headers),
    });
    await server.connect(transport);
    // `connect` installs its own onclose, so chain rather than replace it.
    const connectOnClose = transport.onclose;
    transport.onclose = () => {
      try {
        connectOnClose?.();
      } finally {
        if (transport.sessionId) {
          sessions.delete(transport.sessionId);
          sessionPrincipals.delete(transport.sessionId);
          const held = sessionAddresses.get(transport.sessionId);
          if (held !== undefined) {
            sessionAddresses.delete(transport.sessionId);
            freeSlot(held);
          }
          lastActivity.delete(transport.sessionId);
          probes.delete(transport.sessionId);
        }
      }
    };
    return transport;
  }

  function isInitializeBody(body: unknown): boolean {
    return Array.isArray(body) ? body.some((m) => isInitializeRequest(m)) : isInitializeRequest(body);
  }

  function sendSessionNotFound(res: http.ServerResponse): void {
    res.writeHead(404, { "Content-Type": "application/json" });
    res.end(
      JSON.stringify({ jsonrpc: "2.0", error: { code: -32001, message: "Session not found" }, id: null })
    );
  }

  async function dispatch(
    req: http.IncomingMessage,
    res: http.ServerResponse,
    raw: string
  ): Promise<void> {
    let parsedBody: unknown = undefined;
    if (raw) {
      try {
        parsedBody = JSON.parse(raw);
      } catch {
        // Leave undefined; the transport will surface a parse error.
      }
    }

    const headerSessionId = req.headers["mcp-session-id"];
    const sessionId = Array.isArray(headerSessionId) ? headerSessionId[0] : headerSessionId;

    // The response gate is scoped to this request only, and installed before the
    // transport can write anything. `expectedApiCall` decides whether the head has to be
    // held until the API's verdict lands (see installAuthAwareResponse).
    const state = createApiAuthFailureState(expectsApiCall(parsedBody));
    installAuthAwareResponse(res, state, challenge);

    // Set while this request holds a session slot that no session owns yet.
    let pendingSlot: string | undefined;
    try {
      // A known session id reuses its transport. An unknown id (typically one minted before
      // a restart emptied the Map) is answered 404 "Session not found", the Streamable HTTP
      // signal that makes a compliant client re-initialise; only an `initialize` may open a
      // new session, and never an orphan transport for anything else.
      let transport = sessionId ? sessions.get(sessionId) : undefined;
      if (sessionId && transport) lastActivity.set(sessionId, now());
      if (!transport) {
        if (sessionId && !isInitializeBody(parsedBody)) {
          sendSessionNotFound(res);
          return;
        }
        // Cap open sessions per authenticated principal (known from the initialize request's
        // Authorization header). Nothing is evicted; the caller is told plainly.
        const bearer = readBearerToken(req.headers);
        const principal = bearer ? principalOf(bearer) : undefined;
        if (principal) {
          let open = 0;
          for (const p of sessionPrincipals.values()) if (p === principal) open++;
          if (open >= maxSessionsPerPrincipal) {
            res.writeHead(429, { "Content-Type": "application/json" });
            res.end(
              JSON.stringify({
                jsonrpc: "2.0",
                error: { code: -32000, message: "Too many open MCP sessions for this account. Close an existing session and retry." },
                id: null,
              })
            );
            return;
          }
        }
        // Caps that do not depend on the token (see maxSessions above). Nothing is evicted.
        const address = clientAddressKey(req);
        if ((openByAddress.get(address) ?? 0) >= maxSessionsPerIp) {
          sendJsonRpcError(res, 429, "Too many open MCP sessions from this address. Close an existing session and retry.");
          return;
        }
        if (openTotal >= maxSessions) {
          res.setHeader("Retry-After", "60");
          sendJsonRpcError(res, 503, "The MCP server has no free session. Retry later.");
          return;
        }
        holdSlot(address);
        pendingSlot = address;
        transport = await createSession(principal, address, () => {
          pendingSlot = undefined;
        });
      }
      await runInAuthFailureScope(state, () => transport.handleRequest(req, res, parsedBody));
    } catch (err) {
      console.error("HTTP handler error:", err);
      if (!res.headersSent) {
        res.writeHead(500, { "Content-Type": "application/json" });
      }
      res.end(JSON.stringify({ error: "Internal server error" }));
    } finally {
      // The request ended without opening a session (a refused or malformed initialize).
      if (pendingSlot !== undefined) freeSlot(pendingSlot);
    }
  }

  const httpServer = http.createServer((req, res) => {
    // The request URI arrives unchanged from nginx, so this is the public path verbatim
    // (one trailing slash tolerated).
    const path = (req.url ?? "/").split("?")[0];

    // RFC 9728 metadata. Unauthenticated by design: it is what a client fetches when it has
    // no token yet, so it can discover the authorization server at all. Both the root path
    // and the path-suffixed form a strict client derives are served (see
    // isProtectedResourceMetadataPath).
    if (req.method === "GET" && isProtectedResourceMetadataPath(path)) {
      sendJson(res, 200, metadataDocument);
      return;
    }

    // Unauthenticated health check for the container healthcheck.
    if (req.method === "GET" && path === "/health") {
      sendJson(res, 200, JSON.stringify({ status: "ok" }));
      return;
    }

    // Contract §7 (amending mcp-tool-bridge.md §5): asked to do anything with no bearer
    // token at all, the answer is the challenge, never a JSON-RPC error. No session and
    // no transport is created for such a request.
    if (!readBearerToken(req.headers)) {
      sendUnauthorized(res, challenge, undefined);
      return;
    }

    // Body cap: a bearer value is all it takes to get here, so the body is never buffered
    // past the cap. A declared length over it is refused before a byte is read; a body with
    // no declared length is refused as soon as it crosses it. What is left of an oversized
    // body is discarded (Node drains it), not kept.
    const tooLarge = (): void =>
      sendJsonRpcError(res, 413, `Request body too large (limit ${maxBodyBytes} bytes).`);
    if (Number(req.headers["content-length"]) > maxBodyBytes) {
      tooLarge();
      return;
    }
    const chunks: Buffer[] = [];
    let received = 0;
    let refused = false;
    req.on("data", (chunk: Buffer) => {
      if (refused) return;
      received += chunk.length;
      if (received > maxBodyBytes) {
        refused = true;
        chunks.length = 0;
        tooLarge();
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => {
      if (refused) return;
      void dispatch(req, res, Buffer.concat(chunks).toString());
    });
    req.on("error", (err) => {
      console.error("Request error:", err);
      if (!res.headersSent) {
        res.writeHead(400, { "Content-Type": "application/json" });
      }
      res.end(JSON.stringify({ error: "Bad request" }));
    });
  });
  httpServer.on("close", () => clearInterval(reaper));
  return httpServer;
}
