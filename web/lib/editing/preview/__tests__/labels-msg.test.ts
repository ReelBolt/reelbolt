import { describe, expect, it } from 'vitest';
import { ok } from '../../__tests__/helpers/catalog';
import { classifyItem } from '../parity';
import { PREPARE_FAILED_REASON, OPEN_FAILED_REASON, REQUEST_FAILED_REASON } from '../readiness';
import { REDUCED_QUALITY_SOFTWARE } from '../live-session';
import { NO_MEDIA_REASON } from '../proxy-urls';
import { HDR_PREVIEW_REASON, HDR_CLIP_NOTE } from '../../media/hdr';
import * as L from '../labels-msg';

const dsp = { worklet: false } as never;
const clip = (extra: object) => ({ id: 'a', type: 'mediaClip', name: 'x', recordIn: 0, recordOut: 10, ...extra }) as never;

describe('preview labels', () => {
  it('maps the parity reasons the classifier returns', () => {
    const items = [
      clip({ playbackRate: 0.5 }), clip({ playbackRate: 2 }), clip({ stabilize: {} }), clip({ cornerPin: {} }), clip({ mask: {} }), clip({ keyer: {} }),
      clip({ reframe: {} }), clip({ effects: [{ type: 'blur', amount: 1 }] }), clip({ channelMap: 'LL' }),
      { id: 't', type: 'titleClip', name: 'T', text: 'x', recordIn: 0, recordOut: 1 }, { id: 'c', type: 'caption', recordIn: 0, recordOut: 1, text: 'x' },
      { id: 'tr', type: 'transition', transitionType: 'wipeleft', recordIn: 0, recordOut: 1 }, { id: 'tr', type: 'transition', transitionType: 'dissolve', recordIn: 0, recordOut: 1 },
      { id: 'g', type: 'generatorClip', generatorType: 'bars', recordIn: 0, recordOut: 1 }, { id: 'adj', type: 'adjustmentClip', recordIn: 0, recordOut: 1 },
    ] as never[];
    for (const item of items) {
      const { reason } = classifyItem(item, undefined, { dsp });
      const m = L.parityReasonMsg(reason);
      expect(m.$msg, reason).not.toBe('editor-lib.common.raw');
      ok(m);
    }
  });
  it('maps the lib constants', () => {
    for (const c of [PREPARE_FAILED_REASON, OPEN_FAILED_REASON, REQUEST_FAILED_REASON]) ok(L.readinessWordsMsg({ state: 'failed', reason: c }));
    for (const c of [REDUCED_QUALITY_SOFTWARE, NO_MEDIA_REASON, HDR_PREVIEW_REASON, HDR_CLIP_NOTE]) { const m = L.previewNoteMsg(c); expect(m.$msg, c).not.toBe('editor-lib.common.raw'); ok(m); }
    ok(L.readinessWordsMsg({ state: 'ready', original: true } as never)); ok(L.readinessWordsMsg({ state: 'preparing', percent: 40 }));
    for (const kind of ['notReady', 'renderRequired', 'approximate', 'faithful'] as const) { ok(L.verdictLabelMsg(kind, { count: 2 })); ok(L.verdictLabelMsg(kind, { simple: true })); }
    ok(L.itemTitleMsg('mediaClip', 'Interview')); ok(L.itemTitleMsg('weird', ''));
    ok(L.simpleFallbackLineMsg(null)); ok(L.sessionUnavailableLineMsg(null)); ok(L.singleClipLineMsg(null));
    for (const q of ['auto', 'full', 'half', 'quarter'] as const) ok(L.previewQualityLabelMsg(q));
    ok(L.budgetReasonMsgs({ deviceClass: 'phone', cores: 2, memoryGb: 2, softwareGraphics: true }, 4, 4)[3]);
    ok(L.preview3sButtonMsg(null)); ok(L.preview3sButtonMsg(L.preview3sIncludedMsg())); ok(L.preview3sStatusMsg('Queued')); ok(L.preview3sStatusMsg('Running'));
    ok(L.honestyReasonMsg('Preparing preview…'));
  });
});
