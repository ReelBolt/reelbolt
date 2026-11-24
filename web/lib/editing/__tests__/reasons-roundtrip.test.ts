import { describe, expect, it } from 'vitest';
import { IntlMessageFormat } from 'intl-messageformat';
import en from '@/messages/en/editor-lib.json';
import it_ from '@/messages/it/editor-lib.json';
import { ok } from './helpers/catalog';
import { describeOp } from '../store/editor-store';
import { statusSentence } from '../humanize-error';
import { refusalSentence } from '../refusal';
import { micErrorSentence, VO_LIMIT_MESSAGE } from '../audio/vo-recorder';
import { summarize } from '../link/migrate';
import { describePreset } from '../preset-manage';
import { READ_ONLY_MESSAGE } from '../store/editor-store';
import * as L from '../labels-msg';
import * as S from '../summary-msg';
import { reasonMsg } from '../reason-msg';

type Tree = { [k: string]: string | Tree };
const leaves = (t: Tree, path = ''): [string, string][] => Object.entries(t).flatMap(([k, v]) => (typeof v === 'string' ? [[path + k, v] as [string, string]] : leaves(v, `${path}${k}.`)));

describe('editor-lib catalogues', () => {
  it('have the same keys and placeholders in en and it, and every message parses as ICU', () => {
    const e = new Map(leaves(en as Tree));
    const i = new Map(leaves(it_ as Tree));
    expect([...i.keys()].sort()).toEqual([...e.keys()].sort());
    const names = (s: string) => [...new Set([...s.matchAll(/\{(\w+)\s*[,}]/g)].map((m) => m[1]).filter((n) => !['one', 'other'].includes(n)))].sort();
    for (const [k, v] of e) {
      expect(() => new IntlMessageFormat(v, 'en'), `en ${k}`).not.toThrow();
      expect(() => new IntlMessageFormat(i.get(k) as string, 'it'), `it ${k}`).not.toThrow();
      expect(names(i.get(k) as string), `placeholders ${k}`).toEqual(names(v));
    }
  });
});

describe('lib sentences map to keys', () => {
  it('history labels', () => {
    const ops = [
      { type: 'insert', item: { name: 'A', type: 'mediaClip' } }, { type: 'extract' }, { type: 'closeGaps' }, { type: 'removeMarker' }, { type: 'joinClips' }, { type: 'removeTrack' },
      { type: 'setTimelineSettings' }, { type: 'retime', playbackRate: 1.5 }, { type: 'removeTransition' }, { type: 'blade', atFrame: 10 }, { type: 'addTransition', transition: { transitionType: 'dissolve' } },
      { type: 'setProperty', key: 'opacity' }, { type: 'setInOut', inOut: null }, { type: 'nudge', deltaFrames: 3 }, { type: 'move', toRecordIn: 5 }, { type: 'trim', mode: 'ripple', edge: 'in', deltaFrames: -2 },
    ] as never[];
    for (const op of ops) {
      const label = describeOp(op);
      const m = L.historyLabelMsg(label);
      expect(m.$msg, label).not.toBe('editor-lib.common.raw');
      ok(m);
    }
  });
  it('status, refusal, sync, mic, undo, preset, link', () => {
    for (const s of [401, 402, 403, 404, 409, 413, 429, 500]) { const m = L.humanizeErrorMsg(statusSentence(s) as string); expect(m.$msg).not.toBe('editor-lib.common.raw'); ok(m); }
    expect(L.refusalSentenceMsg(refusalSentence('track \'abc\' is locked')).$msg).toBe('editor-lib.refusal.lockedUnnamed');
    expect(L.refusalSentenceMsg(refusalSentence('unknown item id x')).$msg).toBe('editor-lib.refusal.noItem');
    for (const name of ['NotAllowedError', 'NotFoundError', 'NotReadableError', 'OverconstrainedError', 'AbortError', 'x']) { const m = L.voTextMsg(micErrorSentence({ name })); expect(m.$msg).not.toBe('editor-lib.common.raw'); }
    expect(L.voTextMsg(VO_LIMIT_MESSAGE).$msg).not.toBe('editor-lib.common.raw');
    expect(L.syncMessageMsg(READ_ONLY_MESSAGE).$msg).toBe('editor-lib.sync.readOnly');
    ok(L.stepRefusedTitleMsg('Could not undo'));
    for (const [l, a] of [[0, 0], [2, 0], [0, 3], [2, 3]]) { const m = S.linkSummaryMsg(l, a); ok(m); expect(summarize(l, a).length).toBeGreaterThan(0); }
    expect(reasonMsg('sync', 'And 3 more problems.').values).toEqual({ n: 3 });
    expect(describePreset('effects' as never, {})).toBeTruthy(); ok(S.describePresetMsg('effects', {})); ok(S.describePresetMsg('mastering', { targetLufs: -14, truePeakDbtp: -1, loudnessRange: 11 })); ok(S.describePresetMsg('transition', { framesBefore: 5, framesAfter: 5, transitionType: 'dissolve', easing: 'linear' }));
    ok(S.diffNoneMsg()); ok(S.finishedRenderMsg()); ok(S.trackedWindowNoteMsg()); ok(S.frameHandleLabelMsg('window'));
    ok(L.panLabelMsg(-0.3)); ok(L.panLabelMsg(0.5)); ok(L.poolSortLabelMsg('added')); ok(L.poolStateLabelMsg('all')); ok(L.captionLookLabelMsg('spotlight')); ok(L.dspStageLabelMsg('gate'));
    ok(L.placeModeLabelMsg('placeOnTop')); ok(L.placeTrackNameMsg('video', 2)); ok(L.timeFormatLabelMsg('square')); ok(L.fieldLabelMsg('opacity')); ok(L.micLabelMsg(1));
  });
});
