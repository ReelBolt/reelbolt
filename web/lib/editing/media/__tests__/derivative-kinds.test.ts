/** @vitest-environment node */
/** Audit d2 N5 / RD-15: only kinds the pipeline produces are advertised as chips. */

import { describe, expect, it } from 'vitest';

import { derivativeKindsToShow, expectedDerivativeKinds } from '../derivative-kinds';
import { AUDIO_FILE_ITEM, IMAGE_FILE_ITEM, STEP_RESULT_ITEM, VIDEO_FILE_ITEM } from '../fixtures';

describe('expectedDerivativeKinds', () => {
  it('never advertises thumbnail or waveform, which the pool pipeline does not produce', () => {
    for (const item of [VIDEO_FILE_ITEM, STEP_RESULT_ITEM, AUDIO_FILE_ITEM, IMAGE_FILE_ITEM]) {
      expect(expectedDerivativeKinds(item)).not.toContain('thumbnail');
      expect(expectedDerivativeKinds(item)).not.toContain('waveform');
    }
  });

  it('video gets proxy, filmstrip, keyframe index, and peaks only when it may have sound', () => {
    expect(expectedDerivativeKinds(VIDEO_FILE_ITEM)).toEqual(['proxy', 'filmstrip', 'keyframeIndex', 'waveformPeaks']);
    const silent = { ...VIDEO_FILE_ITEM, probe: { ...VIDEO_FILE_ITEM.probe!, audioCodec: null, audioChannels: 0 } };
    expect(expectedDerivativeKinds(silent)).toEqual(['proxy', 'filmstrip', 'keyframeIndex']);
    expect(expectedDerivativeKinds(STEP_RESULT_ITEM)).toContain('waveformPeaks');
  });

  it('audio gets peaks only; a still gets nothing', () => {
    expect(expectedDerivativeKinds(AUDIO_FILE_ITEM)).toEqual(['waveformPeaks']);
    expect(expectedDerivativeKinds(IMAGE_FILE_ITEM)).toEqual([]);
  });

  it('a row that exists is always shown', () => {
    expect(derivativeKindsToShow(IMAGE_FILE_ITEM, [{ kind: 'thumbnail' }])).toEqual(['thumbnail']);
  });
});
