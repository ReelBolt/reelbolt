import { strict as assert } from "node:assert";
import { test } from "node:test";
import { createApiClient, ReelBoltApiError } from "./apiClient.js";

/**
 * A minimal fetch double: records (url, init), returns canned responses, and can
 * throw to simulate an unreachable API. `createApiClient` is the code under test;
 * only the network is stubbed, so the real error-mapping logic runs.
 */
interface StubResponseSpec {
  status?: number;
  ok?: boolean;
  statusText?: string;
  body?: string | object;
}

function makeFetch(
  specs: StubResponseSpec[] | StubResponseSpec
): { fn: typeof fetch; calls: Array<{ url: string; init: RequestInit }> } {
  let i = 0;
  const calls: Array<{ url: string; init: RequestInit }> = [];
  const fn = (async (input: string | URL | Request, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    calls.push({ url, init: init ?? {} });
    const spec = Array.isArray(specs) ? specs[Math.min(i, specs.length - 1)] : specs;
    i += 1;
    const bodyText =
      spec.body === undefined
        ? ""
        : typeof spec.body === "string"
          ? spec.body
          : JSON.stringify(spec.body);
    const status = spec.status ?? 200;
    const ok = spec.ok ?? (status >= 200 && status < 300);
    return {
      status,
      statusText: spec.statusText ?? "OK",
      ok,
      text: async () => bodyText,
    } as unknown as Response;
  }) as typeof fetch;
  return { fn, calls };
}

const base = { apiBaseUrl: "http://inference.test" };

test("catalog: 200 -> returns { tools } and forwards the bearer token verbatim", async () => {
  const { fn, calls } = makeFetch({ body: { tools: [{ name: "A" }] } });
  const client = createApiClient({ ...base, fetchImpl: fn });
  const res = await client.catalog("tok-abc");
  assert.deepEqual(res.tools, [{ name: "A" }]);
  assert.equal(calls[0].url, "http://inference.test/api/v1/assistant/tool-catalog");
  assert.equal(calls[0].init.method, "GET");
  assert.equal((calls[0].init.headers as Record<string, string>)["Authorization"], "Bearer tok-abc");
  // A bodyless GET must NOT claim a content type.
  assert.equal((calls[0].init.headers as Record<string, string>)["Content-Type"], undefined);
});

test("catalog: missing token -> no Authorization header", async () => {
  const { fn, calls } = makeFetch({ body: { tools: [] } });
  const client = createApiClient({ ...base, fetchImpl: fn });
  await client.catalog(undefined);
  assert.equal((calls[0].init.headers as Record<string, string>)["Authorization"], undefined);
});

test("catalog: non-2xx -> throws ReelBoltApiError with the status (never a crash)", async () => {
  const { fn } = makeFetch({ status: 401, statusText: "Unauthorized", body: { error: "bad token" } });
  const client = createApiClient({ ...base, fetchImpl: fn });
  await assert.rejects(client.catalog("tok"), (err: unknown) => {
    assert.ok(err instanceof ReelBoltApiError);
    assert.equal(err.status, 401);
    return true;
  });
});

test("catalog: unreachable API -> throws (fetch rejects), never a crash", async () => {
  const { fn } = makeFetch({} as never);
  const throwing = (async () => {
    throw new Error("connect ECONNREFUSED");
  }) as typeof fetch;
  const client = createApiClient({ ...base, fetchImpl: throwing });
  await assert.rejects(client.catalog("tok"), (err: unknown) => {
    assert.ok(err instanceof ReelBoltApiError);
    assert.match(err.message, /unreachable|ECONNREFUSED/i);
    return true;
  });
  void fn;
});

test("catalog: malformed JSON -> throws", async () => {
  const { fn } = makeFetch({ body: "{ this is not json" });
  const client = createApiClient({ ...base, fetchImpl: fn });
  await assert.rejects(client.catalog("tok"), (err: unknown) => {
    assert.ok(err instanceof ReelBoltApiError);
    assert.match(err.message, /malformed/i);
    return true;
  });
});

test("catalog: valid JSON but wrong shape (no tools array) -> throws", async () => {
  const { fn } = makeFetch({ body: { notTools: true } });
  const client = createApiClient({ ...base, fetchImpl: fn });
  await assert.rejects(client.catalog("tok"), (err: unknown) => {
    assert.ok(err instanceof ReelBoltApiError);
    return true;
  });
});

test("callTool: 200 with a tool-level refusal (isError:false, own text) passes through untouched", async () => {
  const { fn, calls } = makeFetch({
    body: { content: [{ type: "text", text: '{"error":"plan gate: upgrade required"}' }], isError: false },
  });
  const client = createApiClient({ ...base, fetchImpl: fn });
  const res = await client.callTool(
    "ExecuteWorkflow",
    { projectId: "p-1", regenerateVideoClips: false },
    { projectId: "p-1" },
    "tok"
  );
  // A refusal is a RESULT: forwarded verbatim, isError preserved (false here).
  assert.equal(res.isError, false);
  assert.deepEqual(res.content, [{ type: "text", text: '{"error":"plan gate: upgrade required"}' }]);
  // Assert the forwarded request BODY (not just the URL), per lead guidance.
  const sentBody = JSON.parse(calls[0].init.body as string);
  assert.deepEqual(sentBody, {
    name: "ExecuteWorkflow",
    arguments: { projectId: "p-1", regenerateVideoClips: false },
    context: { projectId: "p-1" },
  });
  assert.equal(calls[0].url, "http://inference.test/api/v1/assistant/tool-call");
  assert.equal(calls[0].init.method, "POST");
  assert.equal((calls[0].init.headers as Record<string, string>)["Authorization"], "Bearer tok");
  // REGRESSION GUARD (independent verification finding, blocker): a POST with a body that does
  // not declare Content-Type makes fetch send `text/plain;charset=UTF-8`, and the API's
  // [ApiController] + [FromBody] answers 415 Unsupported Media Type — so every tools/call failed
  // while tools/list (a GET) worked. Assert the header, not just the URL and body.
  assert.equal(
    (calls[0].init.headers as Record<string, string>)["Content-Type"],
    "application/json",
    "a JSON body must declare Content-Type or the API rejects the call with 415"
  );
});

test("callTool: forwards arguments verbatim (no reshape/rename) and omits context when absent", async () => {
  const { fn, calls } = makeFetch({ body: { content: [{ type: "text", text: "ok" }] } });
  const client = createApiClient({ ...base, fetchImpl: fn });
  await client.callTool("GetProject", { projectId: "p-1", verbose: true }, undefined, "tok");
  const sentBody = JSON.parse(calls[0].init.body as string);
  assert.deepEqual(sentBody, { name: "GetProject", arguments: { projectId: "p-1", verbose: true } });
  assert.equal("context" in sentBody, false, "context must be omitted when not supplied");
});

test("callTool: non-2xx (404 unknown tool) -> throws with status, never a crash", async () => {
  const { fn } = makeFetch({ status: 404, statusText: "Not Found", body: { error: "unknown tool: NoSuchTool" } });
  const client = createApiClient({ ...base, fetchImpl: fn });
  await assert.rejects(client.callTool("NoSuchTool", {}, undefined, "tok"), (err: unknown) => {
    assert.ok(err instanceof ReelBoltApiError);
    assert.equal(err.status, 404);
    return true;
  });
});

test("callTool: 200 with isError:true (execution failure) is forwarded, not thrown away", async () => {
  const { fn } = makeFetch({
    body: { content: [{ type: "text", text: "execution failed" }], isError: true },
  });
  const client = createApiClient({ ...base, fetchImpl: fn });
  const res = await client.callTool("ExecuteWorkflow", { projectId: "p-1" }, undefined, "tok");
  assert.equal(res.isError, true);
});

test("callTool: unreachable API -> throws, never a crash", async () => {
  const throwing = (async () => {
    throw new Error("ECONNRESET");
  }) as typeof fetch;
  const client = createApiClient({ ...base, fetchImpl: throwing });
  await assert.rejects(client.callTool("GetProject", {}, undefined, "tok"), (err: unknown) => {
    assert.ok(err instanceof ReelBoltApiError);
    return true;
  });
});

test("callTool: malformed JSON -> throws", async () => {
  const { fn } = makeFetch({ body: "not-json{{" });
  const client = createApiClient({ ...base, fetchImpl: fn });
  await assert.rejects(client.callTool("GetProject", {}, undefined, "tok"), (err: unknown) => {
    assert.ok(err instanceof ReelBoltApiError);
    assert.match(err.message, /malformed/i);
    return true;
  });
});
