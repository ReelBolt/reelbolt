/**
 * Native MCP run notifications: `reelbolt://runs/{executionId}[?project={projectId}]`.
 *
 * Stateless toward authority: every status read is a `GetExecutionStatus` tool call to the
 * Inference API made with the subscriber's bearer token; this module never decides who may
 * see a run. The only state is the in-memory subscription (uri, token, timers) of a live MCP
 * session. The token is held only for the subscription's lifetime, never logged, and dropped
 * on 401/403, on terminal, on unsubscribe, TTL and session close.
 *
 * Degradation: notifications are best-effort. A client that never calls resources/* sees no
 * change; one without subscription support can still `resources/read` or call the
 * GetExecutionStatus tool; an HTTP session with no standing SSE stream simply misses the
 * pushes (send errors are swallowed) while reads and the tool stay authoritative. stdio uses
 * the same code with the one token the process holds.
 */
import { createHash } from "node:crypto";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { McpError, ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import { ReelBoltApiError, type ReelBoltApiClient } from "./apiClient.js";
import { parseSnapshot, type Snapshot } from "./watch.js";

const CAP_MESSAGE =
  "Too many run subscriptions. Use the GetExecutionStatus tool to check a run instead.";
export const RUN_URI_TEMPLATE = "reelbolt://runs/{executionId}";
const URI_RE = /^reelbolt:\/\/runs\/([^/?#]+)(?:\?project=([^&#]+))?$/;
const GUID_RE = /^\{?([0-9a-f]{8})-?([0-9a-f]{4})-?([0-9a-f]{4})-?([0-9a-f]{4})-?([0-9a-f]{12})\}?$/i;

/** Lowercase, no braces, dashed; null when the id is not a GUID. */
export function canonicalGuid(id: string): string | null {
  const m = GUID_RE.exec(id.trim());
  return m ? m.slice(1).join("-").toLowerCase() : null;
}

/**
 * Quota identity: a truncated SHA-256 of the bearer token. The token itself is never stored as
 * a key or logged here; no token means one shared anonymous principal (the API will 401 anyway).
 */
export function principalOf(token: string | undefined): string {
  if (!token) return "anon";
  return createHash("sha256").update(token).digest("hex").slice(0, 16);
}
const MIN_POLL_S = 5;
const MAX_POLL_S = 60;
const MAX_FAILURES = 5;

export interface SubscriptionLimits {
  perSession: number;
  /** Across ALL sessions of one principal. */
  perPrincipal: number;
  /** Backstop; only reachable by many distinct principals. */
  processWide: number;
  ttlMs: number;
}

export function limitsFromEnv(env: NodeJS.ProcessEnv = process.env): SubscriptionLimits {
  const n = (v: string | undefined, d: number): number => {
    const x = Number(v);
    return v && Number.isFinite(x) && x > 0 ? x : d;
  };
  return {
    perSession: n(env.MCP_MAX_SUBSCRIPTIONS_PER_SESSION, 5),
    perPrincipal: n(env.MCP_MAX_SUBSCRIPTIONS_PER_PRINCIPAL, 10),
    processWide: n(env.MCP_MAX_SUBSCRIPTIONS, 100),
    ttlMs: n(env.MCP_SUBSCRIPTION_TTL_MINUTES, 120) * 60_000,
  };
}

/** Process-wide counter, shared by every session unless a test injects its own. */
export interface SubscriptionCounter {
  count: number;
  byPrincipal?: Map<string, number>;
}
export const processCounter: SubscriptionCounter = { count: 0, byPrincipal: new Map() };

export function parseRunUri(uri: string): { executionId: string; projectId?: string } | null {
  const m = URI_RE.exec(uri);
  if (!m) return null;
  try {
    const executionId = canonicalGuid(decodeURIComponent(m[1]));
    if (!executionId) return null;
    return { executionId, projectId: m[2] ? decodeURIComponent(m[2]) : undefined };
  } catch {
    return null;
  }
}

interface Sub {
  key: string;
  principal: string;
  uri: string;
  token: string | undefined;
  progressToken: string | number | undefined;
  timer: NodeJS.Timeout | undefined;
  ttl: NodeJS.Timeout | undefined;
  lastKey: string;
  failures: number;
  args: Record<string, unknown>;
  context: Record<string, unknown> | undefined;
}

const keyOf = (s: Snapshot): string => `${s.status}|${s.stepsDone}|${s.currentStep}`;

export class RunSubscriptions {
  private readonly subs = new Map<string, Sub>();

  constructor(
    private readonly server: Server,
    private readonly api: ReelBoltApiClient,
    private readonly limits: SubscriptionLimits = limitsFromEnv(),
    private readonly counter: SubscriptionCounter = processCounter
  ) {}

  private perPrincipal(): Map<string, number> {
    return (this.counter.byPrincipal ??= new Map());
  }

  private overCap(principal: string): boolean {
    return (
      this.subs.size >= this.limits.perSession ||
      (this.perPrincipal().get(principal) ?? 0) >= this.limits.perPrincipal ||
      this.counter.count >= this.limits.processWide
    );
  }

  get size(): number {
    return this.subs.size;
  }

  private async fetch(
    uri: string,
    token: string | undefined
  ): Promise<{ snap: Snapshot; text: string }> {
    const ref = parseRunUri(uri);
    if (!ref) throw new McpError(ErrorCode.InvalidParams, "Unknown resource URI; expected reelbolt://runs/{executionId}");
    const res = await this.api.callTool(
      "GetExecutionStatus",
      { executionId: ref.executionId },
      ref.projectId ? { projectId: ref.projectId } : undefined,
      token
    );
    const snap = res.isError ? null : parseSnapshot(res.content);
    if (!snap) throw new Error("unreadable status");
    const part = res.content.find(
      (c): c is { text: string } => !!c && typeof (c as { text?: unknown }).text === "string"
    );
    return { snap, text: part?.text ?? "{}" };
  }

  async read(uri: string, token: string | undefined): Promise<string> {
    try {
      return (await this.fetch(uri, token)).text;
    } catch (e) {
      if (e instanceof McpError) throw e;
      // Same message for missing, forbidden and broken: no existence leak, no token.
      throw new McpError(
        ErrorCode.InvalidRequest,
        "Could not read this run (unknown, not permitted, or the ReelBolt API is unavailable). Try the GetExecutionStatus tool."
      );
    }
  }

  async subscribe(uri: string, token: string | undefined, progressToken: string | number | undefined): Promise<void> {
    const ref = parseRunUri(uri);
    if (!ref) throw new McpError(ErrorCode.InvalidParams, "Unknown resource URI; expected reelbolt://runs/{executionId}");
    const principal = principalOf(token);
    const key = `${principal}:${ref.executionId}`;
    // Idempotent: the same run (any URI variant) never takes a second slot.
    const existing = this.subs.get(key);
    if (existing) {
      existing.token = token;
      existing.uri = uri;
      existing.progressToken = progressToken ?? existing.progressToken;
      return;
    }
    if (this.overCap(principal)) throw new McpError(ErrorCode.InvalidRequest, CAP_MESSAGE);
    let snap: Snapshot;
    try {
      snap = (await this.fetch(uri, token)).snap;
    } catch {
      return; // missing/forbidden/unreadable: succeed, register nothing, leak nothing
    }
    if (snap.terminal) {
      void Promise.resolve().then(() => this.notify(uri, snap, progressToken));
      return;
    }
    // Re-check after the await: another subscribe may have raced in.
    if (this.subs.has(key)) return;
    if (this.overCap(principal)) throw new McpError(ErrorCode.InvalidRequest, CAP_MESSAGE);
    const sub: Sub = {
      key,
      principal,
      uri,
      token,
      progressToken,
      timer: undefined,
      ttl: undefined,
      lastKey: keyOf(snap),
      failures: 0,
      args: { executionId: ref.executionId },
      context: ref.projectId ? { projectId: ref.projectId } : undefined,
    };
    this.subs.set(key, sub);
    this.counter.count += 1;
    this.perPrincipal().set(principal, (this.perPrincipal().get(principal) ?? 0) + 1);
    sub.ttl = setTimeout(() => this.drop(key), this.limits.ttlMs);
    sub.ttl.unref?.();
    this.schedule(sub, snap.retryAfterSeconds);
  }

  unsubscribe(uri: string, token: string | undefined): void {
    const ref = parseRunUri(uri);
    if (ref) this.drop(`${principalOf(token)}:${ref.executionId}`);
  }

  /** Clear every subscription and timer (session close). */
  closeAll(): void {
    for (const key of [...this.subs.keys()]) this.drop(key);
  }

  private drop(key: string): void {
    const sub = this.subs.get(key);
    if (!sub) return;
    if (sub.timer) clearTimeout(sub.timer);
    if (sub.ttl) clearTimeout(sub.ttl);
    sub.token = undefined;
    this.subs.delete(key);
    this.counter.count = Math.max(0, this.counter.count - 1);
    const pp = this.perPrincipal();
    const left = (pp.get(sub.principal) ?? 1) - 1;
    if (left > 0) pp.set(sub.principal, left);
    else pp.delete(sub.principal);
  }

  private schedule(sub: Sub, retryAfter: number | null): void {
    const s = Math.min(MAX_POLL_S, Math.max(MIN_POLL_S, retryAfter ?? MIN_POLL_S));
    sub.timer = setTimeout(() => void this.poll(sub), s * 1000);
    sub.timer.unref?.();
  }

  // One in-flight poll per subscription: the next timer is only set after this one settles.
  private async poll(sub: Sub): Promise<void> {
    if (this.subs.get(sub.key) !== sub) return;
    let snap: Snapshot | null = null;
    try {
      const res = await this.api.callTool("GetExecutionStatus", sub.args, sub.context, sub.token);
      snap = res.isError ? null : parseSnapshot(res.content);
    } catch (e) {
      if (e instanceof ReelBoltApiError && (e.status === 401 || e.status === 403)) {
        this.finish(sub);
        return;
      }
    }
    if (this.subs.get(sub.key) !== sub) return; // dropped while the call was in flight
    if (!snap) {
      sub.failures += 1;
      if (sub.failures >= MAX_FAILURES) {
        this.finish(sub);
        return;
      }
      this.schedule(sub, Math.min(MAX_POLL_S, MIN_POLL_S * 2 ** (sub.failures - 1)));
      return;
    }
    sub.failures = 0;
    const key = keyOf(snap);
    if (snap.terminal) {
      this.drop(sub.key);
      await this.notify(sub.uri, snap, sub.progressToken);
      return;
    }
    if (key !== sub.lastKey) {
      sub.lastKey = key;
      await this.notify(sub.uri, snap, sub.progressToken);
    }
    if (this.subs.get(sub.key) === sub) this.schedule(sub, snap.retryAfterSeconds);
  }

  /** Final "go re-read it" ping after an error, then forget the subscription. */
  private finish(sub: Sub): void {
    this.drop(sub.key);
    void this.notify(sub.uri, null, undefined);
  }

  private async notify(uri: string, snap: Snapshot | null, progressToken: string | number | undefined): Promise<void> {
    try {
      await this.server.notification({ method: "notifications/resources/updated", params: { uri } });
      if (snap && progressToken !== undefined && snap.stepsDone !== null) {
        await this.server.notification({
          method: "notifications/progress",
          params: {
            progressToken,
            progress: snap.stepsDone,
            ...(snap.stepsTotal !== null ? { total: snap.stepsTotal } : {}),
            ...(snap.currentStep ? { message: snap.currentStep } : {}),
          },
        });
      }
    } catch {
      // Best effort: no standing stream / closed transport. Reads and the tool stay authoritative.
    }
  }
}
