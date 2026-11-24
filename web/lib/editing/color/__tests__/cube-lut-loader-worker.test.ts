import { afterEach, describe, expect, it, vi } from 'vitest';
import { disposeViewingLut, loadViewingLut } from '../cube-lut-loader';
import { parseCubeLut } from '../cube-lut';

/**
 * The production regression behind the owner's Samsung LUT ("could not be read"): in the production bundle the LUT
 * worker booted ANOTHER worker's module (the decode worker's), so the parser never ran and every pick timed out.
 * The loader now sends the text only after the worker says `ready`, and parses on the main thread when it never does.
 */

const ONE_D = 'TITLE "t"\r\nLUT_1D_SIZE 3\r\n\r\n0 0 0\r\n0.5 0.5 0.5\r\n9.9994 9.9994 9.9994\r\n';

type Listener = ((event: MessageEvent) => void) | null;

/** The real module: boots a little later, drops anything posted before, then announces `ready`. */
class ParserWorker {
  static instances: ParserWorker[] = [];
  onmessage: Listener = null;
  onerror: ((event: Event) => void) | null = null;
  booted = false;
  dropped = 0;
  constructor() {
    ParserWorker.instances.push(this);
    setTimeout(() => {
      this.booted = true;
      this.onmessage?.({ data: { ready: true } } as MessageEvent);
    }, 5);
  }
  postMessage(text: string) {
    if (!this.booted) {
      this.dropped += 1;
      return;
    }
    setTimeout(() => this.onmessage?.({ data: { ok: true, lut: parseCubeLut(text) } } as MessageEvent), 0);
  }
  terminated = false;
  terminate() {
    this.terminated = true;
  }
}

/** What production actually ran: another worker's module, which never says ready and ignores the text. */
class WrongModuleWorker {
  onmessage: Listener = null;
  onerror: ((event: Event) => void) | null = null;
  postMessage() {}
  terminate() {}
}

/** Boots, takes the text, then crashes (an error after the text was sent). */
class CrashAfterTextWorker extends ParserWorker {
  override postMessage() {
    setTimeout(() => this.onerror?.(new Event('error')), 0);
  }
}

/** Crashes while booting, before saying ready. */
class CrashOnBootWorker {
  onmessage: Listener = null;
  onerror: ((event: Event) => void) | null = null;
  constructor() {
    setTimeout(() => this.onerror?.(new Event('error')), 0);
  }
  postMessage() {}
  terminate() {}
}

/** Boots and answers with a refusal. */
class RefusingWorker extends ParserWorker {
  override postMessage() {
    setTimeout(() => this.onmessage?.({ data: { ok: false, code: 'wrong_row_count', details: { expected: 3, found: 2 } } } as MessageEvent), 0);
  }
}

/** The real module that boots, takes the text, then never answers (a parse that hangs). */
class HangingParserWorker extends ParserWorker {
  override postMessage() {}
}

afterEach(() => {
  vi.unstubAllGlobals();
  disposeViewingLut();
  ParserWorker.instances = [];
});

describe('the viewing LUT worker handshake', () => {
  it('sends the text only after the worker says it is ready (nothing is dropped)', async () => {
    vi.stubGlobal('Worker', ParserWorker);
    const lut = await loadViewingLut(async () => ONE_D);
    expect(lut.kind).toBe('1d');
    expect(ParserWorker.instances[0]?.dropped).toBe(0);
  });

  it('a worker that runs the wrong module (the production bug) falls back to the main thread', async () => {
    vi.stubGlobal('Worker', WrongModuleWorker);
    const lut = await loadViewingLut(async () => ONE_D, { bootTimeoutMs: 20 });
    expect(lut.kind).toBe('1d');
    expect(lut.oneD?.size).toBe(3);
  });

  it('that fallback is not limited to small files: a worker that never booted says nothing about the file', async () => {
    vi.stubGlobal('Worker', WrongModuleWorker);
    const big = `LUT_3D_SIZE 2\n${'0 0 0\n'.repeat(8)}${'# padding\n'.repeat(300_000)}`;
    const lut = await loadViewingLut(async () => big, { bootTimeoutMs: 20 });
    expect(lut.kind).toBe('3d');
  });

  it('a worker that has the text and hangs is still refused, not retried on the main thread', async () => {
    vi.stubGlobal('Worker', HangingParserWorker);
    await expect(loadViewingLut(async () => ONE_D, { parseTimeoutMs: 30 })).rejects.toMatchObject({ code: 'malformed' });
  });

  it('a worker crash before ready parses here at the full cap; after the text only a small file falls back', async () => {
    const big = `LUT_3D_SIZE 2\n${'0 0 0\n'.repeat(8)}${'# padding\n'.repeat(300_000)}`;
    vi.stubGlobal('Worker', CrashOnBootWorker);
    expect((await loadViewingLut(async () => big)).kind).toBe('3d');
    vi.stubGlobal('Worker', CrashAfterTextWorker);
    expect((await loadViewingLut(async () => ONE_D)).kind).toBe('1d');
    await expect(loadViewingLut(async () => big)).rejects.toMatchObject({ code: 'too_large' });
  });

  it("passes the worker's refusal through with its details, and always terminates the worker", async () => {
    vi.stubGlobal('Worker', RefusingWorker);
    await expect(loadViewingLut(async () => ONE_D)).rejects.toMatchObject({ code: 'wrong_row_count', details: { expected: 3, found: 2 } });
    expect(ParserWorker.instances.every((w) => w.terminated)).toBe(true);
  });

  it('caches a table parsed by the worker under its key', async () => {
    vi.stubGlobal('Worker', ParserWorker);
    const first = await loadViewingLut(async () => ONE_D, { key: 'k1' });
    const second = await loadViewingLut(async () => {
      throw new Error('should come from the cache');
    }, { key: 'k1' });
    expect(second).toBe(first);
    expect(ParserWorker.instances).toHaveLength(1);
  });
});
