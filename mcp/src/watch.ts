#!/usr/bin/env node
/**
 * reelbolt-mcp-watch — follow one workflow execution and print a line when it changes.
 *
 * Built for a Claude Code Monitor: every stdout line is one notification, so stdout carries
 * only short plain lines (no progress bars, no ANSI). Diagnostics go to stderr. The token is
 * never printed.
 *
 *   node dist/watch.js <executionId> [--project <projectId>] [--max-minutes 120]
 */
import { realpathSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { createApiClient, ReelBoltApiError, type ReelBoltApiClient } from "./apiClient.js";

const MAX_CONSECUTIVE_FAILURES = 5;
const MIN_SLEEP_S = 5;
const MAX_SLEEP_S = 60;
const DEFAULT_MAX_MINUTES = 120;

export interface WatchOptions {
  executionId: string;
  projectId?: string;
  maxMinutes: number;
}

export interface WatchDeps {
  apiClient: ReelBoltApiClient;
  token: string;
  out: (line: string) => void;
  err: (line: string) => void;
  now: () => number;
  sleep: (ms: number) => Promise<void>;
}

export interface Snapshot {
  status: string;
  terminal: boolean;
  stepsTotal: number | null;
  stepsDone: number | null;
  currentStep: string | null;
  error: string | null;
  retryAfterSeconds: number | null;
}

function num(v: unknown): number | null {
  return typeof v === "number" && Number.isFinite(v) ? v : null;
}

/** Parse the MCP text content defensively; null when the shape is not recognised. */
export function parseSnapshot(content: unknown[]): Snapshot | null {
  for (const part of content) {
    if (!part || typeof part !== "object") continue;
    const text = (part as { text?: unknown }).text;
    if (typeof text !== "string") continue;
    let json: unknown;
    try {
      json = JSON.parse(text);
    } catch {
      continue;
    }
    if (!json || typeof json !== "object" || Array.isArray(json)) continue;
    const o = json as Record<string, unknown>;
    if (typeof o.status !== "string" || typeof o.terminal !== "boolean") continue;
    // Real shape (AssistantRunTools.Describe): progress{stepsTotal,stepsDone,currentStep{label}},
    // errorMessage. The flat spellings are tolerated too; they are harmless.
    const p =
      o.progress && typeof o.progress === "object" ? (o.progress as Record<string, unknown>) : o;
    const cur = p.currentStep ?? o.currentStep;
    const label =
      typeof cur === "string"
        ? cur
        : cur && typeof cur === "object" && typeof (cur as { label?: unknown }).label === "string"
          ? (cur as { label: string }).label
          : null;
    const err = typeof o.errorMessage === "string" && o.errorMessage ? o.errorMessage : o.error;
    return {
      status: o.status,
      terminal: o.terminal,
      stepsTotal: num(p.stepsTotal ?? o.stepsTotal),
      stepsDone: num(p.stepsDone ?? o.stepsDone),
      currentStep: label ? label : null,
      error: typeof err === "string" && err ? err : null,
      retryAfterSeconds: num(o.retryAfterSeconds),
    };
  }
  return null;
}

function oneLine(s: string, max: number): string {
  const flat = s.replace(/\s+/g, " ").trim();
  return flat.length > max ? flat.slice(0, max) : flat;
}

function progressLine(s: Snapshot): string {
  let line = s.status.toLowerCase();
  if (s.stepsTotal !== null && s.stepsDone !== null) line += ` ${s.stepsDone}/${s.stepsTotal}`;
  if (s.currentStep) line += ` - ${oneLine(s.currentStep, 80)}`;
  return line;
}

function terminalLine(s: Snapshot): string {
  const status = s.status.toLowerCase();
  if (status.startsWith("pass") || status.startsWith("succe") || status.startsWith("complet")) {
    return "finished: Passed";
  }
  if (status.startsWith("cancel")) return "finished: Cancelled";
  if (status.startsWith("fail")) {
    return s.error ? `finished: Failed - ${oneLine(s.error, 200)}` : "finished: Failed";
  }
  return `finished: ${oneLine(s.status, 40)}`;
}

/** Returns the process exit code. */
export async function watch(opts: WatchOptions, deps: WatchDeps): Promise<number> {
  const deadline = deps.now() + opts.maxMinutes * 60_000;
  const args: Record<string, unknown> = { executionId: opts.executionId };
  const context = opts.projectId ? { projectId: opts.projectId } : undefined;
  let lastKey: string | null = null;
  let failures = 0;
  let warnedShape = false;

  for (;;) {
    let sleepS = MIN_SLEEP_S;
    let failed = false;
    try {
      const res = await deps.apiClient.callTool("GetExecutionStatus", args, context, deps.token);
      const snap = parseSnapshot(res.content);
      if (!snap) {
        failed = true;
        if (!warnedShape) {
          warnedShape = true;
          deps.err("unexpected answer");
        }
      } else {
        failures = 0;
        if (snap.terminal) {
          deps.out(terminalLine(snap));
          return 0;
        }
        const key = `${snap.status}|${snap.stepsDone}|${snap.currentStep}`;
        if (key !== lastKey) {
          lastKey = key;
          deps.out(progressLine(snap));
        }
        sleepS = snap.retryAfterSeconds ?? MIN_SLEEP_S;
      }
    } catch (e) {
      if (e instanceof ReelBoltApiError && (e.status === 401 || e.status === 403)) {
        deps.err("authentication failed: check REELBOLT_MCP_TOKEN");
        return 1;
      }
      failed = true;
      deps.err(`transient error: ${e instanceof Error ? oneLine(e.message, 200) : "unknown"}`);
    }

    if (failed) {
      failures += 1;
      if (failures >= MAX_CONSECUTIVE_FAILURES) {
        deps.out("lost contact with ReelBolt");
        return 1;
      }
      // Exponential backoff on failures: 5, 10, 20, 40 s (capped).
      sleepS = Math.min(MAX_SLEEP_S, MIN_SLEEP_S * 2 ** (failures - 1));
    }

    sleepS = Math.min(MAX_SLEEP_S, Math.max(MIN_SLEEP_S, sleepS));
    if (deps.now() + sleepS * 1000 > deadline) {
      deps.out(`still running after ${opts.maxMinutes} minutes`);
      return 1;
    }
    await deps.sleep(sleepS * 1000);
  }
}

const USAGE = "usage: reelbolt-mcp-watch <executionId> [--project <projectId>] [--max-minutes 120]";

/** Returns options, or null on a usage error. */
export function parseArgs(argv: string[]): WatchOptions | null {
  let executionId: string | undefined;
  let projectId: string | undefined;
  let maxMinutes = DEFAULT_MAX_MINUTES;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--project") {
      projectId = argv[++i];
      if (!projectId) return null;
    } else if (a === "--max-minutes") {
      const n = Number(argv[++i]);
      if (!Number.isFinite(n) || n <= 0) return null;
      maxMinutes = n;
    } else if (a.startsWith("--") || executionId !== undefined) {
      return null;
    } else {
      executionId = a;
    }
  }
  return executionId ? { executionId, projectId, maxMinutes } : null;
}

async function main(): Promise<void> {
  const opts = parseArgs(process.argv.slice(2));
  if (!opts) {
    console.error(USAGE);
    process.exit(2);
  }
  const apiBaseUrl = process.env.INFERENCE_API_URL;
  if (!apiBaseUrl) {
    console.error(
      "FATAL: INFERENCE_API_URL is not set. Set it to the Inference API base URL " +
        "(e.g. http://inference:8080 in the compose deployment, or the operator's " +
        "host URL for the Claude Code plugin)."
    );
    process.exit(1);
  }
  const token = process.env.REELBOLT_MCP_TOKEN;
  if (!token) {
    console.error(
      "FATAL: REELBOLT_MCP_TOKEN is not set. The watcher needs the caller's ReelBolt " +
        "bearer token. (It is forwarded verbatim to the Inference API and never logged.)"
    );
    process.exit(1);
  }
  const code = await watch(opts, {
    apiClient: createApiClient({ apiBaseUrl }),
    token,
    out: (l) => process.stdout.write(`${l}\n`),
    err: (l) => process.stderr.write(`${l}\n`),
    now: () => Date.now(),
    sleep: (ms) => new Promise((r) => setTimeout(r, ms)),
  });
  process.exit(code);
}

// npm links the bin through a symlink, so compare real paths, not the argv spelling.
function isEntryPoint(): boolean {
  const arg = process.argv[1];
  if (!arg) return false;
  try {
    return import.meta.url === pathToFileURL(realpathSync(arg)).href;
  } catch {
    return false;
  }
}

if (isEntryPoint()) {
  void main();
}
