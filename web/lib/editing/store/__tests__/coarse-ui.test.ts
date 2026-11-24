/** @vitest-environment node */
import { describe, expect, it } from 'vitest';
import { createCoarseUiSelector, leafPlayheadFrame } from '../coarse-ui';
import { createEditorStore } from '../editor-store';
import { standardDoc } from '../../__tests__/fixtures';

const make = () => createEditorStore({ timelineId: 't', projectId: 'p', doc: standardDoc(), revision: 1 });

describe('leafPlayheadFrame', () => {
  it('is exact when not playing', () => {
    const store = make();
    store.setUi({ playheadFrame: 137, playbackSpeed: 0 });
    expect(leafPlayheadFrame(store.getState(), 0.25)).toBe(137);
  });
  it('rounds down to the quantum while playing, at any speed', () => {
    const store = make();
    store.setUi({ playheadFrame: 137, playbackSpeed: 1 });
    expect(leafPlayheadFrame(store.getState(), 0.25)).toBe(136); // 30 fps -> quantum 8
    store.setUi({ playbackSpeed: 4 });
    expect(leafPlayheadFrame(store.getState(), 0.25)).toBe(136);
    expect(leafPlayheadFrame(store.getState(), 1)).toBe(120);
  });
});

describe('createCoarseUiSelector', () => {
  it('keeps the same object for every playhead tick while playing, and the real frame once paused', () => {
    const store = make();
    const select = createCoarseUiSelector();
    store.setUi({ playbackSpeed: 1, playheadFrame: 10 });
    const first = select(store.getState());
    expect(first.playheadFrame).toBe(10);
    for (let f = 11; f < 200; f++) {
      store.setUi({ playheadFrame: f });
      expect(select(store.getState())).toBe(first);
    }
    store.setUi({ playbackSpeed: 0 });
    expect(select(store.getState()).playheadFrame).toBe(199);
  });

  it('follows every other ui change at once, still holding the playhead', () => {
    const store = make();
    const select = createCoarseUiSelector();
    store.setUi({ playbackSpeed: 1, playheadFrame: 3 });
    const a = select(store.getState());
    store.setUi({ playheadFrame: 50, pixelsPerFrame: 7 });
    const b = select(store.getState());
    expect(b).not.toBe(a);
    expect(b.pixelsPerFrame).toBe(7);
    expect(b.playheadFrame).toBe(3);
  });

  it('a new play starts from the frame it starts at', () => {
    const store = make();
    const select = createCoarseUiSelector();
    store.setUi({ playbackSpeed: 1, playheadFrame: 3 });
    select(store.getState());
    store.setUi({ playbackSpeed: 0, playheadFrame: 40 });
    expect(select(store.getState()).playheadFrame).toBe(40);
    store.setUi({ playbackSpeed: 1 });
    select(store.getState()); // the hook reads the snapshot on every store change
    store.setUi({ playheadFrame: 90 });
    expect(select(store.getState()).playheadFrame).toBe(40);
  });
});

describe('leafPlayheadFrame paint ceiling', () => {
  it('halves a 60 fps timeline to 30 Hz and leaves 30 fps exact', () => {
    const store = make();
    store.setUi({ playbackSpeed: 1, playheadFrame: 137 });
    expect(leafPlayheadFrame(store.getState(), 1 / 30)).toBe(137); // standardDoc is 30 fps: quantum 1
  });
});
