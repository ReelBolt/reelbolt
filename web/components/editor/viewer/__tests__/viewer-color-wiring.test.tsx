// @vitest-environment jsdom
/**
 * The viewer's colour stages are connected end to end: the workspace helper builds the hooks the live session needs,
 * the viewer hands them to the session, the converted proxy is requested through the API, and the permanent
 * "preview only" badge shows while a viewing LUT is on.
 */
import React from 'react';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { PreviewSession } from '@/lib/editing/preview';
import { ZERO_STATS } from '@/lib/editing/preview';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import type { ViewingLutSpec } from '@/lib/editing/color/viewer-color';

const requestConvertedProxy = vi.hoisted(() => vi.fn(async () => ({})));
const fetchMediaDerivatives = vi.hoisted(() => vi.fn(async () => [{ id: 'plain', kind: 'proxy', mimeType: 'video/mp4', sizeBytes: 1, durationSec: 1, width: 640, height: 360, contentUrl: '' }]));
vi.mock('@/lib/editing/api/media', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/editing/api/media')>()),
  requestConvertedProxy,
  fetchMediaDerivatives,
  derivativeContentUrl: (_p: string, row: { id: string }) => `/p/${row.id}`,
  originalContentUrl: () => '/orig',
}));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

let sessionOptions: { live?: { inputStageFor?: unknown; getViewingLut?: unknown } } | null = null;
let invalidations = 0;
vi.mock('@/lib/editing/preview', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/editing/preview')>();
  return {
    ...actual,
    hasWebCodecs: () => true,
    createPreviewSession: (options: typeof sessionOptions) => {
      sessionOptions = options;
      const session: PreviewSession = {
        kind: 'live', reason: null, seek: () => {}, play: () => {}, pause: () => {}, currentFrame: () => 0, isPlaying: () => false, stats: () => ZERO_STATS,
        missingLayers: () => [], subscribe: () => () => {}, invalidate: () => { invalidations += 1; }, dispose: () => {},
      } as unknown as PreviewSession;
      return session;
    },
  };
});

import { ProgramViewer } from '../ProgramViewer';
import { buildSessionHooks, createWorkspaceProxyResolver, wantDocumentProfiles } from '../session-wiring';

const timeline: EditTimeline = doc([track('v1', 'video', [media('c1', 0, 300, 0, { inputProfile: 'appleLog' })])]);
const flags = () => ({ locked: false, enabled: true, mute: false, solo: false });
const SPEC: ViewingLutSpec = { key: 'profile:appleLog', size: 2, domainMin: [0, 0, 0], domainMax: [1, 1, 1], texels: new Uint16Array(32) };
const tick = () => new Promise((r) => setTimeout(r, 0));

describe('viewer colour wiring', () => {
  beforeEach(() => {
    sessionOptions = null;
    invalidations = 0;
    requestConvertedProxy.mockClear();
  });
  afterEach(() => cleanup());

  it('the workspace helper yields non-null hooks that follow the clip profile and the HDR flag', async () => {
    const resolver = createWorkspaceProxyResolver('p1');
    const hooks = buildSessionHooks(resolver, (c) => (c.assetRef.projectFileId === 'hdr' ? 'hlg' : null));
    expect(hooks.proxyUrlFor).toBeTypeOf('function');
    expect(hooks.inputStageFor).toBeTypeOf('function');
    const clip = timeline.tracks[0].items[0] as MediaClip;
    expect(hooks.inputStageFor(clip)).toEqual({ profile: 'appleLog', baked: false });
    expect(hooks.inputStageFor({ ...clip, assetRef: { ...clip.assetRef, projectFileId: 'hdr' } } as MediaClip)).toBeNull();
    expect(hooks.inputStageFor({ ...clip, inputProfile: undefined })).toBeNull();
  });

  it('asks for the converted proxy once per (source, profile) through requestConvertedProxy, skipping HDR sources', async () => {
    const resolver = createWorkspaceProxyResolver('p1');
    resolver.request(['file:00000000-0000-4000-8000-000000000001']);
    await tick();
    wantDocumentProfiles(resolver, timeline, () => false);
    wantDocumentProfiles(resolver, timeline, () => false);
    expect(requestConvertedProxy).toHaveBeenCalledTimes(1);
    expect(requestConvertedProxy).toHaveBeenCalledWith('p1', 'file:00000000-0000-4000-8000-000000000001', 'appleLog');
    const other = createWorkspaceProxyResolver('p1');
    other.request(['file:00000000-0000-4000-8000-000000000001']);
    await tick();
    requestConvertedProxy.mockClear();
    wantDocumentProfiles(other, timeline, () => true);
    expect(requestConvertedProxy).not.toHaveBeenCalled();
  });

  it('the viewer hands the session the input stage and viewing LUT hooks, and shows the badge while a LUT is on', () => {
    const inputStageFor = vi.fn(() => ({ profile: 'appleLog', baked: false }));
    const getViewingLut = vi.fn(() => SPEC);
    const props = { doc: timeline, getDoc: () => timeline, flags, proxyUrlFor: () => '/p', playheadFrame: 0, speed: 0, playbackResolution: 'half' as const, forceFallback: false, onStats: () => undefined, onFrame: () => undefined, onEnded: () => undefined };
    const view = render(
      <MantineProvider>
        <ProgramViewer {...props} inputStageFor={inputStageFor} getViewingLut={getViewingLut} viewingLutVersion={null} />
      </MantineProvider>,
    );
    const live = sessionOptions?.live as { inputStageFor: (c: MediaClip) => unknown; getViewingLut: () => unknown };
    expect(live.inputStageFor).toBeTypeOf('function');
    expect(live.getViewingLut).toBeTypeOf('function');
    expect(live.inputStageFor(timeline.tracks[0].items[0] as MediaClip)).toEqual({ profile: 'appleLog', baked: false });
    expect(live.getViewingLut()).toBe(SPEC);
    expect(screen.queryByTestId('viewing-lut-badge')).toBeNull();
    const before = invalidations;
    view.rerender(
      <MantineProvider>
        <ProgramViewer {...props} inputStageFor={inputStageFor} getViewingLut={getViewingLut} viewingLutVersion="profile:appleLog" />
      </MantineProvider>,
    );
    expect(invalidations).toBeGreaterThan(before);
    expect(screen.getByTestId('viewing-lut-badge').textContent).toContain('Preview only, not in your export');
  });

  it('EditorWorkspace connects every hook (resolver, profiles, input stage, viewing LUT, menu off on phone)', () => {
    // The workspace is split by domain: the sources of the preview in one hook, the viewer's markup in one view.
    const workspace = path.join(__dirname, '..', '..', 'workspace');
    const src = ['usePreviewSources.ts', path.join('view', 'useViewerZoneView.tsx')].map((file) => readFileSync(path.join(workspace, file), 'utf8')).join('\n');
    expect(src).toContain('createWorkspaceProxyResolver(');
    expect(src).toContain('wantDocumentProfiles(');
    expect(src).toContain('buildSessionHooks(');
    expect(src).toContain('inputStageFor={inputStageFor}');
    expect(src).toContain('getViewingLut={viewingLut.getViewingLut}');
    expect(src).toMatch(/!isPhone && <ViewingLutMenu/);
  });
});
