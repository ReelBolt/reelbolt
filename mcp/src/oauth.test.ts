import { strict as assert } from "node:assert";
import { test } from "node:test";
import {
  ASSISTANT_ADMIN_SCOPE,
  ASSISTANT_TOOLS_SCOPE,
  MCP_PUBLIC_URL_ENV,
  OAUTH_ISSUER_URL_ENV,
  OAuthConfigError,
  PROTECTED_RESOURCE_METADATA_PATH,
  bearerChallengeValue,
  buildProtectedResourceMetadata,
  createApiAuthFailureState,
  protectedResourceMetadataUrl,
  readOAuthResourceConfig,
  runInAuthFailureScope,
  withAuthFailureTracking,
} from "./oauth.js";
import { ReelBoltApiError, type CallResult, type ReelBoltApiClient } from "./apiClient.js";

const ENV = {
  [MCP_PUBLIC_URL_ENV]: "https://reelbolt.example.com/mcp",
  [OAUTH_ISSUER_URL_ENV]: "https://reelbolt.example.com",
};

/* ---------------------------------------------------------------------------
 * Configuration — fail closed, from the environment, never hardcoded
 * ------------------------------------------------------------------------- */

test("readOAuthResourceConfig reads both public URLs from the environment", () => {
  const config = readOAuthResourceConfig({ ...ENV });
  assert.deepEqual(config, {
    resourceUrl: "https://reelbolt.example.com/mcp",
    authorizationServerUrl: "https://reelbolt.example.com",
  });
});

test("readOAuthResourceConfig normalizes a trailing slash and the issuer's root path", () => {
  const config = readOAuthResourceConfig({
    [MCP_PUBLIC_URL_ENV]: "https://reelbolt.example.com/mcp/",
    [OAUTH_ISSUER_URL_ENV]: "https://reelbolt.example.com/",
  });
  assert.equal(config.resourceUrl, "https://reelbolt.example.com/mcp");
  assert.equal(config.authorizationServerUrl, "https://reelbolt.example.com");
});

test("readOAuthResourceConfig fails closed and names the missing variable", () => {
  assert.throws(
    () => readOAuthResourceConfig({ [OAUTH_ISSUER_URL_ENV]: ENV[OAUTH_ISSUER_URL_ENV] }),
    (err: unknown) => {
      assert.ok(err instanceof OAuthConfigError);
      assert.match(err.message, /MCP_PUBLIC_URL is not set/);
      // The stdio transport must not be told it needs this.
      assert.match(err.message, /stdio transport does not need it/);
      return true;
    }
  );
  assert.throws(
    () => readOAuthResourceConfig({ [MCP_PUBLIC_URL_ENV]: ENV[MCP_PUBLIC_URL_ENV] }),
    (err: unknown) => {
      assert.ok(err instanceof OAuthConfigError);
      assert.match(err.message, /OAUTH_ISSUER_URL is not set/);
      return true;
    }
  );
});

test("readOAuthResourceConfig rejects a relative URL and a non-http scheme", () => {
  assert.throws(
    () => readOAuthResourceConfig({ ...ENV, [MCP_PUBLIC_URL_ENV]: "reelbolt.example.com/mcp" }),
    /MCP_PUBLIC_URL is not a valid absolute URL/
  );
  assert.throws(
    () => readOAuthResourceConfig({ ...ENV, [OAUTH_ISSUER_URL_ENV]: "ftp://reelbolt.example.com" }),
    /OAUTH_ISSUER_URL must be an http\(s\) URL/
  );
});

test("readOAuthResourceConfig rejects an issuer URL carrying a path", () => {
  assert.throws(
    () => readOAuthResourceConfig({ ...ENV, [OAUTH_ISSUER_URL_ENV]: "https://host/api" }),
    /OAUTH_ISSUER_URL must be an origin/
  );
});

/* ---------------------------------------------------------------------------
 * The RFC 9728 document and the challenge (contract §7)
 * ------------------------------------------------------------------------- */

test("buildProtectedResourceMetadata emits the frozen RFC 9728 document", () => {
  const document = buildProtectedResourceMetadata(readOAuthResourceConfig({ ...ENV }));
  assert.deepEqual(document, {
    resource: "https://reelbolt.example.com/mcp",
    authorization_servers: ["https://reelbolt.example.com"],
    scopes_supported: ["assistant.tools", "assistant.admin"],
    bearer_methods_supported: ["header"],
  });
  // Exactly the four contract keys — no invented fields.
  assert.deepEqual(Object.keys(document), [
    "resource",
    "authorization_servers",
    "scopes_supported",
    "bearer_methods_supported",
  ]);
  assert.deepEqual(document.scopes_supported, [ASSISTANT_TOOLS_SCOPE, ASSISTANT_ADMIN_SCOPE]);
});

test("protectedResourceMetadataUrl is the resource ORIGIN plus the well-known path", () => {
  const config = readOAuthResourceConfig({ ...ENV });
  assert.equal(
    protectedResourceMetadataUrl(config),
    `https://reelbolt.example.com${PROTECTED_RESOURCE_METADATA_PATH}`
  );
  // Never the resource URL with the path appended.
  assert.doesNotMatch(protectedResourceMetadataUrl(config), /\/mcp\/\.well-known/);
});

test("bearerChallengeValue is the exact header the contract pins", () => {
  assert.equal(
    bearerChallengeValue("https://reelbolt.example.com/.well-known/oauth-protected-resource"),
    'Bearer resource_metadata="https://reelbolt.example.com/.well-known/oauth-protected-resource", scope="assistant.tools"'
  );
});

/* ---------------------------------------------------------------------------
 * API 401 tracking (the amendment to mcp-tool-bridge.md §5)
 * ------------------------------------------------------------------------- */

function makeClient(fail: (() => void) | undefined): ReelBoltApiClient {
  return {
    async catalog(): Promise<never> {
      fail?.();
      throw new Error("unreachable");
    },
    async callTool(): Promise<CallResult> {
      fail?.();
      throw new Error("unreachable");
    },
  };
}

test("withAuthFailureTracking marks the request on an API 401 and rethrows it", async () => {
  const state = createApiAuthFailureState(true);
  const client = withAuthFailureTracking(
    makeClient(() => {
      throw new ReelBoltApiError("Inference API returned 401 Unauthorized", 401);
    })
  );
  await assert.rejects(
    () => runInAuthFailureScope(state, () => client.catalog("tok")),
    (err: unknown) => err instanceof ReelBoltApiError && err.status === 401
  );
  assert.equal(state.unauthorized, true);
});

test("withAuthFailureTracking leaves every other failure alone", async () => {
  const state = createApiAuthFailureState(true);
  const client = withAuthFailureTracking(
    makeClient(() => {
      throw new ReelBoltApiError("Inference API returned 500 Internal Server Error", 500);
    })
  );
  await assert.rejects(
    () => runInAuthFailureScope(state, () => client.callTool("ListProjects", {}, undefined, "tok")),
    (err: unknown) => err instanceof ReelBoltApiError && err.status === 500
  );
  assert.equal(state.unauthorized, false);
});

test("withAuthFailureTracking does not leak one request's 401 into another", async () => {
  const unauthorized = createApiAuthFailureState(true);
  const clean = createApiAuthFailureState(true);
  const client = withAuthFailureTracking(
    makeClient(() => {
      throw new ReelBoltApiError("Inference API returned 401 Unauthorized", 401);
    })
  );
  await Promise.all([
    runInAuthFailureScope(unauthorized, () => client.catalog("bad").catch(() => undefined)),
    runInAuthFailureScope(clean, async () => undefined),
  ]);
  assert.equal(unauthorized.unauthorized, true);
  assert.equal(clean.unauthorized, false);
});
