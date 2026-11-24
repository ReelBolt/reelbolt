import { describe, expect, it } from 'vitest';
import {
  candidateLabel,
  defaultFormatForApp,
  fileMediaRef,
  formatsForApp,
  handOffFailureWords,
  handOffMediaState,
  handOffSizeLine,
  importHeadline,
  importSheetModel,
  initialMediaChoice,
  matchedHowWords,
  offlineReasonWords,
  rankCandidates,
  sizeWords,
  HOW_TO_OPEN,
} from '../handoff-ui';

describe('hand-off helpers (R12.2)', () => {
  it('maps editors to file types', () => {
    expect(defaultFormatForApp('premiere')).toBe('otio');
    expect(defaultFormatForApp('fcp')).toBe('fcpxml');
    expect(defaultFormatForApp('other')).toBe('otio');
    expect(defaultFormatForApp('cuts')).toBe('edl');
    expect(formatsForApp('resolve')).toEqual(['otio', 'fcpxml']);
  });

  it('words sizes', () => {
    expect(sizeWords(0)).toBe('0 KB');
    expect(sizeWords(12 * 1024)).toBe('12 KB');
    expect(sizeWords(820 * 1024 * 1024)).toBe('820 MB');
    expect(sizeWords(1.4 * 1024 ** 3)).toBe('1.4 GB');
    expect(handOffSizeLine({ mediaFiles: 12, mediaBytes: 1.4 * 1024 ** 3 })).toBe('12 files, 1.4 GB');
    expect(handOffSizeLine({ mediaFiles: 1, mediaBytes: 2048 })).toBe('1 file, 2 KB');
    expect(handOffSizeLine({ mediaFiles: 0, mediaBytes: 0 })).toBe('No media files');
  });

  it('reads the cap states', () => {
    expect(handOffMediaState(undefined).kind).toBe('unknown');
    expect(handOffMediaState({ supported: false, mediaFiles: 0, mediaBytes: 0, wouldExceedCap: false, capBytes: 0 }).kind).toBe('unsupported');
    const ok = handOffMediaState({ supported: true, mediaFiles: 3, mediaBytes: 1024 ** 2, wouldExceedCap: false, capBytes: 2 * 1024 ** 3 });
    expect(ok).toEqual({ kind: 'ok', line: '3 files, 1 MB' });
    expect(initialMediaChoice(ok)).toBe('zip');
    const over = handOffMediaState({ supported: true, mediaFiles: 9, mediaBytes: 3 * 1024 ** 3, wouldExceedCap: true, capBytes: 2 * 1024 ** 3 });
    expect(over.kind).toBe('overCap');
    expect(initialMediaChoice(over)).toBe('manifest');
    if (over.kind === 'overCap') expect(over.reason).toContain('3 GB');
  });

  it('words refusals', () => {
    expect(handOffFailureWords(429, 'HANDOFF_BUSY', null)).toMatch(/already being prepared/);
    expect(handOffFailureWords(409, 'HANDOFF_TOO_LARGE', 'Too big for one package.')).toBe('Too big for one package.');
    expect(handOffFailureWords(409, 'HANDOFF_TOO_LARGE', null)).toMatch(/List files only/);
    expect(handOffFailureWords(404, null, null)).toMatch(/could not be found/);
    expect(handOffFailureWords(500, null, null)).toMatch(/could not be prepared/);
  });

  it('never claims a tested app and has no em dashes', () => {
    for (const lines of Object.values(HOW_TO_OPEN)) {
      expect(lines).toHaveLength(3);
      for (const line of lines) {
        expect(line).not.toMatch(/tested in/i);
        expect(line).not.toContain('—');
      }
      expect(lines[2]).toMatch(/^Should open in .*Tell us if it does not\.$/);
    }
  });
});

describe('import result helpers (R12.3)', () => {
  it('words the reasons', () => {
    expect(offlineReasonWords('notFound')).toBe('No file with this name in the project');
    expect(offlineReasonWords('ambiguous')).toBe('Two files share this name; pick one');
    expect(offlineReasonWords('durationMismatch')).toBe("The file's length differs from the clip");
    expect(offlineReasonWords(undefined)).toBe('This file could not be matched');
    expect(matchedHowWords('nameAndDuration')).toBe('Matched by name and length');
    expect(importHeadline(3, 0)).toBe('3 files matched. Nothing is offline.');
    expect(importHeadline(1, 2)).toBe('1 file matched, 2 offline.');
  });

  it('ranks candidates by closeness of length, then size', () => {
    const a = { fileId: 'a', name: 'x.mov', durationSec: 10, sizeBytes: 100 };
    const b = { fileId: 'b', name: 'x.mov', durationSec: 30, sizeBytes: 500 };
    const c = { fileId: 'c', name: 'x.mov', durationSec: null, sizeBytes: 900 };
    expect(rankCandidates([c, b, a], 28).map((x) => x.fileId)).toEqual(['b', 'a', 'c']);
    expect(rankCandidates([a, b, c]).map((x) => x.fileId)).toEqual(['b', 'a', 'c']);
    expect(candidateLabel({ fileId: 'a', name: 'x.mov', durationSec: 125, sizeBytes: 340 * 1024 * 1024 })).toBe('x.mov, 2:05, 340 MB');
    expect(fileMediaRef('abc')).toBe('file:abc');
  });

  it('builds the sheet from the 201 body, candidates only for ambiguous rows', () => {
    const model = importSheetModel({
      matched: [{ name: 'a.mov', fileId: '1', how: 'name' }],
      offline: [
        { itemId: 'm1', name: 'Shared.mov', reason: 'ambiguous' },
        { itemId: 'm2', name: 'gone.mov', reason: 'notFound' },
      ],
      ambiguous: [{ name: 'shared.mov', candidates: [{ fileId: 'a', name: 'Shared.mov', durationSec: 5, sizeBytes: 10 }] }],
    });
    expect(model.matched).toHaveLength(1);
    expect(model.offline[0].candidates).toHaveLength(1);
    expect(model.offline[0].reasonWords).toMatch(/pick one/);
    expect(model.offline[1].candidates).toEqual([]);
    expect(importSheetModel({}).offline).toEqual([]);
  });
});
