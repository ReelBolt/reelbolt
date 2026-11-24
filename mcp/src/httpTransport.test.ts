/**
 * End-to-end tests of the HTTP transport's two OAuth answers, over a real socket and
 * with a stub Inference API client (no live API, no network beyond 127.0.0.1).
 *
 * What only a real HTTP server can prove, and this is why these are integration tests:
 * the challenge is written by the SDK transport, not by this code, so "an API 401
 * becomes an HTTP 401 rather than a JSON-RPC error" is a claim about the bytes on the
 * wire after the SDK has had its turn.
 */
import { strict as assert } from "node:assert";
import * as http from "node:http";
import { mock, test } from "node:test";
import { ReelBoltApiError, type CallResult, type ReelBoltApiClient } from "./apiClient.js";
import {
  AUTH_FAILURE_BODY,
  clientAddressKey,
  createHttpServer,
  expectsApiCall,
  installAuthAwareResponse,
  isProtectedResourceMetadataPath,
} from "./httpTransport.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import type { OAuthResourceConfig } from "./oauth.js";
import { PROTECTED_RESOURCE_METADATA_PATH, createApiAuthFailureState } from "./oauth.js";

const OAUTH_CONFIG: OAuthResourceConfig = {
  resourceUrl: "https://reelbolt.example.com/mcp",
  authorizationServerUrl: "https://reelbolt.example.com",
};

const CHALLENGE =
  'Bearer resource_metadata="https://reelbolt.example.com/.well-known/oauth-protected-resource", ' +
  'scope="assistant.tools"';

const INITIALIZE = {
  jsonrpc: "2.0",
  id: 1,
  method: "initialize",
  params: {
    protocolVersion: "2025-06-18",
    capabilities: {},
    clientInfo: { name: "reelbolt-test", version: "0.0.0" },
  },
};

const LIST_TOOLS = { jsonrpc: "2.0", id: 2, method: "tools/list" };

/* ---------------------------------------------------------------------------
 * A stub Inference API client whose verdict the test switches at will
 * ------------------------------------------------------------------------- */

type Behavior = "ok" | "unauthorized" | "server-error";

interface StubApi {
  client: ReelBoltApiClient;
  setBehavior(behavior: Behavior): void;
  /** Reject calls carrying exactly this token, leaving every other token alone. */
  setTokenVerdict(token: string, behavior: Behavior): void;
  /** The token forwarded verbatim on the last call (contract §9: forwarded, never verified). */
  tokens: (string | undefined)[];
}

function makeStubApiClient(): StubApi {
  let behavior: Behavior = "ok";
  const tokenVerdicts = new Map<string, Behavior>();
  const tokens: (string | undefined)[] = [];
  const verdictFor = (token: string | undefined): Behavior =>
    (token === undefined ? undefined : tokenVerdicts.get(token)) ?? behavior;
  /**
   * A real API answers after a network round trip, not in the same microtask turn as the
   * request. That delay is load-bearing for these tests: the SDK transport commits
   * `200 text/event-stream` before the tool handler has its answer, so a stub that throws
   * synchronously would let a response-head rewrite pass that a real deployment fails.
   * (This is not hypothetical — it is how this bug was found.)
   */
  const fail = async (endpoint: string, verdict: Behavior): Promise<never> => {
    await new Promise((resolve) => setTimeout(resolve, 25));
    if (verdict === "unauthorized") {
      throw new ReelBoltApiError(`Inference API returned 401 Unauthorized for ${endpoint}`, 401);
    }
    throw new ReelBoltApiError(`Inference API returned 500 Internal Server Error for ${endpoint}`, 500);
  };
  return {
    tokens,
    setBehavior(next) {
      behavior = next;
    },
    setTokenVerdict(token, next) {
      tokenVerdicts.set(token, next);
    },
    client: {
      async catalog(token) {
        tokens.push(token);
        const verdict = verdictFor(token);
        if (verdict !== "ok") await fail("tool-catalog", verdict);
        return {
          tools: [
            {
              name: "ListProjects",
              description: "List the caller's projects",
              inputSchema: { type: "object", properties: {} },
            },
          ],
        };
      },
      async callTool(_name, _arguments, _context, token): Promise<CallResult> {
        tokens.push(token);
        const verdict = verdictFor(token);
        if (verdict !== "ok") await fail("tool-call", verdict);
        return { content: [{ type: "text", text: "ok" }], isError: false };
      },
    },
  };
}

/* ---------------------------------------------------------------------------
 * Server + client helpers
 * ------------------------------------------------------------------------- */

interface RunningServer {
  baseUrl: string;
  close: () => Promise<void>;
}

async function startServer(
  apiClient: ReelBoltApiClient,
  oauth: OAuthResourceConfig = OAUTH_CONFIG,
  limits: { maxSessions?: number; maxSessionsPerIp?: number; maxBodyBytes?: number } = {}
): Promise<RunningServer> {
  const server = createHttpServer({ apiClient, oauth, ...limits });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object", "expected a bound TCP address");
  return {
    baseUrl: `http://127.0.0.1:${address.port}`,
    close: async () => {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

type AnyMessage = Record<string, unknown>;

/** Parse an MCP POST response: SSE frames when streamed, a bare JSON body otherwise. */
function parseMessages(text: string): AnyMessage[] {
  const messages: AnyMessage[] = [];
  for (const line of text.split("\n")) {
    const trimmed = line.trim();
    if (trimmed.startsWith("data:")) {
      messages.push(JSON.parse(trimmed.slice("data:".length).trim()) as AnyMessage);
    }
  }
  if (messages.length === 0 && text.trim().startsWith("{")) {
    messages.push(JSON.parse(text.trim()) as AnyMessage);
  }
  return messages;
}

interface RpcResponse {
  status: number;
  headers: Headers;
  body: string;
  messages: AnyMessage[];
}

/** POST an MCP request to `/mcp` — the public path nginx forwards unchanged. */
async function rpc(
  baseUrl: string,
  body: unknown,
  token?: string,
  sessionId?: string,
  path = "/mcp"
): Promise<RpcResponse> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    Accept: "application/json, text/event-stream",
  };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (sessionId) headers["mcp-session-id"] = sessionId;
  const res = await fetch(`${baseUrl}${path}`, {
    method: "POST",
    headers,
    body: JSON.stringify(body),
  });
  const text = await res.text();
  return { status: res.status, headers: res.headers, body: text, messages: parseMessages(text) };
}

async function initializeSession(baseUrl: string, token: string): Promise<string> {
  const res = await rpc(baseUrl, INITIALIZE, token);
  assert.equal(res.status, 200, `initialize failed: ${res.status} ${res.body}`);
  const sessionId = res.headers.get("mcp-session-id");
  assert.ok(sessionId, "initialize must mint a session id");
  return sessionId;
}

/* ---------------------------------------------------------------------------
 * 0. Which requests wait for an API verdict (the response-head hold)
 * ------------------------------------------------------------------------- */

test("expectsApiCall is true exactly for the two proxied tool methods", () => {
  assert.equal(expectsApiCall(LIST_TOOLS), true);
  assert.equal(
    expectsApiCall({ jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "x" } }),
    true
  );
  // A batch is held if any message in it reaches the API.
  assert.equal(expectsApiCall([{ jsonrpc: "2.0", method: "notifications/initialized" }, LIST_TOOLS]), true);

  for (const body of [
    INITIALIZE,
    { jsonrpc: "2.0", id: 9, method: "ping" },
    { jsonrpc: "2.0", method: "notifications/initialized" },
    [{ jsonrpc: "2.0", id: 9, method: "ping" }],
    undefined,
    null,
    "not json",
    { jsonrpc: "2.0", id: 4, method: "tools/List" },
  ]) {
    assert.equal(expectsApiCall(body), false, `should not hold for ${JSON.stringify(body)}`);
  }
});

/* ---------------------------------------------------------------------------
 * 0b. The response gate itself, against a recording response object
 *
 * These are unit tests rather than end-to-end ones because the hard part is the
 * ORDER of events (a head held and then released), which an HTTP client only ever
 * sees the final byte stream of. The end-to-end tests above then prove the bytes.
 * ------------------------------------------------------------------------- */

interface Recording {
  res: import("node:http").ServerResponse;
  events: string[];
}

function makeRecordingResponse(): Recording {
  const events: string[] = [];
  const res = {
    writeHead(status: number, headers?: unknown) {
      events.push(
        `head ${status}${headers === undefined ? "" : ` ${JSON.stringify(headers)}`}`
      );
      return res;
    },
    write(chunk: unknown) {
      events.push(`write ${String(chunk)}`);
      return true;
    },
    end(chunk?: unknown) {
      events.push(`end${chunk === undefined ? "" : ` ${String(chunk)}`}`);
      return res;
    },
    flushHeaders() {
      events.push("flushHeaders");
    },
  };
  return { res: res as unknown as import("node:http").ServerResponse, events };
}

test("the gate holds a tool request's head until the verdict, then replays it", () => {
  const { res, events } = makeRecordingResponse();
  const state = createApiAuthFailureState(true);
  installAuthAwareResponse(res, state, CHALLENGE);

  res.writeHead(200, { "content-type": "text/event-stream" });
  assert.deepEqual(events, [], "the head must not be sent before the API's verdict");

  // The API accepts the token.
  state.apiCallsStarted = 1;
  state.apiCallsSettled = 1;
  state.onVerdict?.();
  assert.deepEqual(events, ['head 200 {"content-type":"text/event-stream"}']);

  // Everything after the verdict streams normally.
  res.write(": keepalive\n\n");
  res.end();
  assert.deepEqual(events, [
    'head 200 {"content-type":"text/event-stream"}',
    "write : keepalive\n\n",
    "end",
  ]);
});

test("the gate replaces a held response with the challenge on an API 401", () => {
  const { res, events } = makeRecordingResponse();
  const state = createApiAuthFailureState(true);
  installAuthAwareResponse(res, state, CHALLENGE);

  res.writeHead(200, { "content-type": "text/event-stream" });
  state.apiCallsStarted = 1;
  state.apiCallsSettled = 1;
  state.unauthorized = true;
  state.onVerdict?.();

  assert.equal(events.length, 2);
  assert.match(events[0], /^head 401 /);
  assert.match(events[0], /WWW-Authenticate/);
  assert.equal(events[1], `end ${AUTH_FAILURE_BODY}`);

  // The transport's own body and end are dropped, not appended under the 401.
  assert.equal(res.write("event: message\n\n"), true);
  res.end();
  assert.equal(events.length, 2);
});

test("the gate never holds a response that expects no API verdict", () => {
  const { res, events } = makeRecordingResponse();
  const state = createApiAuthFailureState(false);
  installAuthAwareResponse(res, state, CHALLENGE);

  res.writeHead(200, { "content-type": "application/json" });
  res.write("{}");
  res.end();
  assert.deepEqual(events, ['head 200 {"content-type":"application/json"}', "write {}", "end"]);
});

test("the gate releases a held head on the first chunk (a long call's keep-alive)", () => {
  const { res, events } = makeRecordingResponse();
  const state = createApiAuthFailureState(true);
  installAuthAwareResponse(res, state, CHALLENGE);

  res.writeHead(200, { "content-type": "text/event-stream" });
  state.apiCallsStarted = 1; // the tool call is still running
  assert.deepEqual(events, []);

  res.write(": keepalive\n\n");
  assert.deepEqual(events, [
    'head 200 {"content-type":"text/event-stream"}',
    "write : keepalive\n\n",
  ]);
});

test("the gate releases a held response when the transport ends it", () => {
  const { res, events } = makeRecordingResponse();
  const state = createApiAuthFailureState(true);
  installAuthAwareResponse(res, state, CHALLENGE);

  // An error response the transport produces on its own: no API verdict ever comes.
  res.writeHead(400, { "content-type": "application/json" });
  res.end(JSON.stringify({ jsonrpc: "2.0", error: { code: -32000 } }));
  assert.equal(events.length, 2);
  assert.match(events[0], /^head 400 /);
  assert.match(events[1], /^end \{"jsonrpc"/);
});

test("the gate swallows an implicit flush while it holds the head", () => {
  const { res, events } = makeRecordingResponse();
  const state = createApiAuthFailureState(true);
  installAuthAwareResponse(res, state, CHALLENGE);

  res.writeHead(200, { "content-type": "text/event-stream" });
  res.flushHeaders();
  assert.deepEqual(events, [], "a held head must not be flushed implicitly");

  state.apiCallsStarted = 1;
  state.apiCallsSettled = 1;
  state.onVerdict?.();
  res.flushHeaders();
  assert.deepEqual(events, ['head 200 {"content-type":"text/event-stream"}', "flushHeaders"]);
});

/* ---------------------------------------------------------------------------
 * 1. The RFC 9728 metadata document
 * ------------------------------------------------------------------------- */

test("GET /.well-known/oauth-protected-resource is served unauthenticated", async (t) => {
  const { baseUrl, close } = await startServer(makeStubApiClient().client);
  t.after(close);

  const res = await fetch(`${baseUrl}/.well-known/oauth-protected-resource`);
  assert.equal(res.status, 200);
  assert.match(res.headers.get("content-type") ?? "", /application\/json/);
  // Nothing about the metadata is a challenge: a client fetches it because it has no token.
  assert.equal(res.headers.get("www-authenticate"), null);
  assert.deepEqual(await res.json(), {
    resource: "https://reelbolt.example.com/mcp",
    authorization_servers: ["https://reelbolt.example.com"],
    scopes_supported: ["assistant.tools", "assistant.admin"],
    bearer_methods_supported: ["header"],
  });
});

test("the metadata document is served with the public URLs it was configured with", async (t) => {
  const { baseUrl, close } = await startServer(makeStubApiClient().client, {
    resourceUrl: "http://localhost:8080/mcp",
    authorizationServerUrl: "http://localhost:8080",
  });
  t.after(close);

  const res = await fetch(`${baseUrl}/.well-known/oauth-protected-resource`);
  assert.equal(res.status, 200);
  const document = (await res.json()) as Record<string, unknown>;
  // No hardcoded host anywhere: the same code serves both documents.
  assert.equal(document.resource, "http://localhost:8080/mcp");
  assert.deepEqual(document.authorization_servers, ["http://localhost:8080"]);
});

test("the path-suffixed URL RFC 9728 builds for a resource with a path also answers", async (t) => {
  const { baseUrl, close } = await startServer(makeStubApiClient().client);
  t.after(close);

  const expected = {
    resource: "https://reelbolt.example.com/mcp",
    authorization_servers: ["https://reelbolt.example.com"],
    scopes_supported: ["assistant.tools", "assistant.admin"],
    bearer_methods_supported: ["header"],
  };

  // Exactly the URL a strict client derives from the resource identifier: the well-known
  // string inserted between the host and the resource's path components (RFC 9728 §3).
  const derived = `${PROTECTED_RESOURCE_METADATA_PATH}${new URL(OAUTH_CONFIG.resourceUrl).pathname}`;
  assert.equal(derived, "/.well-known/oauth-protected-resource/mcp");

  for (const path of [PROTECTED_RESOURCE_METADATA_PATH, derived, `${PROTECTED_RESOURCE_METADATA_PATH}/`]) {
    const res = await fetch(`${baseUrl}${path}`);
    assert.equal(res.status, 200, `${path} must serve the metadata document`);
    assert.deepEqual(await res.json(), expected, `${path} must serve the same document`);
    assert.equal(res.headers.get("www-authenticate"), null);
  }
});

test("only the metadata paths are metadata paths", () => {
  assert.equal(isProtectedResourceMetadataPath(PROTECTED_RESOURCE_METADATA_PATH), true);
  assert.equal(isProtectedResourceMetadataPath(`${PROTECTED_RESOURCE_METADATA_PATH}/mcp`), true);
  assert.equal(isProtectedResourceMetadataPath(`${PROTECTED_RESOURCE_METADATA_PATH}/`), true);
  assert.equal(isProtectedResourceMetadataPath(`${PROTECTED_RESOURCE_METADATA_PATH}/a/b`), true);

  for (const path of [
    "/mcp",
    "/health",
    "/",
    "/.well-known/oauth-authorization-server",
    `/.well-known/oauth-protected-resource-other`,
    `/.well-known/oauth-protected-resourcex/mcp`,
  ]) {
    assert.equal(isProtectedResourceMetadataPath(path), false, `${path} is not the metadata path`);
  }
});

/* ---------------------------------------------------------------------------
 * 2. No bearer token -> the challenge, not a JSON-RPC error
 * ------------------------------------------------------------------------- */

test("a tokenless POST is 401 with the WWW-Authenticate challenge and no JSON-RPC body", async (t) => {
  const { baseUrl, close } = await startServer(makeStubApiClient().client);
  t.after(close);

  const res = await rpc(baseUrl, INITIALIZE);
  assert.equal(res.status, 401);
  assert.equal(res.headers.get("www-authenticate"), CHALLENGE);
  assert.equal(res.body, "");
  assert.deepEqual(res.messages, []);
});

test("every unauthenticated method is challenged, including a GET stream", async (t) => {
  const { baseUrl, close } = await startServer(makeStubApiClient().client);
  t.after(close);

  for (const method of ["GET", "DELETE"]) {
    const res = await fetch(`${baseUrl}/mcp`, { method });
    assert.equal(res.status, 401, `${method} should be challenged`);
    assert.equal(res.headers.get("www-authenticate"), CHALLENGE);
  }
});

test("a malformed Authorization value is challenged rather than forwarded", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const res = await fetch(`${baseUrl}/mcp`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: "Basic dXNlcjpwdw==" },
    body: JSON.stringify(INITIALIZE),
  });
  assert.equal(res.status, 401);
  assert.equal(res.headers.get("www-authenticate"), CHALLENGE);
  assert.deepEqual(api.tokens, []);
});

test("the health check stays unauthenticated", async (t) => {
  const { baseUrl, close } = await startServer(makeStubApiClient().client);
  t.after(close);

  const res = await fetch(`${baseUrl}/health`);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { status: "ok" });
});

/* ---------------------------------------------------------------------------
 * 3. The Inference API rejects the presented token with 401 -> the same challenge
 * ------------------------------------------------------------------------- */

test("an API 401 becomes HTTP 401 + the challenge, not a JSON-RPC error", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const sessionId = await initializeSession(baseUrl, "rejected-token");
  api.setBehavior("unauthorized");

  const res = await rpc(baseUrl, LIST_TOOLS, "rejected-token", sessionId);
  assert.equal(res.status, 401);
  assert.equal(res.headers.get("www-authenticate"), CHALLENGE);
  assert.match(res.headers.get("content-type") ?? "", /application\/json/);
  // The transport's own body — a JSON-RPC error inside an SSE envelope — must be gone.
  assert.equal(res.body, AUTH_FAILURE_BODY);
  assert.doesNotMatch(res.body, /jsonrpc/);
  assert.deepEqual(JSON.parse(res.body), {
    error: "invalid_token",
    error_description:
      "The ReelBolt access token was rejected. Fetch the protected-resource metadata to " +
      "discover the authorization server, then retry with a bearer token.",
  });
});

test("an API 401 on tools/call becomes the same challenge", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const sessionId = await initializeSession(baseUrl, "rejected-token");
  api.setBehavior("unauthorized");

  const res = await rpc(
    baseUrl,
    { jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "ListProjects", arguments: {} } },
    "rejected-token",
    sessionId
  );
  assert.equal(res.status, 401);
  assert.equal(res.headers.get("www-authenticate"), CHALLENGE);
});

test("a rejected token cannot challenge a concurrent, authorized request", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  api.setTokenVerdict("rejected-token", "unauthorized");
  const goodSession = await initializeSession(baseUrl, "good-token");
  const badSession = await initializeSession(baseUrl, "rejected-token");

  const [good, bad] = await Promise.all([
    rpc(baseUrl, LIST_TOOLS, "good-token", goodSession),
    rpc(baseUrl, LIST_TOOLS, "rejected-token", badSession),
  ]);

  assert.equal(good.status, 200, `the authorized request lost its result: ${good.body}`);
  assert.equal(good.headers.get("www-authenticate"), null);
  assert.ok(good.messages.at(-1)?.result, "expected tools/list to return its result");
  assert.equal(bad.status, 401);
  assert.equal(bad.headers.get("www-authenticate"), CHALLENGE);
});

/* ---------------------------------------------------------------------------
 * 4. Every other API failure keeps today's behaviour
 * ------------------------------------------------------------------------- */

test("a non-401 API failure stays a JSON-RPC error with no challenge", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const sessionId = await initializeSession(baseUrl, "good-token");
  api.setBehavior("server-error");

  const res = await rpc(baseUrl, LIST_TOOLS, "good-token", sessionId);
  assert.equal(res.status, 200);
  assert.equal(res.headers.get("www-authenticate"), null);
  const message = res.messages.at(-1);
  assert.ok(message, `expected a JSON-RPC message, got: ${JSON.stringify(res.body)}`);
  assert.equal(message.jsonrpc, "2.0");
  assert.ok(message.error, "expected a JSON-RPC error object");
  assert.doesNotMatch(res.body, /invalid_token/);
});

test("an unreachable API stays a JSON-RPC error", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer({
    catalog: async () => {
      throw new ReelBoltApiError("Inference API unreachable at http://inference:8080");
    },
    callTool: api.client.callTool,
  });
  t.after(close);

  const sessionId = await initializeSession(baseUrl, "good-token");
  const res = await rpc(baseUrl, LIST_TOOLS, "good-token", sessionId);
  assert.equal(res.status, 200);
  assert.equal(res.headers.get("www-authenticate"), null);
  assert.ok(res.messages.at(-1)?.error);
});

/* ---------------------------------------------------------------------------
 * 5. The transport itself is unchanged: a good token still round-trips
 * ------------------------------------------------------------------------- */

test("a good token still lists tools over /mcp, with the token forwarded verbatim", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const sessionId = await initializeSession(baseUrl, "good-token");
  const res = await rpc(baseUrl, LIST_TOOLS, "good-token", sessionId);
  assert.equal(res.status, 200);
  assert.equal(res.headers.get("www-authenticate"), null);
  // The held head is replayed exactly as the transport wrote it: the response is still the
  // SSE stream it has always been, not something this layer invented.
  assert.match(res.headers.get("content-type") ?? "", /text\/event-stream/);

  const message = res.messages.at(-1);
  assert.ok(message, `expected a JSON-RPC message, got: ${JSON.stringify(res.body)}`);
  const tools = (message.result as { tools: { name: string; inputSchema: Record<string, unknown> }[] })
    .tools;
  assert.equal(tools.length, 1);
  assert.equal(tools[0].name, "ListProjects");
  // The context wrapper (contract §4) is still injected on the way through.
  assert.ok((tools[0].inputSchema.properties as Record<string, unknown>).context);
  // Forwarded verbatim, never parsed or verified (contract §9).
  assert.deepEqual(api.tokens, ["good-token"]);
});

test("initialize is answered without waiting for any API verdict", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const res = await rpc(baseUrl, INITIALIZE, "good-token");
  assert.equal(res.status, 200);
  assert.ok(res.headers.get("mcp-session-id"), "initialize must still open a session");
  // initialize never reaches the API, so it must not be held behind one.
  assert.deepEqual(api.tokens, []);
});

const UNKNOWN_SESSION = "00000000-0000-0000-0000-000000000000";
const SESSION_NOT_FOUND = {
  jsonrpc: "2.0",
  error: { code: -32001, message: "Session not found" },
  id: null,
};

test("an unknown session id on tools/call is 404 -32001, never a held or orphaned response", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const call = { jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "ListProjects", arguments: {} } };
  const res = await rpc(baseUrl, call, "good-token", UNKNOWN_SESSION);
  assert.equal(res.status, 404);
  assert.deepEqual(JSON.parse(res.body), SESSION_NOT_FOUND);
  assert.equal(res.headers.get("www-authenticate"), null);
  assert.deepEqual(api.tokens, []);
  // No session was created for it: the same id is still unknown afterwards.
  const again = await rpc(baseUrl, LIST_TOOLS, "good-token", UNKNOWN_SESSION);
  assert.equal(again.status, 404);
});

test("an unknown session id on tools/list is 404 -32001", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const res = await rpc(baseUrl, LIST_TOOLS, "good-token", UNKNOWN_SESSION);
  assert.equal(res.status, 404);
  assert.deepEqual(JSON.parse(res.body), SESSION_NOT_FOUND);
  assert.deepEqual(api.tokens, []);
});

test("an initialize carrying a stale session id starts a new session with a new id", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const res = await rpc(baseUrl, INITIALIZE, "good-token", UNKNOWN_SESSION);
  assert.equal(res.status, 200);
  const newId = res.headers.get("mcp-session-id");
  assert.ok(newId);
  assert.notEqual(newId, UNKNOWN_SESSION);
  const list = await rpc(baseUrl, LIST_TOOLS, "good-token", newId);
  assert.equal(list.status, 200);
});

test("a known session id is unchanged and keeps working", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const sessionId = await initializeSession(baseUrl, "good-token");
  assert.equal((await rpc(baseUrl, LIST_TOOLS, "good-token", sessionId)).status, 200);
  assert.equal((await rpc(baseUrl, LIST_TOOLS, "good-token", sessionId)).status, 200);
});

test("GET and DELETE with an unknown session id are 404 -32001", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  for (const method of ["GET", "DELETE"]) {
    const res = await fetch(`${baseUrl}/mcp`, {
      method,
      headers: {
        Authorization: "Bearer good-token",
        Accept: "text/event-stream",
        "mcp-session-id": UNKNOWN_SESSION,
      },
    });
    assert.equal(res.status, 404, method);
    assert.deepEqual(JSON.parse(await res.text()), SESSION_NOT_FOUND, method);
  }
});

test("a missing bearer is still 401 before the session check", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const res = await rpc(baseUrl, LIST_TOOLS, undefined, UNKNOWN_SESSION);
  assert.equal(res.status, 401);
  assert.equal(res.headers.get("www-authenticate"), CHALLENGE);
});

test("idle sessions are reaped and free their per-principal slot", async () => {
  const stub = makeStubApiClient();
  let t = 0;
  mock.timers.enable({ apis: ["setInterval"] });
  const server = createHttpServer({ apiClient: stub.client, oauth: OAUTH_CONFIG, now: () => t, idleMs: 60_000 });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const addr = server.address();
  assert.ok(addr && typeof addr === "object");
  const base = `http://127.0.0.1:${addr.port}`;
  try {
    const sid = await initializeSession(base, "tok");
    t = 120_000;
    mock.timers.tick(60_000);
    await new Promise((r) => setTimeout(r, 50));
    const res = await rpc(base, LIST_TOOLS, "tok", sid);
    assert.equal(res.status, 404);
  } finally {
    mock.timers.reset();
    server.closeAllConnections();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
});

/* ---------------------------------------------------------------------------
 * Session and body caps that do not depend on the bearer value (EDGE-02)
 * ------------------------------------------------------------------------- */

/** `initialize` with a made-up token, as seen by nginx from `address`. */
async function initializeFrom(baseUrl: string, token: string, address?: string): Promise<Response> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    Accept: "application/json, text/event-stream",
    Authorization: `Bearer ${token}`,
  };
  if (address) headers["X-Real-IP"] = address;
  const res = await fetch(`${baseUrl}/mcp`, { method: "POST", headers, body: JSON.stringify(INITIALIZE) });
  await res.text();
  return res;
}

test("a different made-up token per request cannot open more than the process-wide cap", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client, OAUTH_CONFIG, { maxSessions: 3 });
  t.after(close);

  const statuses: number[] = [];
  const sessionIds: string[] = [];
  for (let i = 0; i < 6; i++) {
    const res = await initializeFrom(baseUrl, `made-up-${i}`, `198.51.100.${i + 1}`);
    statuses.push(res.status);
    const sid = res.headers.get("mcp-session-id");
    if (sid) sessionIds.push(sid);
  }
  assert.deepEqual(statuses, [200, 200, 200, 503, 503, 503]);
  // No token was ever checked: the cap is what stops it, not the API.
  assert.deepEqual(api.tokens, []);

  // A closed session frees its slot.
  const closed = await fetch(`${baseUrl}/mcp`, {
    method: "DELETE",
    headers: { Authorization: "Bearer made-up-0", "mcp-session-id": sessionIds[0] },
  });
  await closed.text();
  assert.equal((await initializeFrom(baseUrl, "made-up-again")).status, 200);
});

test("concurrent initializes cannot overshoot the process-wide cap", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client, OAUTH_CONFIG, { maxSessions: 4 });
  t.after(close);
  // Widen the window between the cap check and the session registering: every admitted
  // initialize waits before the SDK transport sees it. A slot taken only at registration
  // would let all 20 through; a slot held from the check lets exactly 4 through.
  const original = StreamableHTTPServerTransport.prototype.handleRequest;
  const delayed = mock.method(
    StreamableHTTPServerTransport.prototype,
    "handleRequest",
    async function (this: StreamableHTTPServerTransport, ...args: Parameters<typeof original>) {
      await new Promise((resolve) => setTimeout(resolve, 150));
      return original.apply(this, args);
    }
  );
  t.after(() => delayed.mock.restore());

  const results = await Promise.all(
    Array.from({ length: 20 }, (_, i) => initializeFrom(baseUrl, `burst-${i}`, `198.51.100.${i + 1}`))
  );
  assert.equal(results.filter((r) => r.status === 200).length, 4);
  assert.equal(results.filter((r) => r.status === 503).length, 16);
  // The refusal tells the client when to come back.
  for (const r of results.filter((r) => r.status === 503)) assert.equal(r.headers.get("retry-after"), "60");
  assert.equal(delayed.mock.callCount(), 4);
});

test("the session caps default to 2000 process-wide and 200 per address", async (t) => {
  const saved = { total: process.env.MCP_MAX_SESSIONS, perIp: process.env.MCP_MAX_SESSIONS_PER_IP };
  delete process.env.MCP_MAX_SESSIONS;
  delete process.env.MCP_MAX_SESSIONS_PER_IP;
  t.after(() => {
    if (saved.total !== undefined) process.env.MCP_MAX_SESSIONS = saved.total;
    if (saved.perIp !== undefined) process.env.MCP_MAX_SESSIONS_PER_IP = saved.perIp;
  });
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const burst = async (count: number, addressOf: (i: number) => string, tag: string): Promise<number[]> => {
    const statuses: number[] = [];
    for (let start = 0; start < count; start += 50) {
      const batch = await Promise.all(
        Array.from({ length: Math.min(50, count - start) }, (_, k) =>
          initializeFrom(baseUrl, `${tag}-${start + k}`, addressOf(start + k))
        )
      );
      statuses.push(...batch.map((r) => r.status));
    }
    return statuses;
  };

  // Per address: 200 open, the 201st refused.
  assert.ok((await burst(200, () => "192.0.2.1", "ip")).every((s) => s === 200));
  assert.equal((await initializeFrom(baseUrl, "ip-over", "192.0.2.1")).status, 429);

  // Process-wide: 1800 more from distinct addresses reach 2000, the next is refused.
  const spread = (i: number): string => `10.${(i >> 16) & 255}.${(i >> 8) & 255}.${i & 255}`;
  assert.ok((await burst(1800, (i) => spread(i + 1), "all")).every((s) => s === 200));
  const over = await initializeFrom(baseUrl, "all-over", "198.51.100.200");
  assert.equal(over.status, 503);
  assert.equal(over.headers.get("retry-after"), "60");
});

test("one client address cannot hold more than its own cap, and another address is unaffected", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client, OAUTH_CONFIG, { maxSessionsPerIp: 2 });
  t.after(close);

  assert.equal((await initializeFrom(baseUrl, "a", "203.0.113.9")).status, 200);
  assert.equal((await initializeFrom(baseUrl, "b", "203.0.113.9")).status, 200);
  const refused = await initializeFrom(baseUrl, "c", "203.0.113.9");
  assert.equal(refused.status, 429);
  assert.equal(refused.headers.get("mcp-session-id"), null);
  assert.equal((await initializeFrom(baseUrl, "d", "203.0.113.10")).status, 200);
  // Rotating inside one IPv6 /64 is one address.
  assert.equal((await initializeFrom(baseUrl, "e", "2001:db8:1:2::1")).status, 200);
  assert.equal((await initializeFrom(baseUrl, "f", "2001:db8:1:2:aaaa::2")).status, 200);
  assert.equal((await initializeFrom(baseUrl, "g", "2001:db8:1:2:ffff:ffff:ffff:ffff")).status, 429);
  assert.equal((await initializeFrom(baseUrl, "h", "2001:db8:1:3::1")).status, 200);
});

test("a request that opens no session gives its slot back", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client, OAUTH_CONFIG, { maxSessions: 1 });
  t.after(close);

  // No session id and not an initialize: the transport refuses it and no session exists.
  for (let i = 0; i < 3; i++) {
    const res = await rpc(baseUrl, { jsonrpc: "2.0", id: 9, method: "ping" }, "good-token");
    assert.notEqual(res.status, 200);
    assert.equal(res.headers.get("mcp-session-id"), null);
  }
  assert.equal((await initializeFrom(baseUrl, "good-token")).status, 200);
});

test("the client address key is the address nginx sent, IPv6 on its /64", () => {
  const key = (xRealIp: string | undefined, remoteAddress?: string): string =>
    clientAddressKey({
      headers: xRealIp === undefined ? {} : { "x-real-ip": xRealIp },
      socket: { remoteAddress },
    } as unknown as http.IncomingMessage);
  assert.equal(key("203.0.113.9"), "203.0.113.9");
  assert.equal(key(undefined, "127.0.0.1"), "127.0.0.1");
  assert.equal(key(undefined, "::ffff:10.0.0.7"), "10.0.0.7");
  assert.equal(key("2001:DB8:1:2::1"), "2001:db8:1:2::/64");
  assert.equal(key("2001:db8:1:2:a:b:c:d"), "2001:db8:1:2::/64");
  assert.equal(key("2001:0db8:0001:0002::1"), "2001:db8:1:2::/64");
  assert.equal(key("2001:db8::1"), "2001:db8:0:0::/64");
  assert.equal(key("::1"), "0:0:0:0::/64");
  assert.notEqual(key("2001:db8:1:3::1"), key("2001:db8:1:2::1"));
  assert.equal(key(undefined, undefined), "unknown");
});

test("a body over the cap is 413 and never parsed, with or without a declared length", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client, OAUTH_CONFIG, { maxBodyBytes: 4096 });
  t.after(close);

  const big = { ...INITIALIZE, params: { ...INITIALIZE.params, padding: "x".repeat(64 * 1024) } };
  // Declared length (fetch sends Content-Length for a string body).
  const declared = await rpc(baseUrl, big, "made-up-token");
  assert.equal(declared.status, 413);
  assert.equal(declared.headers.get("mcp-session-id"), null);
  assert.equal((JSON.parse(declared.body) as { error: { code: number } }).error.code, -32000);

  // No declared length: a chunked upload is cut off at the cap.
  const { port } = new URL(baseUrl);
  const chunked = await new Promise<{ status: number; sessionId: string | undefined }>((resolve, reject) => {
    const req = http.request(
      {
        host: "127.0.0.1",
        port,
        path: "/mcp",
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Accept: "application/json, text/event-stream",
          Authorization: "Bearer made-up-token",
          "Transfer-Encoding": "chunked",
        },
      },
      (res) => {
        res.resume();
        res.on("end", () =>
          resolve({ status: res.statusCode ?? 0, sessionId: res.headers["mcp-session-id"] as string | undefined })
        );
      }
    );
    req.on("error", reject);
    const body = JSON.stringify(big);
    for (let at = 0; at < body.length; at += 8192) req.write(body.slice(at, at + 8192));
    req.end();
  });
  assert.equal(chunked.status, 413);
  assert.equal(chunked.sessionId, undefined);

  // An ordinary request is untouched, on the same server.
  const sessionId = await initializeSession(baseUrl, "good-token");
  assert.equal((await rpc(baseUrl, LIST_TOOLS, "good-token", sessionId)).status, 200);
});

test("the default body cap is 1 MiB: a body just under it is accepted", async (t) => {
  const api = makeStubApiClient();
  const { baseUrl, close } = await startServer(api.client);
  t.after(close);

  const under = { ...INITIALIZE, params: { ...INITIALIZE.params, padding: "x".repeat(1024 * 1024 - 2048) } };
  assert.equal((await rpc(baseUrl, under, "good-token")).status, 200);
  const over = { ...INITIALIZE, params: { ...INITIALIZE.params, padding: "x".repeat(1024 * 1024 + 1) } };
  assert.equal((await rpc(baseUrl, over, "good-token")).status, 413);
});
