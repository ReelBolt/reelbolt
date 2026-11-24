import { test } from "node:test";
import { spawnSync } from "node:child_process";
import { mkdtempSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { parseArgs, watch, type WatchDeps } from "./watch.js";
import { ReelBoltApiError, type CallResult } from "./apiClient.js";

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

function harness(steps: Step[], maxMinutes = 120) {
  const out: string[] = [];
  const err: string[] = [];
  const sleeps: number[] = [];
  let t = 0;
  let i = 0;
  const calls: unknown[][] = [];
  const deps: WatchDeps = {
    apiClient: {
      catalog: async () => ({ tools: [] }),
      callTool: async (...a) => {
        calls.push(a);
        const s = steps[Math.min(i++, steps.length - 1)];
        if (s instanceof Error) throw s;
        return s;
      },
    },
    token: "secret-token",
    out: (l) => out.push(l),
    err: (l) => err.push(l),
    now: () => t,
    sleep: async (ms) => {
      sleeps.push(ms);
      t += ms;
    },
  };
  return { deps, out, err, sleeps, calls, run: () => watch({ executionId: "e1", projectId: "p1", maxMinutes }, deps) };
}

test("terminal at once", async () => {
  const h = harness([snap({ status: "Passed", terminal: true })]);
  assert.equal(await h.run(), 0);
  assert.deepEqual(h.out, ["finished: Passed"]);
  assert.deepEqual(h.calls[0].slice(0, 3), ["GetExecutionStatus", { executionId: "e1" }, { projectId: "p1" }]);
});

test("running -> running -> passed, no duplicate lines", async () => {
  const h = harness([
    snap({ status: "Running", stepsDone: 3, stepsTotal: 9, currentStep: "Render" }),
    snap({ status: "Running", stepsDone: 3, stepsTotal: 9, currentStep: "Render" }),
    snap({ status: "Running", stepsDone: 4, stepsTotal: 9, currentStep: "Mix" }),
    snap({ status: "Passed", terminal: true }),
  ]);
  assert.equal(await h.run(), 0);
  assert.deepEqual(h.out, ["running 3/9 - Render", "running 4/9 - Mix", "finished: Passed"]);
  assert.deepEqual(h.sleeps, [10000, 10000, 10000]);
});

test("failed with long error is truncated to 200 chars", async () => {
  const h = harness([snap({ status: "Failed", terminal: true, error: "x".repeat(500) })]);
  assert.equal(await h.run(), 0);
  assert.equal(h.out[0], `finished: Failed - ${"x".repeat(200)}`);
});

test("cancelled exits 0", async () => {
  const h = harness([snap({ status: "Cancelled", terminal: true })]);
  assert.equal(await h.run(), 0);
  assert.deepEqual(h.out, ["finished: Cancelled"]);
});

test("401 exits 1 without leaking the token", async () => {
  const h = harness([new ReelBoltApiError("nope", 401)]);
  assert.equal(await h.run(), 1);
  assert.deepEqual(h.out, []);
  assert.ok(!h.err.join("\n").includes("secret-token"));
});

test("transient errors recover; 5 in a row lose contact", async () => {
  const h = harness([new Error("down"), new Error("down"), snap({ status: "Passed", terminal: true })]);
  assert.equal(await h.run(), 0);
  assert.deepEqual(h.out, ["finished: Passed"]);
  const bad = harness([new Error("down")]);
  assert.equal(await bad.run(), 1);
  assert.deepEqual(bad.out, ["lost contact with ReelBolt"]);
});

test("unexpected shape warns once on stderr", async () => {
  const h = harness([{ content: [{ type: "text", text: "hello" }] }]);
  assert.equal(await h.run(), 1);
  assert.equal(h.err.filter((l) => l === "unexpected answer").length, 1);
});

test("sleep is clamped to 5..60", async () => {
  const h = harness([
    snap({ status: "Running", retryAfterSeconds: 1 }),
    snap({ status: "Running", retryAfterSeconds: 500 }),
    snap({ status: "Passed", terminal: true }),
  ]);
  await h.run();
  assert.deepEqual(h.sleeps, [5000, 60000]);
});

test("max time passes", async () => {
  const h = harness([snap({ status: "Running", retryAfterSeconds: 60 })], 2);
  assert.equal(await h.run(), 1);
  assert.equal(h.out.at(-1), "still running after 2 minutes");
});

test("parseArgs", () => {
  assert.deepEqual(parseArgs(["e", "--project", "p", "--max-minutes", "5"]), {
    executionId: "e",
    projectId: "p",
    maxMinutes: 5,
  });
  assert.equal(parseArgs([]), null);
  assert.equal(parseArgs(["e", "--bogus"]), null);
  assert.equal(parseArgs(["e", "--max-minutes", "x"]), null);
});

test("the bin runs through a symlink (not a silent no-op)", () => {
  const dir = mkdtempSync(join(tmpdir(), "watchbin-"));
  const link = join(dir, "reelbolt-mcp-watch");
  symlinkSync(fileURLToPath(new URL("./watch.js", import.meta.url)), link);
  const env = { ...process.env };
  delete env.INFERENCE_API_URL;
  const r = spawnSync(process.execPath, [link, "e1"], { env, encoding: "utf8" });
  assert.equal(r.status, 1);
  assert.match(r.stderr, /FATAL: INFERENCE_API_URL is not set/);
});
