/**
 * Which derivative kinds the media pipeline actually produces for a source — the chips the media
 * pane shows. Advertising a kind nothing produces leaves a chip reading "not generated yet" forever
 * (audit d2 N5, RD-15): the pool pipeline makes a proxy, a filmstrip and a keyframe index for video,
 * waveform peaks for anything with sound, and nothing for a still. `thumbnail` comes from the
 * Thumbnail workflow step, not from the pool, and `waveform` (the image) has no producer.
 */

import { hasAudioStream } from './pool-insert';
import type { MediaDerivativeKind, MediaDerivativeRow, MediaPoolItem } from './types';

export function expectedDerivativeKinds(item: Pick<MediaPoolItem, 'kind' | 'probe'>): MediaDerivativeKind[] {
  if (item.kind === 'image') return [];
  if (item.kind === 'audio') return ['waveformPeaks'];
  const kinds: MediaDerivativeKind[] = ['proxy', 'filmstrip', 'keyframeIndex'];
  // Unknown (not probed yet) keeps the chip: the run will find out.
  if (hasAudioStream(item) !== false) kinds.push('waveformPeaks');
  return kinds;
}

/** The chips to show: the expected kinds, plus any kind a row exists for (a fact is never hidden). */
export function derivativeKindsToShow(
  item: Pick<MediaPoolItem, 'kind' | 'probe'>,
  rows: readonly Pick<MediaDerivativeRow, 'kind'>[] | null,
): MediaDerivativeKind[] {
  const kinds = expectedDerivativeKinds(item);
  for (const row of rows ?? []) if (!kinds.includes(row.kind)) kinds.push(row.kind);
  return kinds;
}
