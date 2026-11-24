/**
 * The MCP server as an OAuth **resource server** (RFC 9728) — purely declarative.
 *
 * Two things live here, and nothing else:
 *
 *  1. The protected-resource metadata document and the `WWW-Authenticate` challenge
 *     that point a client at the authorization server (the Go API). Both the public
 *     MCP URL and the authorization server's origin come from configuration — no
 *     host is ever hardcoded (contract §7/§8).
 *  2. The per-request flag that turns "the Inference API rejected the presented
 *     token with 401" into that same HTTP 401 challenge instead of a JSON-RPC error
 *     (contract §7, which AMENDS `mcp-tool-bridge.md` §5; that document is frozen
 *     and is deliberately not edited).
 *
 * What is deliberately NOT here: any verification or parsing of the token. The MCP
 * server forwards the bearer token verbatim and the Inference API remains the sole
 * authority (`mcp-tool-bridge.md` §9). Advertising an authorization server is not
 * the same thing as validating a token.
 */
import { AsyncLocalStorage } from "node:async_hooks";
import { ReelBoltApiError, type ReelBoltApiClient } from "./apiClient.js";

/** The RFC 9728 metadata path this server serves, unauthenticated. */
export const PROTECTED_RESOURCE_METADATA_PATH = "/.well-known/oauth-protected-resource";

/** Public URL of this MCP server, e.g. `https://host/mcp`. The `resource` value. */
export const MCP_PUBLIC_URL_ENV = "MCP_PUBLIC_URL";

/** Public origin of the Go API (the authorization server), e.g. `https://host`. */
export const OAUTH_ISSUER_URL_ENV = "OAUTH_ISSUER_URL";

/** Scope every authenticated MCP call needs (contract §3). */
export const ASSISTANT_TOOLS_SCOPE = "assistant.tools";

/** Scope the gated tools additionally require, advertised but never demanded here. */
export const ASSISTANT_ADMIN_SCOPE = "assistant.admin";

export interface OAuthResourceConfig {
  /** The public MCP URL — the RFC 9728 `resource` identifier. */
  resourceUrl: string;
  /** The public origin of the Go API — the authorization server. */
  authorizationServerUrl: string;
}

/** Thrown for a missing or unusable OAuth variable. The HTTP transport fails closed on it. */
export class OAuthConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "OAuthConfigError";
  }
}

function requireHttpUrl(
  raw: string | undefined,
  envName: string,
  guidance: string
): URL {
  const value = (raw ?? "").trim();
  if (!value) {
    throw new OAuthConfigError(`${envName} is not set. ${guidance}`);
  }
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new OAuthConfigError(
      `${envName} is not a valid absolute URL: ${JSON.stringify(value)}. ` +
        `It must include the scheme and host (e.g. https://reelbolt.example.com).`
    );
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new OAuthConfigError(
      `${envName} must be an http(s) URL, got ${JSON.stringify(value)}.`
    );
  }
  return url;
}

/**
 * Read the two public URLs the OAuth metadata needs. Called by the HTTP transport
 * ONLY: stdio mode must start and work with neither set (contract §7).
 */
export function readOAuthResourceConfig(
  env: NodeJS.ProcessEnv = process.env
): OAuthResourceConfig {
  const resource = requireHttpUrl(
    env[MCP_PUBLIC_URL_ENV],
    MCP_PUBLIC_URL_ENV,
    "Set it to the externally reachable URL of this MCP server, e.g. " +
      "https://reelbolt.example.com/mcp. It is published as the `resource` of the " +
      "OAuth protected-resource metadata so a client can discover the authorization " +
      "server. The stdio transport does not need it."
  );
  const issuer = requireHttpUrl(
    env[OAUTH_ISSUER_URL_ENV],
    OAUTH_ISSUER_URL_ENV,
    "Set it to the public origin of the Go API, which is the OAuth authorization " +
      "server, e.g. https://reelbolt.example.com (scheme and host only, no path). " +
      "The stdio transport does not need it."
  );
  if (issuer.pathname !== "/" || issuer.search || issuer.hash) {
    throw new OAuthConfigError(
      `${OAUTH_ISSUER_URL_ENV} must be an origin — scheme and host only, no path — ` +
        `got ${JSON.stringify(issuer.origin + issuer.pathname)}. Use ` +
        `${JSON.stringify(issuer.origin)}.`
    );
  }
  return {
    // One trailing slash is not part of the identifier: RFC 9728 comparison and the
    // SDK's own resource comparison both ignore it, so it is dropped here rather than
    // published as a resource a token would then fail to match.
    resourceUrl: resource.href.replace(/\/+$/, ""),
    authorizationServerUrl: issuer.origin,
  };
}

export interface ProtectedResourceMetadata {
  resource: string;
  authorization_servers: string[];
  scopes_supported: string[];
  bearer_methods_supported: string[];
}

/** The RFC 9728 document, exactly as frozen in contract §7. */
export function buildProtectedResourceMetadata(
  config: OAuthResourceConfig
): ProtectedResourceMetadata {
  return {
    resource: config.resourceUrl,
    authorization_servers: [config.authorizationServerUrl],
    scopes_supported: [ASSISTANT_TOOLS_SCOPE, ASSISTANT_ADMIN_SCOPE],
    bearer_methods_supported: ["header"],
  };
}

/**
 * The URL a client should fetch the metadata document from.
 *
 * The document is served at the resource host's well-known root path — nginx forwards
 * `/.well-known/oauth-protected-resource` to this server with the URI preserved
 * (contract §7), while the public MCP URL itself carries the `/mcp` path — so the
 * metadata URL is the resource's ORIGIN plus that path, not the resource URL plus the
 * path (`https://host/.well-known/oauth-protected-resource`, never
 * `https://host/mcp/.well-known/...`).
 */
export function protectedResourceMetadataUrl(config: OAuthResourceConfig): string {
  return new URL(PROTECTED_RESOURCE_METADATA_PATH, new URL(config.resourceUrl).origin).href;
}

/** The exact `WWW-Authenticate` value the contract pins (contract §7). */
export function bearerChallengeValue(metadataUrl: string): string {
  return `Bearer resource_metadata="${metadataUrl}", scope="${ASSISTANT_TOOLS_SCOPE}"`;
}

/**
 * Per-request record of "the Inference API rejected the presented token".
 *
 * The MCP HTTP transport decides its own response — and, for a tool request, commits it
 * as `200 text/event-stream` BEFORE the tool handler has an answer (the SDK returns the
 * SSE Response up-front and streams the JSON-RPC result into it). So the flag is recorded
 * while the tool handler runs, and the transport layer holds the response head until that
 * verdict lands: see `installAuthAwareResponse`. An AsyncLocalStorage scope keeps all of
 * it strictly per request — two concurrent requests, one authorized and one not, can never
 * see each other's flag.
 */
export interface ApiAuthFailureState {
  /**
   * True when the request's body asks for a tool (`tools/list` / `tools/call`), so an
   * Inference API verdict is on its way.
   */
  readonly expectedApiCall: boolean;
  /** The API rejected the presented token with 401. */
  unauthorized: boolean;
  /** API calls started / settled within this request. */
  apiCallsStarted: number;
  apiCallsSettled: number;
  /**
   * Set by the HTTP response gate; called whenever a fresh verdict is available, so a
   * held response can be released — or challenged — the moment the answer is known.
   */
  onVerdict?: () => void;
}

export function createApiAuthFailureState(expectedApiCall: boolean): ApiAuthFailureState {
  return {
    expectedApiCall,
    unauthorized: false,
    apiCallsStarted: 0,
    apiCallsSettled: 0,
  };
}

const apiAuthFailure = new AsyncLocalStorage<ApiAuthFailureState>();

/** Run one HTTP request's work inside its own auth-failure scope. */
export function runInAuthFailureScope<T>(
  state: ApiAuthFailureState,
  work: () => Promise<T>
): Promise<T> {
  return apiAuthFailure.run(state, work);
}

/**
 * Wrap the API client so a 401 from the Inference API marks the current request, and so
 * the request's response gate learns the moment the verdict is in.
 * The error is rethrown untouched: every other status keeps today's behaviour.
 */
export function withAuthFailureTracking(client: ReelBoltApiClient): ReelBoltApiClient {
  async function track<T>(call: () => Promise<T>): Promise<T> {
    const state = apiAuthFailure.getStore();
    if (state) {
      state.apiCallsStarted += 1;
    }
    try {
      return await call();
    } catch (err) {
      if (state && err instanceof ReelBoltApiError && err.status === 401) {
        state.unauthorized = true;
      }
      throw err;
    } finally {
      if (state) {
        state.apiCallsSettled += 1;
        state.onVerdict?.();
      }
    }
  }
  return {
    catalog: (token) => track(() => client.catalog(token)),
    callTool: (name, toolArguments, context, token) =>
      track(() => client.callTool(name, toolArguments, context, token)),
  };
}
