/**
 * The server entry point end to end: spawn the REAL built server (`dist/server.js`) as a
 * child process and talk to it over stdio and over HTTP.
 *
 * The point of these tests is what only the real entry point can prove:
 *   - stdio must keep starting and working with `MCP_PUBLIC_URL` and `OAUTH_ISSUER_URL`
 *     unset — no OAuth configuration, no metadata, just the token it always took;
 *   - the HTTP transport must fail closed when those two are missing;
 *   - and with them set, the environment (never a constant) decides what the metadata
 *     document says and what the challenge points at (docs/design/mcp-oauth.md §7).
 */
import { strict as assert } from "node:assert";
import { spawn, type ChildProcess } from "node:child_process";
import * as http from "node:http";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const SERVER_PATH = fileURLToPath(new URL("./server.js", import.meta.url));

type AnyMessage = Record<string, unknown>;

/* ---------------------------------------------------------------------------
 * A fake Inference API, so the child's tools/list has something to proxy
 * ------------------------------------------------------------------------- */

interface FakeApi {
  baseUrl: string;
  authorizations: (string | undefined)[];
  close: () => Promise<void>;
}

async function startFakeInferenceApi(): Promise<FakeApi> {
  const authorizations: (string | undefined)[] = [];
  const server = http.createServer((req, res) => {
    authorizations.push(req.headers.authorization);
    if (req.url === "/api/v1/assistant/tool-catalog") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(
        JSON.stringify({
          tools: [
            {
              name: "ListProjects",
              description: "List the caller's projects",
              title: "List projects",
              annotations: {
                title: "List projects",
                readOnlyHint: true,
                destructiveHint: false,
                idempotentHint: true,
                openWorldHint: false,
              },
              inputSchema: { type: "object", properties: {} },
            },
          ],
        })
      );
      return;
    }
    res.writeHead(404, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "not found" }));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  return {
    baseUrl: `http://127.0.0.1:${address.port}`,
    authorizations,
    close: async () => {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

/* ---------------------------------------------------------------------------
 * Driving the child over stdio
 * ------------------------------------------------------------------------- */

interface StdioChild {
  send(message: unknown): void;
  waitForMessage(id: number, what: string): Promise<AnyMessage>;
  /** Wait until stderr matches, returning the captured groups; throws on timeout. */
  waitForStderr(pattern: RegExp): Promise<RegExpMatchArray>;
  stderrText(): string;
  waitForExit(): Promise<number | null>;
  endInput(): void;
  kill(): void;
}

function spawnServer(env: NodeJS.ProcessEnv): StdioChild {
  const child: ChildProcess = spawn(process.execPath, [SERVER_PATH], {
    env,
    stdio: ["pipe", "pipe", "pipe"],
  });
  const { stdout, stderr, stdin } = child;
  assert.ok(stdout && stderr && stdin, "the child must have all three standard streams");

  const messages: AnyMessage[] = [];
  let buffer = "";
  let stderrText = "";
  stdout.setEncoding("utf8");
  stderr.setEncoding("utf8");
  stdout.on("data", (chunk: string) => {
    buffer += chunk;
    for (let index = buffer.indexOf("\n"); index >= 0; index = buffer.indexOf("\n")) {
      const line = buffer.slice(0, index).trim();
      buffer = buffer.slice(index + 1);
      if (line) {
        messages.push(JSON.parse(line) as AnyMessage);
      }
    }
  });
  stderr.on("data", (chunk: string) => {
    stderrText += chunk;
  });

  async function waitFor<T>(produce: () => T | undefined): Promise<T | undefined> {
    const deadline = Date.now() + 10_000;
    for (;;) {
      const found = produce();
      if (found !== undefined) return found;
      if (Date.now() > deadline) return undefined;
      await new Promise((resolve) => setTimeout(resolve, 20));
    }
  }

  return {
    send(message) {
      stdin.write(`${JSON.stringify(message)}\n`);
    },
    async waitForMessage(id, what) {
      const found = await waitFor(() => messages.find((message) => message.id === id));
      assert.ok(found, `timed out waiting for ${what}; stderr was: ${stderrText}`);
      return found;
    },
    async waitForStderr(pattern) {
      const match = await waitFor(() => stderrText.match(pattern) ?? undefined);
      assert.ok(match, `timed out waiting for ${pattern}; stderr was: ${stderrText}`);
      return match;
    },
    stderrText: () => stderrText,
    waitForExit() {
      return new Promise<number | null>((resolve, reject) => {
        const timer = setTimeout(
          () => reject(new Error(`child did not exit; stderr was: ${stderrText}`)),
          10_000
        );
        child.once("exit", (code) => {
          clearTimeout(timer);
          resolve(code);
        });
      });
    },
    endInput() {
      stdin.end();
    },
    kill() {
      child.kill("SIGKILL");
    },
  };
}

/* ---------------------------------------------------------------------------
 * Tests
 * ------------------------------------------------------------------------- */

test("stdio starts and works with MCP_PUBLIC_URL and OAUTH_ISSUER_URL unset", async (t) => {
  const api = await startFakeInferenceApi();
  t.after(api.close);

  const server = spawnServer({
    // Deliberately minimal: neither OAuth variable is here, and nothing else is either.
    PATH: process.env.PATH,
    INFERENCE_API_URL: api.baseUrl,
    REELBOLT_MCP_TOKEN: "stdio-token",
    MCP_TRANSPORT: "stdio",
  });
  t.after(() => server.kill());

  server.send({
    jsonrpc: "2.0",
    id: 1,
    method: "initialize",
    params: {
      protocolVersion: "2025-06-18",
      capabilities: {},
      clientInfo: { name: "reelbolt-test", version: "0.0.0" },
    },
  });
  const initialized = await server.waitForMessage(1, "initialize");
  const serverInfo = (initialized.result as { serverInfo?: { name?: string } }).serverInfo;
  assert.equal(serverInfo?.name, "reelbolt");
  assert.match(server.stderrText(), /ReelBolt MCP server ready on stdio/);

  server.send({ jsonrpc: "2.0", method: "notifications/initialized" });
  server.send({ jsonrpc: "2.0", id: 2, method: "tools/list" });

  const listed = await server.waitForMessage(2, "tools/list");
  const tools = (
    listed.result as {
      tools: Array<{
        name: string;
        title?: string;
        annotations?: Record<string, unknown>;
        inputSchema: Record<string, unknown>;
      }>;
    }
  ).tools;
  assert.equal(tools.length, 1);
  assert.equal(tools[0].name, "ListProjects");
  // The catalog's directory metadata is forwarded verbatim, not dropped and not renamed.
  assert.equal(tools[0].title, "List projects");
  assert.deepEqual(tools[0].annotations, {
    title: "List projects",
    readOnlyHint: true,
    destructiveHint: false,
    idempotentHint: true,
    openWorldHint: false,
  });
  // The bridge still injects its one optional context wrapper.
  assert.ok((tools[0].inputSchema.properties as Record<string, unknown>).context);
  // The token still comes from REELBOLT_MCP_TOKEN and is forwarded verbatim (contract §9).
  assert.deepEqual(api.authorizations, ["Bearer stdio-token"]);

  server.endInput();
  assert.equal(await server.waitForExit(), 0);
});

test("stdio without REELBOLT_MCP_TOKEN still fails closed with the original message", async (t) => {
  const server = spawnServer({
    PATH: process.env.PATH,
    INFERENCE_API_URL: "http://127.0.0.1:1",
    MCP_TRANSPORT: "stdio",
  });
  t.after(() => server.kill());

  assert.equal(await server.waitForExit(), 1);
  assert.match(server.stderrText(), /REELBOLT_MCP_TOKEN is not set/);
});

test("http without MCP_PUBLIC_URL fails closed with a clear message", async (t) => {
  const server = spawnServer({
    PATH: process.env.PATH,
    INFERENCE_API_URL: "http://127.0.0.1:1",
    MCP_TRANSPORT: "http",
    OAUTH_ISSUER_URL: "https://reelbolt.example.com",
    PORT: "0",
  });
  t.after(() => server.kill());

  assert.equal(await server.waitForExit(), 1);
  assert.match(server.stderrText(), /FATAL: MCP_PUBLIC_URL is not set/);
  assert.match(server.stderrText(), /stdio transport does not need it/);
});

test("http without OAUTH_ISSUER_URL fails closed with a clear message", async (t) => {
  const server = spawnServer({
    PATH: process.env.PATH,
    INFERENCE_API_URL: "http://127.0.0.1:1",
    MCP_TRANSPORT: "http",
    MCP_PUBLIC_URL: "https://reelbolt.example.com/mcp",
    PORT: "0",
  });
  t.after(() => server.kill());

  assert.equal(await server.waitForExit(), 1);
  assert.match(server.stderrText(), /FATAL: OAUTH_ISSUER_URL is not set/);
});

test("the http transport serves the metadata and the challenge from the environment", async (t) => {
  const server = spawnServer({
    PATH: process.env.PATH,
    INFERENCE_API_URL: "http://127.0.0.1:1",
    MCP_TRANSPORT: "http",
    MCP_PUBLIC_URL: "https://reelbolt.example.com/mcp",
    OAUTH_ISSUER_URL: "https://reelbolt.example.com",
    PORT: "0",
  });
  t.after(() => server.kill());

  const [, port] = await server.waitForStderr(/listening on port (\d+) \(streamable HTTP\)/);
  const baseUrl = `http://127.0.0.1:${port}`;

  const metadata = await fetch(`${baseUrl}/.well-known/oauth-protected-resource`);
  assert.equal(metadata.status, 200);
  const document = {
    resource: "https://reelbolt.example.com/mcp",
    authorization_servers: ["https://reelbolt.example.com"],
    scopes_supported: ["assistant.tools", "assistant.admin"],
    bearer_methods_supported: ["header"],
  };
  assert.deepEqual(await metadata.json(), document);

  // The URL a strict client derives instead of following the challenge's resource_metadata
  // link: RFC 9728 §3 inserts the well-known string before the resource's path components.
  const derived = await fetch(
    `${baseUrl}/.well-known/oauth-protected-resource/mcp`
  );
  assert.equal(derived.status, 200, "the RFC 9728 path-suffixed URL must answer too");
  assert.deepEqual(await derived.json(), document);

  const challenged = await fetch(`${baseUrl}/mcp`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize" }),
  });
  assert.equal(challenged.status, 401);
  assert.equal(
    challenged.headers.get("www-authenticate"),
    'Bearer resource_metadata="https://reelbolt.example.com/.well-known/oauth-protected-resource", ' +
      'scope="assistant.tools"'
  );
  assert.equal(await challenged.text(), "");
});
