import type { Page, Route } from '@playwright/test';

/**
 * Stubs of the per-file analysis API (T-013: `/api/v1/projects/{p}/media/file:{id}/analysis...`) for the
 * analysis-driven editing specs. Transcription is paid and the free analyses depend on the media, so a
 * spec states exactly what the file "contains" and the editor reads it through its real code path; the
 * assertions are always on the STORED timeline document, never on the stub.
 *
 * mode 'full': every kind is answered by the stub (state `none` until a POST, then `running` for
 * `pollsBeforeReady` reads, then `ready`; or `ready` from the start with `readyAtStart`).
 * mode 'transcriptOnly': silence/shots/motion go to the real backend; only the transcript (estimate, start,
 * status, data) is stubbed - the one paid kind.
 */

export type Range = { startSec: number; endSec: number };
export type MotionWindow = Range & { mean: number; motionClass: 'still' | 'low' | 'high' };
export type StubWord = { text: string; startSec: number; endSec: number; estimated?: boolean };
export type FileStub = {
  fileId: string;
  silence?: Range[];
  shots?: Range[];
  motion?: MotionWindow[];
  transcript?: StubWord[];
};
export type Kind = 'silence' | 'shots' | 'motion' | 'transcript';

export type StubOptions = {
  mode?: 'full' | 'transcriptOnly';
  readyAtStart?: boolean;
  pollsBeforeReady?: number;
  estimate?: Partial<{ billingEnabled: boolean; credits: number | null; seconds: number | null; balanceCredits: number | null; affordable: boolean; cached: boolean; unavailableReason: string | null }>;
};

export type StubControl = {
  starts: Array<{ fileId: string; kinds: string[] }>;
  estimates: string[];
};

const KINDS: Kind[] = ['silence', 'shots', 'motion', 'transcript'];

function summary(kind: Kind, stub: FileStub): Record<string, number | string | null> {
  if (kind === 'silence') return { rangeCount: stub.silence?.length ?? 0, totalSeconds: (stub.silence ?? []).reduce((n, r) => n + r.endSec - r.startSec, 0) };
  if (kind === 'shots') return { shotCount: stub.shots?.length ?? 0 };
  if (kind === 'motion') return { windowCount: stub.motion?.length ?? 0, stillSeconds: (stub.motion ?? []).filter((w) => w.motionClass === 'still').reduce((n, w) => n + w.endSec - w.startSec, 0) };
  const words = stub.transcript ?? [];
  const estimated = words.filter((w) => w.estimated).length;
  return { wordCount: words.length, estimatedWordCount: estimated, measuredWordCount: words.length - estimated, language: 'en' };
}

const has = (kind: Kind, stub: FileStub): boolean => (stub[kind] ?? null) !== null;

export async function stubAnalysis(page: Page, stubs: FileStub[], options: StubOptions = {}): Promise<StubControl> {
  const mode = options.mode ?? 'full';
  const control: StubControl = { starts: [], estimates: [] };
  const started = new Map<string, number>(); // `${file}|${kind}` -> reads since start
  const pollsBeforeReady = options.pollsBeforeReady ?? 2;
  const stubbed = (kind: Kind): boolean => mode === 'full' || kind === 'transcript';

  const stateOf = (fileId: string, kind: Kind, stub: FileStub): { state: string; reads: number } => {
    if (!has(kind, stub)) return { state: 'none', reads: 0 };
    if (options.readyAtStart) return { state: 'ready', reads: 0 };
    const key = `${fileId}|${kind}`;
    const reads = started.get(key);
    if (reads === undefined) return { state: 'none', reads: 0 };
    started.set(key, reads + 1);
    return { state: reads >= pollsBeforeReady ? 'ready' : 'running', reads };
  };

  const statusBody = (fileId: string, stub: FileStub, base: { kinds?: Record<string, unknown> } | null) => {
    const kinds: Record<string, unknown> = { ...(base?.kinds ?? {}) };
    for (const kind of KINDS) {
      if (!stubbed(kind)) continue;
      const { state } = stateOf(fileId, kind, stub);
      kinds[kind] = { state, reason: null, requestId: state === 'none' ? null : '00000000-0000-4000-8000-000000000abc', updatedAt: state === 'none' ? null : new Date().toISOString(), summary: state === 'ready' ? summary(kind, stub) : null };
    }
    return { mediaRef: `file:${fileId}`, kinds };
  };

  const data = (kind: Kind, stub: FileStub): unknown => {
    if (kind === 'silence') return { durationSec: 30, ranges: stub.silence ?? [], truncated: false };
    if (kind === 'shots') return { shots: stub.shots ?? [], truncated: false };
    if (kind === 'motion') return { windows: stub.motion ?? [], truncated: false };
    return { language: 'en', words: (stub.transcript ?? []).map((w) => ({ ...w, estimated: w.estimated === true })), truncated: false };
  };

  await page.route(/\/api\/v1\/projects\/[^/]+\/media\/file(?::|%3A)[^/]+\/analysis/i, async (route: Route) => {
    const url = new URL(route.request().url());
    const match = /\/media\/file(?::|%3A)([^/]+)\/analysis(?:\/([a-z]+))?$/i.exec(url.pathname);
    const stub = match ? stubs.find((s) => s.fileId === match[1]) : undefined;
    if (!match || !stub) return route.continue();
    const method = route.request().method();
    const tail = match[2];

    if (method === 'POST' && tail === 'estimate') {
      control.estimates.push(match[1]);
      return route.fulfill({ status: 200, json: { billingEnabled: true, credits: 3, seconds: 12, balanceCredits: 100, affordable: true, cached: false, unavailableReason: null, ...options.estimate } });
    }
    if (method === 'POST' && !tail) {
      const body = JSON.parse(route.request().postData() ?? '{}') as { kinds?: string[] };
      const kinds = body.kinds ?? [];
      control.starts.push({ fileId: match[1], kinds });
      if (!kinds.some((k) => stubbed(k as Kind))) return route.continue();
      for (const k of kinds) if (stubbed(k as Kind)) started.set(`${match[1]}|${k}`, 0);
      if (mode === 'transcriptOnly') {
        if (kinds.every((k) => k === 'transcript')) return route.fulfill({ status: 202, json: { requestId: '00000000-0000-4000-8000-000000000abc', mediaRef: `file:${match[1]}`, states: { transcript: { state: 'queued', reason: null } } } });
        return route.continue();
      }
      return route.fulfill({ status: 202, json: { requestId: '00000000-0000-4000-8000-000000000abc', mediaRef: `file:${match[1]}`, states: Object.fromEntries(kinds.map((k) => [k, { state: 'queued', reason: null }])) } });
    }
    if (method === 'GET' && !tail) {
      if (mode === 'transcriptOnly') {
        const real = await route.fetch();
        let base: { kinds?: Record<string, unknown> } | null = null;
        try { base = (await real.json()) as { kinds?: Record<string, unknown> }; } catch { /* the stub answers alone */ }
        return route.fulfill({ response: real, json: statusBody(match[1], stub, base) });
      }
      return route.fulfill({ status: 200, json: statusBody(match[1], stub, null) });
    }
    if (method === 'GET' && tail && (KINDS as string[]).includes(tail)) {
      const kind = tail as Kind;
      if (!stubbed(kind)) return route.continue();
      const { state } = options.readyAtStart ? { state: 'ready' } : (started.has(`${match[1]}|${kind}`) ? { state: (started.get(`${match[1]}|${kind}`) ?? 0) >= pollsBeforeReady ? 'ready' : 'running' } : { state: 'none' });
      if (state !== 'ready' || !has(kind, stub)) return route.fulfill({ status: 409, json: { state, reason: null } });
      return route.fulfill({ status: 200, json: data(kind, stub) });
    }
    return route.continue();
  });
  return control;
}
