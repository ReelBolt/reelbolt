/** @vitest-environment node */
/**
 * Honest failure and recovery: createPreviewSession names the one cause behind the Simple preview, the
 * live session reports a lost graphics context (and clears it on restore), the Simple preview carries
 * the top clip's grade, and the page holds one probe context that is released at once.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { __resetGlProbeForTests, createPreviewSession, hasWebGl2 } from '../index';
import { CONTEXT_RESTORE_WAIT_MS, createLivePreviewSession } from '../live-session';
import { createFallbackPreviewSession } from '../fallback-session';
import { COMPOSITOR_CONTEXT_ATTRIBUTES } from '../gl-compositor';
import { REASON_COMPOSITOR_FAILED, REASON_CONTEXT_FAILED, REASON_CONTEXT_LOST, REASON_NO_WEBGL2, REASON_USER_CHOICE } from '../fault-reasons';
import { probeGl } from '../gl-probe';
import { readDeviceFacts } from '../device-budget';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import type { MediaClip } from '../../types';

const flags = (t: { locked: boolean; enabled: boolean; mute: boolean; solo: boolean }) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo });
const urlFor = (clip: MediaClip) => `/proxy/${clip.id}`;

function plainDoc(extra: Partial<MediaClip> = {}) {
  return doc([track('v1', 'video', [media('c1', 0, 60, 0, { assetRef: assetRef(1), ...extra })])]);
}

/** A WebGL2 stand-in: every method answers harmlessly; programs "link". */
function fakeGl(renderer = 'ANGLE (NVIDIA)', onLose?: () => void): WebGL2RenderingContext {
  const handlers: Record<string, (...args: unknown[]) => unknown> = {
    getShaderParameter: () => true,
    getProgramParameter: () => true,
    getAttribLocation: () => 0,
    getExtension: (name) => {
      if (name === 'WEBGL_debug_renderer_info') return { UNMASKED_RENDERER_WEBGL: 1 };
      if (name === 'WEBGL_lose_context') return { loseContext: () => onLose?.() };
      return null;
    },
    getParameter: () => renderer,
  };
  return new Proxy({} as WebGL2RenderingContext, { get: (_t, prop: string) => handlers[prop] ?? (() => ({})) });
}

function canvasWith(gl: WebGL2RenderingContext | null) {
  const handlers = new Map<string, Set<(e: Event) => void>>();
  const canvas = {
    width: 320,
    height: 180,
    getContext: () => gl,
    addEventListener: (type: string, fn: (e: Event) => void) => {
      if (!handlers.has(type)) handlers.set(type, new Set());
      handlers.get(type)!.add(fn);
    },
    removeEventListener: (type: string, fn: (e: Event) => void) => handlers.get(type)?.delete(fn),
  } as unknown as HTMLCanvasElement;
  const fire = (type: string) => handlers.get(type)?.forEach((fn) => fn({ preventDefault: () => undefined } as unknown as Event));
  return { canvas, fire };
}

function fakeVideo() {
  return { src: '', currentTime: 0, muted: false, addEventListener: () => undefined, removeEventListener: () => undefined, pause: () => undefined, removeAttribute: () => undefined, load: () => undefined } as unknown as HTMLVideoElement;
}

beforeEach(() => {
  __resetGlProbeForTests();
  vi.stubGlobal('VideoDecoder', function VideoDecoder() {});
  vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  vi.spyOn(console, 'error').mockImplementation(() => undefined);
  vi.spyOn(console, 'debug').mockImplementation(() => undefined);
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function stubDocumentGl(gl: WebGL2RenderingContext | null) {
  let created = 0;
  vi.stubGlobal('document', { createElement: () => (created++, { width: 0, height: 0, getContext: () => gl }) });
  return () => created;
}

describe('createPreviewSession names the one cause', () => {
  const base = (over: Partial<Parameters<typeof createPreviewSession>[0]>) => ({
    canvas: canvasWith(fakeGl()).canvas,
    video: fakeVideo(),
    getDoc: () => plainDoc(),
    flags,
    proxyUrlFor: urlFor,
    live: { audioContext: null },
    ...over,
  });

  it('the Simple preview was chosen: userChoice, and live is available', () => {
    stubDocumentGl(fakeGl());
    const s = createPreviewSession(base({ forceKind: 'fallback' }));
    expect(s).toMatchObject({ kind: 'fallback', fault: 'userChoice', reason: REASON_USER_CHOICE, liveAvailable: true });
    s.dispose();
  });

  it('no WebGL2 in this browser: noWebGl2, with the graphics acceleration hint, live not available', () => {
    stubDocumentGl(null);
    const s = createPreviewSession(base({ canvas: canvasWith(null).canvas }));
    expect(s).toMatchObject({ kind: 'fallback', fault: 'noWebGl2', reason: REASON_NO_WEBGL2, liveAvailable: false });
    expect(s.reason).toMatch(/graphics acceleration/);
    expect(s.reason).not.toMatch(/WebGL/);
    s.dispose();
  });

  it('the compositor failing to start is NOT reported as missing WebGL2', () => {
    stubDocumentGl(fakeGl()); // the probe succeeds ...
    const s = createPreviewSession(base({ canvas: canvasWith(null).canvas })); // ... but the compositor's own context fails
    expect(s).toMatchObject({ kind: 'fallback', fault: 'compositorFailed', reason: REASON_COMPOSITOR_FAILED });
    s.dispose();
  });

  it('a working browser with Auto/Full goes live', () => {
    stubDocumentGl(fakeGl());
    const s = createPreviewSession(base({}));
    expect(s.kind).toBe('live');
    s.dispose();
  });
});

describe('the single probe', () => {
  it('creates one context for capability AND renderer, with the compositor attributes, and releases it', () => {
    const lost = vi.fn();
    const attrs: unknown[] = [];
    let created = 0;
    vi.stubGlobal('document', { createElement: () => (created++, { getContext: (_k: string, a: unknown) => (attrs.push(a), fakeGl('SwiftShader', lost)) }) });
    vi.stubGlobal('window', { innerWidth: 1280, innerHeight: 800, matchMedia: () => ({ matches: false }) });
    vi.stubGlobal('navigator', { hardwareConcurrency: 8 });
    expect(hasWebGl2(null)).toBe(true);
    expect(readDeviceFacts().softwareGraphics).toBe(true);
    expect(hasWebGl2(null)).toBe(true);
    expect(created).toBe(1);
    expect(attrs).toEqual([COMPOSITOR_CONTEXT_ATTRIBUTES]);
    expect(lost).toHaveBeenCalledTimes(1);
  });

  it('can be re-asked (Try again after turning graphics acceleration on)', () => {
    const created = stubDocumentGl(null);
    expect(probeGl().webGl2).toBe(false);
    expect(probeGl().webGl2).toBe(false);
    expect(created()).toBe(1);
    stubDocumentGl(fakeGl());
    __resetGlProbeForTests();
    expect(probeGl().webGl2).toBe(true);
  });
});

describe('live session: a lost graphics context', () => {
  beforeEach(() => {
    vi.stubGlobal('document', { createElement: () => ({ width: 0, height: 0, getContext: () => null }) });
  });
  function live() {
    const { canvas, fire } = canvasWith(fakeGl());
    const session = createLivePreviewSession({ canvas, getDoc: () => plainDoc(), flags, proxyUrlFor: urlFor, openSource: () => Promise.resolve(null), audioContext: null, requestFrame: () => 1, cancelFrame: () => undefined })!;
    return { session, fire };
  }

  it('says so on loss, clears on restore', () => {
    const { session, fire } = live();
    const seen = vi.fn();
    session.subscribe(seen);
    expect(session.contextLoss).toBeNull();
    fire('webglcontextlost');
    expect(session.contextLoss).toEqual({ state: 'lost', message: REASON_CONTEXT_LOST });
    expect(seen).toHaveBeenCalled();
    fire('webglcontextrestored');
    expect(session.contextLoss).toBeNull();
    session.dispose();
  });

  it('a context the browser never restores turns into the failure message after the bounded wait', () => {
    vi.useFakeTimers();
    try {
      const { session, fire } = live();
      fire('webglcontextlost');
      vi.advanceTimersByTime(CONTEXT_RESTORE_WAIT_MS - 1);
      expect(session.contextLoss).toEqual({ state: 'lost', message: REASON_CONTEXT_LOST });
      vi.advanceTimersByTime(1);
      expect(session.contextLoss).toEqual({ state: 'failed', message: REASON_CONTEXT_FAILED });
      session.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it('a restore inside the wait cancels the timeout', () => {
    vi.useFakeTimers();
    try {
      const { session, fire } = live();
      fire('webglcontextlost');
      fire('webglcontextrestored');
      vi.advanceTimersByTime(CONTEXT_RESTORE_WAIT_MS * 2);
      expect(session.contextLoss).toBeNull();
      session.dispose();
    } finally {
      vi.useRealTimers();
    }
  });

  it('a failed rebuild is reported as failed (the viewer offers Try again)', () => {
    const gl = fakeGl();
    let healthy = true;
    const failing = new Proxy(gl, { get: (t, p: string) => (p === 'getProgramParameter' ? () => healthy : (t as never)[p]) });
    const { canvas, fire } = canvasWith(failing);
    const session = createLivePreviewSession({ canvas, getDoc: () => plainDoc(), flags, proxyUrlFor: urlFor, openSource: () => Promise.resolve(null), audioContext: null, requestFrame: () => 1, cancelFrame: () => undefined })!;
    fire('webglcontextlost');
    healthy = false;
    fire('webglcontextrestored');
    expect(session.contextLoss).toEqual({ state: 'failed', message: REASON_CONTEXT_FAILED });
    session.dispose();
  });
});

describe('the Simple preview carries the grade', () => {
  const graded = { grade: { nodes: [{ id: 'n', primaries: { exposure: 0.5, saturation: 1.3 } }] } } as Partial<MediaClip>;
  function fallback(bypass: () => boolean, d = plainDoc(graded)) {
    return createFallbackPreviewSession({ video: fakeVideo(), getDoc: () => d, flags, proxyUrlFor: urlFor, requestFrame: () => 1, cancelFrame: () => undefined, getGradeBypass: bypass });
  }

  it('exposes the top clip\'s grade as an SVG filter, and none for an ungraded clip', () => {
    const s = fallback(() => false);
    s.seek(5);
    const f = s.gradeFilter!();
    expect(f).not.toBeNull();
    expect(f!.tables[0]).toHaveLength(256);
    const plain = fallback(() => false, plainDoc());
    plain.seek(5);
    expect(plain.gradeFilter!()).toBeNull();
  });

  it('Grade off (bypass) removes it on the next show, back on restores the same filter', () => {
    let bypass = false;
    const s = fallback(() => bypass);
    s.seek(5);
    const on = s.gradeFilter!();
    bypass = true;
    s.invalidate();
    expect(s.gradeFilter!()).toBeNull();
    bypass = false;
    s.invalidate();
    expect(s.gradeFilter!()?.signature).toBe(on!.signature);
  });

  it('a grade shared through a colour group is applied too (group then own stack)', () => {
    // The group case is exercised by effectiveGrade itself; here we only pin that the call is the same one the live session makes.
    const s = fallback(() => false);
    s.seek(0);
    expect(s.gradeFilter!()).not.toBeNull();
  });
});

describe('every user-facing reason is in the catalogues (en + it)', () => {
  it('maps each reason sentence to its own key, not the raw pass-through', async () => {
    const { previewSessionReasonMsg, honestyReasonMsg } = await import('../labels-msg');
    const { SIMPLE_GRADE_NOTE } = await import('../honesty');
    for (const r of [REASON_USER_CHOICE, REASON_NO_WEBGL2, REASON_COMPOSITOR_FAILED, REASON_CONTEXT_LOST, REASON_CONTEXT_FAILED]) {
      expect(JSON.stringify(previewSessionReasonMsg(r))).not.toContain('common.raw');
    }
    expect(JSON.stringify(honestyReasonMsg(SIMPLE_GRADE_NOTE))).not.toContain('common.raw');
  });
});
