import { test, mock } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import {
  ProgressNotificationSchema,
  ResourceUpdatedNotificationSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { makeProxyServer } from "./bridge.js";
import { ReelBoltApiError, type CallResult, type ReelBoltApiClient } from "./apiClient.js";
import { parseRunUri, principalOf, type SubscriptionCounter } from "./runSubscriptions.js";

type Step = CallResult | Error;
const snap = (o: Record<string, unknown>): CallResult => {
  const { stepsDone, stepsTotal, currentStep, error, ...rest } = o as Record<string, unknown>;
  const real = {
    executionId: "x",
    terminal: false,
    retryAfterSeconds: 10,
    hint: "h",
    creditsCharged: 0,
    billingEnabled: false,
    outputs: [],
    ...rest,
    progress: {
      stepsTotal: stepsTotal ?? null,
      stepsDone: stepsDone ?? 0,
      currentStep: currentStep ? { stepOrder: 1, label: currentStep, stepType: "X" } : null,
    },
    errorMessage: error ?? null,
  };
  return { content: [{ type: "text", text: JSON.stringify(real) }] };
};
const running = (done: number, step = "Render") =>
  snap({ status: "Running", stepsDone: done, stepsTotal: 9, currentStep: step });
const passed = snap({ status: "Passed", terminal: true, stepsDone: 9, stepsTotal: 9 });
const URI = "reelbolt://runs/00000000-0000-4000-8000-000000000001";
const TOKEN = "secret-token-xyz";

async function setup(steps: Step[], limits = { perSession: 5, perPrincipal: 10, processWide: 100, ttlMs: 2 * 3600_000 }, counter: SubscriptionCounter = { count: 0, byPrincipal: new Map() }, token = TOKEN) {
  let i = 0;
  const tokens: Array<string | undefined> = [];
  const api: ReelBoltApiClient = {
    catalog: async () => ({ tools: [] }),
    callTool: async (_n, _a, _c, token) => {
      tokens.push(token);
      const s = steps[Math.min(i++, steps.length - 1)];
      if (s instanceof Error) throw s;
      return s;
    },
  };
  const server = makeProxyServer({
    apiClient: api,
    getAuthToken: () => token,
    subscriptionLimits: limits,
    subscriptionCounter: counter,
  });
  const client = new Client({ name: "t", version: "1" }, { capabilities: {} });
  const updated: string[] = [];
  const progress: Array<Record<string, unknown>> = [];
  client.setNotificationHandler(ResourceUpdatedNotificationSchema, async (n) => void updated.push(n.params.uri));
  client.setNotificationHandler(ProgressNotificationSchema, async (n) => void progress.push(n.params));
  const [a, b] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(a), client.connect(b)]);
  const flush = async () => {
    for (let k = 0; k < 20; k++) await new Promise<void>((r) => setImmediate(r));
  };
  return { client, server, updated, progress, tokens, counter, calls: () => i, flush };
}

async function tick(ctx: { flush: () => Promise<void> }, ms: number) {
  mock.timers.tick(ms);
  await ctx.flush();
}

test("capability, template list, empty list", async () => {
  const c = await setup([passed]);
  const caps = c.client.getServerCapabilities();
  assert.deepEqual(caps?.resources, { subscribe: true, listChanged: false });
  const t = await c.client.listResourceTemplates();
  assert.equal(t.resourceTemplates[0].uriTemplate, "reelbolt://runs/{executionId}");
  assert.deepEqual((await c.client.listResources()).resources, []);
  assert.deepEqual(parseRunUri("reelbolt://runs/{00000000-0000-4000-8000-00000000000A}?project=p"), { executionId: "00000000-0000-4000-8000-00000000000a", projectId: "p" });
  assert.equal(parseRunUri("http://x"), null);
});

test("read ok returns json; read error is readable and leaks no token", async () => {
  const ok = await setup([running(1)]);
  const r = await ok.client.readResource({ uri: URI });
  assert.equal(r.contents[0].mimeType, "application/json");
  assert.equal(JSON.parse((r.contents[0] as { text: string }).text).status, "Running");
  const bad = await setup([new ReelBoltApiError(`boom ${TOKEN}`, 404)]);
  await assert.rejects(bad.client.readResource({ uri: URI }), (e: Error) => {
    assert.ok(!e.message.includes(TOKEN));
    assert.match(e.message, /GetExecutionStatus/);
    return true;
  });
});

test("subscribe on a terminal run sends one updated and registers nothing", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([passed]);
    await c.client.subscribeResource({ uri: URI });
    await c.flush();
    assert.deepEqual(c.updated, [URI]);
    assert.equal(c.counter.count, 0);
  } finally {
    mock.timers.reset();
  }
});

test("changes send updated + progress, no duplicate when unchanged, final on terminal", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1), running(1), running(2, "Mix"), passed]);
    await c.client.subscribeResource({ uri: URI, _meta: { progressToken: "pt" } });
    await c.flush();
    assert.deepEqual(c.updated, []);
    await tick(c, 10_000); // same -> nothing
    assert.deepEqual(c.updated, []);
    await tick(c, 10_000); // changed
    assert.deepEqual(c.updated, [URI]);
    assert.deepEqual(c.progress[0], { progressToken: "pt", progress: 2, total: 9, message: "Mix" });
    await tick(c, 10_000); // terminal
    assert.equal(c.updated.length, 2);
    assert.equal(c.counter.count, 0);
    await tick(c, 60_000);
    assert.equal(c.updated.length, 2);
    assert.ok(c.tokens.every((t) => t === TOKEN));
  } finally {
    mock.timers.reset();
  }
});

test("caps: per session and process wide", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1)], { perSession: 2, perPrincipal: 10, processWide: 100, ttlMs: 1e9 });
    await c.client.subscribeResource({ uri: "reelbolt://runs/00000000-0000-4000-8000-000000000002" });
    await c.client.subscribeResource({ uri: "reelbolt://runs/00000000-0000-4000-8000-000000000003" });
    await assert.rejects(c.client.subscribeResource({ uri: "reelbolt://runs/00000000-0000-4000-8000-000000000004" }), /GetExecutionStatus/);
    const d = await setup([running(1)], { perSession: 5, perPrincipal: 10, processWide: 1, ttlMs: 1e9 }, { count: 1, byPrincipal: new Map() });
    await assert.rejects(d.client.subscribeResource({ uri: "reelbolt://runs/00000000-0000-4000-8000-000000000002" }), /GetExecutionStatus/);
  } finally {
    mock.timers.reset();
  }
});

test("TTL drops the subscription and stops polling", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1)], { perSession: 5, perPrincipal: 10, processWide: 100, ttlMs: 30_000 });
    await c.client.subscribeResource({ uri: URI });
    await tick(c, 30_000);
    assert.equal(c.counter.count, 0);
    const n = c.calls();
    await tick(c, 120_000);
    assert.equal(c.calls(), n);
  } finally {
    mock.timers.reset();
  }
});

test("session close clears subscriptions and timers", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1)]);
    await c.client.subscribeResource({ uri: URI });
    assert.equal(c.counter.count, 1);
    await c.server.close();
    assert.equal(c.counter.count, 0);
    const n = c.calls();
    await tick(c, 120_000);
    assert.equal(c.calls(), n);
  } finally {
    mock.timers.reset();
  }
});

test("401 on a poll sends one final updated and drops", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1), new ReelBoltApiError("no", 401)]);
    await c.client.subscribeResource({ uri: URI });
    await tick(c, 10_000);
    assert.deepEqual(c.updated, [URI]);
    assert.equal(c.counter.count, 0);
  } finally {
    mock.timers.reset();
  }
});

test("5 consecutive failures send a final updated and drop", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1), new Error("down")]);
    await c.client.subscribeResource({ uri: URI });
    for (let k = 0; k < 5; k++) await tick(c, 60_000);
    assert.deepEqual(c.updated, [URI]);
    assert.equal(c.counter.count, 0);
  } finally {
    mock.timers.reset();
  }
});

test("unsubscribe stops polling", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1)]);
    await c.client.subscribeResource({ uri: URI });
    await c.client.unsubscribeResource({ uri: URI });
    assert.equal(c.counter.count, 0);
    const n = c.calls();
    await tick(c, 120_000);
    assert.equal(c.calls(), n);
  } finally {
    mock.timers.reset();
  }
});

test("missing run: subscribe succeeds, nothing registered, nothing sent", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([new ReelBoltApiError("not found", 404)]);
    await c.client.subscribeResource({ uri: URI });
    await c.flush();
    assert.equal(c.counter.count, 0);
    assert.deepEqual(c.updated, []);
  } finally {
    mock.timers.reset();
  }
});

const L = { perSession: 50, perPrincipal: 2, processWide: 4, ttlMs: 1e9 };
const g = (n: number) => `reelbolt://runs/00000000-0000-4000-8000-0000000001${String(n).padStart(2, "0")}`;

test("principal A exhausts its cap while B can still subscribe; many sessions of A cannot starve B", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const shared: SubscriptionCounter = { count: 0, byPrincipal: new Map() };
    const a1 = await setup([running(1)], L, shared, "tokenA");
    const a2 = await setup([running(1)], L, shared, "tokenA");
    await a1.client.subscribeResource({ uri: g(1) });
    await a2.client.subscribeResource({ uri: g(2) });
    await assert.rejects(a1.client.subscribeResource({ uri: g(3) }), (e: Error) => {
      assert.ok(!e.message.includes("tokenA"));
      return true;
    });
    const b = await setup([running(1)], L, shared, "tokenB");
    await b.client.subscribeResource({ uri: g(4) });
    assert.equal(shared.count, 3);
    assert.equal(shared.byPrincipal?.get(principalOf("tokenA")), 2);
    assert.ok(![...(shared.byPrincipal?.keys() ?? [])].some((k) => k.includes("token")));
  } finally {
    mock.timers.reset();
  }
});

test("URI variants of the same run count once", async () => {
  mock.timers.enable({ apis: ["setTimeout"] });
  try {
    const c = await setup([running(1)], L);
    const id = "00000000-0000-4000-8000-00000000ABCD";
    await c.client.subscribeResource({ uri: `reelbolt://runs/${id}` });
    await c.client.subscribeResource({ uri: `reelbolt://runs/${id.toLowerCase()}?project=p` });
    await c.client.subscribeResource({ uri: `reelbolt://runs/{${id}}` });
    await c.client.subscribeResource({ uri: `reelbolt://runs/${id.replace(/-/g, "")}` });
    assert.equal(c.counter.count, 1);
    await assert.rejects(c.client.subscribeResource({ uri: "reelbolt://runs/not-a-guid" }), /expected/);
  } finally {
    mock.timers.reset();
  }
});
