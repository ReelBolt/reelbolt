// @vitest-environment jsdom
/**
 * The editor's media-pool wiring (edff3f154; QA C-324-2), rendered through the real EditorWorkspace
 * and store: Create subclip and Insert/Add place the source viewer's marked range (end-exclusive
 * source frames, converted to the timeline rate) with the footage's sound as ONE undo step, the
 * unknown-length note shows, Inspector "Extract audio" puts a clip's sound on the paired track,
 * and every refusal path leaves the document untouched.
 *
 * Only the network/canvas edges are stubbed: the pool hook (fixed items), the derivative request,
 * the compositor viewer, the timeline canvas, and MediaView (a stand-in that calls the SAME props
 * the real one does, with the marks mapping MediaView uses: descriptor in/out -> Marks).
 */

import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { EditorWorkspace } from '../EditorWorkspace';
import type { MediaViewProps } from '../hub/MediaView';
import { createEditorStore, type EditorStore } from '@/lib/editing/store';
import { assetRef, doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { SaveState, SyncClient } from '@/lib/editing/sync';
import type { EditTimeline, MediaClip, Track } from '@/lib/editing/types';
import type { MediaPoolItem, MediaProbe } from '@/lib/editing/media/types';
import type { Marks } from '@/lib/editing/media/in-out';
import { createSubclip } from '@/lib/editing/media/subclip';
import { LENGTH_UNKNOWN_NOTE, PAIRED_AUDIO_TRACK_NAME } from '@/lib/editing/media/pool-insert';

const env = vi.hoisted(() => ({ pool: [] as unknown[], marks: { inFrame: null, outFrame: null } as { inFrame: number | null; outFrame: number | null } }));

vi.mock('@/lib/hooks/use-media', async (importOriginal) => ({ ...(await importOriginal<object>()), useMediaPool: () => ({ data: env.pool }) }));
vi.mock('@/lib/hooks/use-media-derivatives', async (importOriginal) => ({ ...(await importOriginal<object>()), useRequestTimelineDerivatives: () => undefined }));
vi.mock('../timeline/TimelineCanvas', () => ({ TimelineCanvas: () => null }));
vi.mock('../viewer', async (importOriginal) => ({ ...(await importOriginal<object>()), ProgramViewer: () => null }));
vi.mock('../hub/MediaView', () => ({
  MediaView: ({ clipActions, onCreateSubclip }: MediaViewProps) => (
    <div>
      {(env.pool as MediaPoolItem[]).map((item) => (
        <div key={item.mediaRef} data-testid={`stub-${item.fileName}`}>
          {clipActions?.(item, env.marks)}
          <button
            type="button"
            onClick={() => {
              const d = createSubclip(item, env.marks.inFrame ?? 0, env.marks.outFrame ?? 0, item.fileName);
              if (d) onCreateSubclip?.(item, d, { inFrame: d.sourceInFrames, outFrame: d.sourceOutFrames });
            }}
          >
            Create subclip
          </button>
        </div>
      ))}
    </div>
  ),
}));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
Element.prototype.scrollIntoView = vi.fn();
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
globalThis.fetch = vi.fn(() => new Promise<Response>(() => {})) as unknown as typeof fetch;

const show = vi.spyOn(notifications, 'show');
beforeEach(() => {
  show.mockClear();
  env.marks = { inFrame: null, outFrame: null };
});
// These tests are about the full editor: say so, or a desktop with no stored choice waits for the saved preferences.
window.localStorage.setItem('reelbolt:editor:shell', 'full');
afterEach(cleanup);

const FILE_ID = '00000000-0000-4000-8000-000000000001'; // the fixtures' assetRef(1)
const probe = (overrides: Partial<MediaProbe> = {}): MediaProbe => ({
  durationSec: 12, fpsNum: 25, fpsDen: 1, width: 1920, height: 1080, videoCodec: 'h264', audioCodec: 'aac',
  container: 'mov,mp4,m4a,3gp,3g2,mj2', audioChannels: 2, audioSampleRateHz: 48000, bitrateBps: 8_000_000, ...overrides,
});
const poolItem = (overrides: Partial<MediaPoolItem> = {}): MediaPoolItem => ({
  mediaRef: `file:${FILE_ID}`, kind: 'video', fileName: 'interview.mp4', mimeType: 'video/mp4', sizeBytes: 1,
  category: 'userFiles', probe: probe(), bins: [], filmstripUrl: '', ...overrides,
});

const saved: SaveState = { status: 'saved', revision: 1, lastSavedAt: 1, conflictRevision: null, violations: [], message: null, attempt: 0 };
const fakeSync = (state: SaveState) =>
  ({ getState: () => state, subscribe: () => () => {}, attachOps: vi.fn(), flush: vi.fn(), getQueuedEntry: () => null, discardQueue: vi.fn() }) as unknown as SyncClient;

/** V1 [c1 0-60) on a 30 fps timeline, plus an empty A1. */
const baseDoc = (v1: Partial<Track> = {}, c1: Partial<MediaClip> = {}): EditTimeline =>
  doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0, c1)], v1), track(TRACKS.a1, 'audio', [])]);

function mount(d: EditTimeline, opts: { pool?: MediaPoolItem[]; workspace?: 'media' | 'edit'; save?: SaveState; selection?: string[]; playheadFrame?: number; targetedTrackIds?: string[] } = {}): EditorStore {
  env.pool = opts.pool ?? [poolItem()];
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
  store.setUi({ workspace: opts.workspace ?? 'media', selection: opts.selection ?? [], playheadFrame: opts.playheadFrame ?? 0 });
  if (opts.targetedTrackIds) store.setUi({ targetedTrackIds: opts.targetedTrackIds });
  render(
    <MantineProvider>
      <EditorWorkspace store={store} sync={fakeSync(opts.save ?? saved)} onReload={() => {}} />
    </MantineProvider>,
  );
  return store;
}

const trackNamed = (s: EditorStore, name: string) => s.getState().doc.tracks.find((t) => t.name === name);
const clipOn = (s: EditorStore, trackId: string, id: string) => s.getState().doc.tracks.find((t) => t.id === trackId)?.items.find((i) => i.id === id) as MediaClip | undefined;
const titles = () => show.mock.calls.map(([n]) => n.title);
const button = (name: string) => screen.getByRole('button', { name }) as HTMLButtonElement;

describe('Media workspace: marks -> placed range', () => {
  // Source 25 fps, In 25 / Out 75 (Out exclusive) = source seconds [1, 3) -> timeline (30 fps) sourceIn 30, 60 frames.
  const MARKS: Marks = { inFrame: 25, outFrame: 75 };

  it('Create subclip appends the marked range, end-exclusive, with its sound, as one undo step', () => {
    env.marks = MARKS;
    const store = mount(baseDoc());
    const before = store.getState().doc;
    fireEvent.click(button('Create subclip'));

    const [clipId] = store.getState().ui.selection;
    const picture = clipOn(store, TRACKS.v1, clipId)!;
    expect(picture).toMatchObject({ recordIn: 60, recordOut: 120, sourceIn: 30, name: createSubclip(poolItem(), 25, 75, 'interview.mp4')!.name });
    const sound = trackNamed(store, PAIRED_AUDIO_TRACK_NAME)!;
    expect(sound.kind).toBe('audio');
    expect(sound.items).toHaveLength(1);
    expect(sound.items[0]).toMatchObject({ recordIn: 60, recordOut: 120, sourceIn: 30, assetRef: assetRef(1) });
    expect(sound.items[0].id).not.toBe(clipId);

    expect(store.getState().history).toHaveLength(1);
    store.undo();
    expect(store.getState().doc).toEqual(before);
  });

  it('the clip actions say what goes in, and Insert at playhead places the marked range there', () => {
    env.marks = MARKS;
    const store = mount(baseDoc(), { playheadFrame: 60 });
    expect(screen.getByTestId('media-insert-length').textContent).toBe(`Goes in as the marked range: 00:00:02:00, with its sound on the ${PAIRED_AUDIO_TRACK_NAME} track.`);
    expect(screen.queryByTestId('media-length-unknown')).toBeNull();

    fireEvent.click(button('Insert at playhead'));
    const [clipId] = store.getState().ui.selection;
    expect(clipOn(store, TRACKS.v1, clipId)).toMatchObject({ recordIn: 60, recordOut: 120, sourceIn: 30 });
    expect(store.getState().ui.playheadFrame).toBe(60);
    expect(trackNamed(store, PAIRED_AUDIO_TRACK_NAME)?.items[0]).toMatchObject({ recordIn: 60, recordOut: 120, sourceIn: 30 });
    expect(store.getState().history).toHaveLength(1);
  });

  it('an unprobed source says its length is unknown and goes in at 5 s without a companion', () => {
    const store = mount(baseDoc(), { pool: [poolItem({ probe: null })] });
    expect(screen.getByTestId('media-length-unknown').textContent).toBe(LENGTH_UNKNOWN_NOTE);
    expect(screen.getByTestId('media-insert-length').textContent).toBe('Goes in at full length: 00:00:05:00.');

    fireEvent.click(button('Add to end'));
    const [clipId] = store.getState().ui.selection;
    expect(clipOn(store, TRACKS.v1, clipId)).toMatchObject({ recordIn: 60, recordOut: 210, sourceIn: 0 });
    expect(trackNamed(store, PAIRED_AUDIO_TRACK_NAME)).toBeUndefined();
  });
});

describe('Media workspace: refusals leave the document untouched', () => {
  it('read-only (save conflict): buttons disabled, and Create subclip is refused with a notice', () => {
    env.marks = { inFrame: 25, outFrame: 75 };
    const store = mount(baseDoc(), { save: { ...saved, status: 'conflict', conflictRevision: 2 } });
    expect(button('Insert at playhead').disabled).toBe(true);
    expect(button('Add to end').disabled).toBe(true);
    fireEvent.click(button('Create subclip'));
    expect(titles()).toEqual(['Read-only']);
    expect(store.getState().history).toHaveLength(0);
  });

  it('no unlocked video track: "Nowhere to put it"', () => {
    const store = mount(baseDoc({ locked: true }), { targetedTrackIds: [] });
    fireEvent.click(button('Add to end'));
    expect(titles()).toEqual(['Nowhere to put it']);
    expect(store.getState().history).toHaveLength(0);
  });

  it('a TARGETED locked track: the engine refuses the batch and says so', () => {
    const store = mount(baseDoc({ locked: true }));
    expect(store.getState().ui.targetedTrackIds).toEqual([TRACKS.v1]);
    fireEvent.click(button('Add to end'));
    expect(titles()).toEqual(['Edit refused']);
    expect(store.getState().history).toHaveLength(0);
  });

  it('a source with no addressable ref: "Could not place it"', () => {
    const store = mount(baseDoc(), { pool: [poolItem({ mediaRef: 'nonsense' as MediaPoolItem['mediaRef'] })] });
    fireEvent.click(button('Add to end'));
    expect(titles()).toEqual(['Could not place it']);
    expect(store.getState().history).toHaveLength(0);
  });
});

describe('Inspector: Extract audio', () => {
  it('puts the clip sound on the paired track at the same span and offset, one undo step', () => {
    const store = mount(baseDoc({}, { sourceIn: 12 }), { workspace: 'edit', selection: ['c1'] });
    expect(screen.getByTestId('inspector-extract-audio-note').textContent).toBe(`Puts this clip's sound on the ${PAIRED_AUDIO_TRACK_NAME} track, under the picture.`);
    fireEvent.click(button('Extract audio'));

    const sound = trackNamed(store, PAIRED_AUDIO_TRACK_NAME)!;
    expect(sound.items).toHaveLength(1);
    expect(sound.items[0]).toMatchObject({ recordIn: 0, recordOut: 60, sourceIn: 12, assetRef: assetRef(1) });
    expect(store.getState().history).toHaveLength(1);
    expect(store.getState().history[0].label).toBe('Extract audio');
    expect(clipOn(store, TRACKS.v1, 'c1')).toMatchObject({ recordIn: 0, recordOut: 60 });
  });

  it('a source the probe says is silent: disabled, and says the file has no sound', () => {
    mount(baseDoc(), { workspace: 'edit', selection: ['c1'], pool: [poolItem({ probe: probe({ audioCodec: null, audioChannels: null, audioSampleRateHz: null }) })] });
    expect(button('Extract audio').disabled).toBe(true);
    expect(screen.getByTestId('inspector-extract-audio-note').textContent).toBe('This file has no sound.');
  });

  it('a freeze frame and a read-only timeline: disabled', () => {
    mount(baseDoc({}, { hold: true }), { workspace: 'edit', selection: ['c1'] });
    expect(button('Extract audio').disabled).toBe(true);
    cleanup();
    mount(baseDoc(), { workspace: 'edit', selection: ['c1'], save: { ...saved, status: 'conflict', conflictRevision: 2 } });
    expect(button('Extract audio').disabled).toBe(true);
  });

  it('a clip already on an audio track gets no Extract audio', () => {
    mount(doc([track(TRACKS.v1, 'video', []), track(TRACKS.a1, 'audio', [media('a1', 0, 60)])]), { workspace: 'edit', selection: ['a1'] });
    expect(screen.getByTestId('inspector-panel')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Extract audio' })).toBeNull();
  });
});
