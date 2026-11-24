/**
 * ReelBolt MCP server entry point.
 *
 * Transports (contract §5/§9):
 *   - stdio: the caller's bearer token is read ONCE at startup from
 *     REELBOLT_MCP_TOKEN and forwarded verbatim on every call.
 *   - http:  the token is read PER REQUEST from the Authorization header and
 *     forwarded verbatim. (Stateless-friendly; the Inference API re-authenticates
 *     per call and is the sole authority.)
 *
 * The server never parses, decodes or validates the token (contract §9) and never
 * requires JWT_SIGNING_KEY. It is a thin proxy: tools/list proxies the catalog,
 * tools/call proxies the invoke. See bridge.ts.
 *
 * Over HTTP it is also an OAuth resource server (docs/design/mcp-oauth.md §7): it
 * publishes the RFC 9728 metadata and challenges a request it cannot authenticate. No
 * authorization server is contacted and no token is inspected — the two public URLs it
 * advertises come from MCP_PUBLIC_URL / OAUTH_ISSUER_URL, and stdio needs neither.
 */
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { createApiClient } from "./apiClient.js";
import { makeProxyServer } from "./bridge.js";
import { createHttpServer } from "./httpTransport.js";
import {
  OAuthConfigError,
  readOAuthResourceConfig,
  type OAuthResourceConfig,
} from "./oauth.js";

// The Inference API base URL. The API is the sole authority for auth; the server
// has no opinion about token contents and reads no signing key (contract §9).
// Fail closed: nothing listens on a default port, so a missing URL must be a clear
// startup error, not a server that starts and then fails every call against nothing.
const apiBaseUrl = process.env.INFERENCE_API_URL;
if (!apiBaseUrl) {
  console.error(
    "FATAL: INFERENCE_API_URL is not set. Set it to the Inference API base URL " +
      "(e.g. http://inference:8080 in the compose deployment, or the operator's " +
      "host URL for the Claude Code plugin). The MCP server proxies only; without " +
      "a target it has nothing to forward to."
  );
  process.exit(1);
}
// The API client is handed to the transports unwrapped: the HTTP transport installs its
// own 401 tracking, and stdio has no HTTP response to challenge with.
const apiClient = createApiClient({ apiBaseUrl });

async function runStdio(): Promise<void> {
  const token = process.env.REELBOLT_MCP_TOKEN;
  if (!token) {
    // Fail fast with a clear message. This is the user's OWN token (never the
    // platform signing key). Without it every call would 401.
    console.error(
      "FATAL: REELBOLT_MCP_TOKEN is not set. The stdio transport requires the " +
        "caller's ReelBolt bearer token at startup. (The token is forwarded " +
        "verbatim to the Inference API and is never verified here.)"
    );
    process.exit(1);
  }

  const server = makeProxyServer({
    apiClient,
    getAuthToken: () => token,
  });
  const transport = new StdioServerTransport();
  await server.connect(transport);
  transport.onclose = () => {
    // Clean shutdown when the client (or parent process) closes stdin.
    process.exit(0);
  };
  console.error("ReelBolt MCP server ready on stdio");
}

async function runHttp(): Promise<void> {
  // The OAuth metadata's two public URLs are required HERE and only here: over HTTP the
  // server advertises an authorization server and challenges unauthenticated requests,
  // and it refuses to guess a host. Fail closed, in the same style as INFERENCE_API_URL
  // above. stdio mode never reaches this function and needs neither variable.
  let oauth: OAuthResourceConfig;
  try {
    oauth = readOAuthResourceConfig();
  } catch (err) {
    if (err instanceof OAuthConfigError) {
      console.error(`FATAL: ${err.message}`);
      process.exit(1);
    }
    throw err;
  }

  const httpServer = createHttpServer({ apiClient, oauth });

  const port = parseInt(process.env.PORT || "3000", 10);
  httpServer.listen(port, () => {
    // The port actually bound, which is not `port` when PORT=0 asks for any free one.
    const address = httpServer.address();
    const bound = address !== null && typeof address === "object" ? address.port : port;
    console.error(`ReelBolt MCP server listening on port ${bound} (streamable HTTP)`);
  });
}

async function main(): Promise<void> {
  const transport = (process.env.MCP_TRANSPORT || "stdio").toLowerCase();
  console.error(`Starting ReelBolt MCP server (${transport} transport)`);
  switch (transport) {
    case "stdio":
      await runStdio();
      break;
    case "http":
      await runHttp();
      break;
    default:
      console.error(`Unknown transport: ${transport}`);
      process.exit(1);
  }
}

main().catch((error) => {
  console.error("Server error:", error);
  process.exit(1);
});
