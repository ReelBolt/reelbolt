import { describe, expect, it } from 'vitest';
import { ok } from '../../__tests__/helpers/catalog';
import { FORMAT_LABELS, FORMAT_SUMMARY, handOffLines } from '../hand-off';
import { HAND_OFF_MEDIA_OPTIONS, HOW_TO_OPEN, EDITOR_APPS, handOffFailureWords, sizeWords, importHeadline, offlineReasonWords, matchedHowWords } from '../handoff-ui';
import * as L from '../labels-msg';

describe('interchange labels', () => {
  it('resolves every key and mirrors the English structure', () => {
    for (const f of Object.keys(FORMAT_LABELS) as (keyof typeof FORMAT_LABELS)[]) { ok(L.formatLabelMsg(f)); ok(L.formatSummaryMsg(f)); }
    for (const f of ['otio', 'fcpxml', 'edl'] as const) ok(L.handOffFileTypeMsg(f));
    for (const a of EDITOR_APPS) ok(L.editorAppLabelMsg(a.value, a.label));
    for (const o of HAND_OFF_MEDIA_OPTIONS) ok(L.handOffMediaChoiceMsg(o.value));
    for (const [app, lines] of Object.entries(HOW_TO_OPEN)) {
      const msgs = L.howToOpenMsgs(app as keyof typeof HOW_TO_OPEN);
      expect(msgs).toHaveLength(lines.length);
      msgs.forEach(ok);
    }
    const counts = { videoClips: 2, audioClips: 1, titles: 1, captions: 3, markers: 1 };
    for (const f of ['otio', 'edl', 'srt', 'vtt', 'fcpxml'] as const) {
      const lines = L.handOffLinesMsg(f, counts);
      expect(lines).toHaveLength(handOffLines(f, counts).length);
      lines.forEach(ok);
    }
    ok(L.mediaSentenceMsg('none')); ok(L.mediaSentenceMsg('reelbolt-url'));
    for (const b of [0, 500, 5000, 5e6, 5e9]) { ok(L.sizeWordsMsg(b)); expect(L.sizeWordsMsg(b).values?.value).toBeDefined(); void sizeWords(b); }
    ok(L.handOffSizeLineMsg({ mediaFiles: 0, mediaBytes: 0 })); ok(L.handOffSizeLineMsg({ mediaFiles: 2, mediaBytes: 9e6 }));
    ok(L.handOffOverCapMsg({ mediaBytes: 5e9, capBytes: 2e9 }));
    for (const [s, c] of [[409, null], [429, null], [404, null], [401, null], [500, null]] as const) { ok(L.handOffFailureMsg(s, c, null)); void handOffFailureWords(s, c, null); }
    for (const r of ['notFound', 'ambiguous', 'durationMismatch', 'x', undefined]) { ok(L.offlineReasonMsg(r)); void offlineReasonWords(r); }
    for (const h of ['id', 'nameAndDuration', 'name']) { ok(L.matchedHowMsg(h)); void matchedHowWords(h); }
    ok(L.importHeadlineMsg(3, 0)); ok(L.importHeadlineMsg(3, 2)); void importHeadline;
    ok(L.relinkLabelMsg());
  });
});
