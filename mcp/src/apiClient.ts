/**
 * ReelBoltApiClient — the one and only HTTP layer of the MCP proxy.
 *
 * It talks to exactly two Inference API endpoints (contract §3) and does NOTHING
 * else: no tool logic, no argument reshaping, no token verification. The token is
 * passed straight through as `Authorization: Bearer <token>` (contract §9) and the
 * Inference API is the sole authority.
 *
 * Error policy (contract §5, lead guidance): a 2xx response is a *result* and is
 * forwarded untouched (tool-level refusals arrive as 200 + the tool's own text +
 * `isError:false` and must pass through as normal results). A non-2xx response
 * (401/403 bad/insufficient token, 404 unknown tool, 5xx API failure) or an
 * unreachable API or a malformed body is a *transport-level failure* and is thrown
 * so the caller surfaces it as a JSON-RPC error — never a crash, never a
 * swallowed throw.
 */

/**
 * The MCP `annotations` object the Inference API publishes on every catalog entry:
 * a human title plus the four standard hints. Forwarded verbatim; the API is the
 * only place they are decided.
 */
export interface CatalogToolAnnotations {
  title: string;
  readOnlyHint: boolean;
  destructiveHint: boolean;
  idempotentHint: boolean;
  openWorldHint: boolean;
}

export interface CatalogTool {
  name: string;
  description?: string;
  inputSchema?: Record<string, unknown>;
  /** Human title, also repeated inside `annotations.title`. */
  title?: string;
  /** MCP standard hint object (title + four boolean hints). */
  annotations?: CatalogToolAnnotations;
}

export interface CatalogResponse {
  tools: CatalogTool[];
}

/** The tool-call result the API returns; passed through verbatim. */
export interface CallResult {
  content: unknown[];
  isError?: boolean;
}

/**
 * The narrow seam the bridge (and the tests) depend on. `token` is the caller's
 * bearer token, forwarded verbatim; `undefined` means "send no Authorization
 * header". `arguments` is forwarded verbatim (minus the bridge's own `context`
 * wrapper, which is stripped by the bridge before this is called).
 */
export interface ReelBoltApiClient {
  catalog(token: string | undefined): Promise<CatalogResponse>;
  callTool(
    name: string,
    toolArguments: Record<string, unknown>,
    context: Record<string, unknown> | undefined,
    token: string | undefined
  ): Promise<CallResult>;
}

/** Thrown for every transport-level failure; the SDK maps it to a JSON-RPC error. */
export class ReelBoltApiError extends Error {
  constructor(
    message: string,
    public readonly status?: number
  ) {
    super(message);
    this.name = "ReelBoltApiError";
  }
}

export interface ApiClientOptions {
  apiBaseUrl: string;
  /** Injectable fetch for tests; defaults to the global fetch. */
  fetchImpl?: typeof fetch;
}

export function createApiClient(options: ApiClientOptions): ReelBoltApiClient {
  const doFetch = options.fetchImpl ?? fetch;
  const base = options.apiBaseUrl.replace(/\/+$/, "");

  async function request(
    path: string,
    method: "GET" | "POST",
    token: string | undefined,
    body?: Record<string, unknown>
  ): Promise<{ status: number; statusText: string; ok: boolean; text: () => Promise<string> }> {
    const headers: Record<string, string> = { Accept: "application/json" };
    // A JSON body MUST declare its own media type. Without this, fetch falls back to
    // `Content-Type: text/plain;charset=UTF-8`, and the API's [ApiController] + [FromBody]
    // rejects the POST with 415 Unsupported Media Type. That made every tools/call fail
    // against the real API while tools/list (a GET, no body) worked — so it could not be
    // caught by any test whose fetch stub ignored headers.
    if (body !== undefined) {
      headers["Content-Type"] = "application/json";
    }
    // Forward the bearer token verbatim (contract §9). No parse, no validation.
    if (token) {
      headers["Authorization"] = `Bearer ${token}`;
    }
    let res: Response;
    try {
      res = await doFetch(`${base}${path}`, {
        method,
        headers,
        body: body !== undefined ? JSON.stringify(body) : undefined,
      });
    } catch (err) {
      // Unreachable API (DNS, connection refused, timeout, ...).
      const msg = err instanceof Error ? err.message : String(err);
      throw new ReelBoltApiError(`Inference API unreachable at ${base}${path}: ${msg}`);
    }
    const status = res.status;
    const statusText = res.statusText ?? "";
    return { status, statusText, ok: res.ok, text: () => res.text() };
  }

  async function readBody(
    res: { status: number; statusText: string; ok: boolean; text: () => Promise<string> }
  ): Promise<Record<string, unknown>> {
    if (!res.ok) {
      // Non-2xx: transport-level failure (401/403, 404 unknown tool, 5xx).
      // Read a short snippet (e.g. the 404 body carries the tool name) to make the
      // JSON-RPC error actionable, but never trust it.
      let snippet = "";
      try {
        const raw = await res.text();
        snippet = raw.slice(0, 200);
      } catch {
        // body not readable; ignore
      }
      throw new ReelBoltApiError(
        `Inference API returned ${res.status} ${res.statusText}${snippet ? `: ${snippet}` : ""}`,
        res.status
      );
    }
    let raw: string;
    try {
      raw = await res.text();
    } catch (err) {
      throw new ReelBoltApiError(
        `Inference API response body could not be read: ${err instanceof Error ? err.message : String(err)}`
      );
    }
    try {
      const parsed: unknown = JSON.parse(raw);
      if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
        throw new ReelBoltApiError(
          `Inference API returned malformed JSON (expected an object)`
        );
      }
      return parsed as Record<string, unknown>;
    } catch (err) {
      if (err instanceof ReelBoltApiError) throw err;
      throw new ReelBoltApiError(
        `Inference API returned malformed JSON: ${err instanceof Error ? err.message : String(err)}`
      );
    }
  }

  return {
    async catalog(token) {
      const body = await readBody(
        await request("/api/v1/assistant/tool-catalog", "GET", token)
      );
      if (!Array.isArray(body.tools)) {
        throw new ReelBoltApiError(
          `Inference API tool-catalog response is malformed (expected { "tools": [...] })`
        );
      }
      return { tools: body.tools as CatalogTool[] };
    },

    async callTool(name, args, context, token) {
      const payload: Record<string, unknown> = { name, arguments: args };
      // Only forward the context wrapper when the caller supplied one; the API
      // treats it as optional (contract §3/§4).
      if (context && typeof context === "object") {
        payload.context = context;
      }
      const body = await readBody(
        await request("/api/v1/assistant/tool-call", "POST", token, payload)
      );
      if (!Array.isArray(body.content)) {
        throw new ReelBoltApiError(
          `Inference API tool-call response is malformed (expected { "content": [...] })`
        );
      }
      return {
        content: body.content as unknown[],
        isError: typeof body.isError === "boolean" ? body.isError : false,
      };
    },
  };
}
