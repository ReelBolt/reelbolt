import { msg, type Msg } from '@/i18n/msg';
import type { UploadEntry } from '@/lib/hooks/use-upload-queue';
import type { MediaDerivativeStage } from '@/lib/hooks/use-media-derivatives';
import type { MediaDerivativeRow, MediaPoolItem } from '@/lib/editing/media/types';

/** What a tile's readiness chip says. The words are the product; nothing here names a pipeline stage. */
export type TileReadiness = 'processing' | 'ready' | 'notReady';

export const READINESS_LABEL: Record<TileReadiness, string> = {
  processing: 'Processing preview',
  ready: 'Ready',
  notReady: 'Preview not ready - you can still add it',
};

export interface TileReads {
  /** `null` until the derivative rows were read. */
  rows: MediaDerivativeRow[] | null;
  error: string | null;
}

/**
 * One tile's readiness (R04.1, R05.7): from the stored probe, the derivative rows (a proxy row is the whole
 * state machine) and the pipeline's latest stage for the source. A picture is ready as soon as it is known.
 * "Not ready" never blocks adding: the sheet still offers Insert and Add to end.
 */
export function tileReadiness(item: MediaPoolItem, reads: TileReads | undefined, stage: MediaDerivativeStage | undefined): TileReadiness {
  if (stage === 'Failed') return 'notReady';
  const running = stage !== undefined && stage !== 'Done' && stage !== 'Skipped';
  if (item.kind === 'image') return running ? 'processing' : 'ready';
  if (reads?.rows?.some((row) => row.kind === 'proxy')) return 'ready';
  if (running) return 'processing';
  if (item.probe === null || reads === undefined || (reads.rows === null && reads.error === null)) return 'processing';
  return 'notReady';
}

/** One plain line for an upload row. */
export function uploadText(entry: UploadEntry): string {
  switch (entry.status) {
    case 'queued':
      return 'Waiting to upload';
    case 'uploading':
      return `Uploading ${entry.progress}%`;
    case 'done':
      return 'Added';
    case 'error':
      return entry.quotaExceeded ? 'Not uploaded: your storage is full' : 'Did not upload';
  }
}

/* ---- Msg counterparts ---- */

/** {@link READINESS_LABEL} as a Msg. */
export function readinessMsg(readiness: TileReadiness): Msg {
  return msg(`editor-helpers-1.phone.readiness.${readiness}`);
}

/** {@link uploadText} as a Msg. */
export function uploadMsg(entry: UploadEntry): Msg {
  switch (entry.status) {
    case 'queued':
      return msg('editor-helpers-1.phone.upload.queued');
    case 'uploading':
      return msg('editor-helpers-1.phone.upload.uploading', { progress: entry.progress });
    case 'done':
      return msg('editor-helpers-1.phone.upload.done');
    case 'error':
      return msg(entry.quotaExceeded ? 'editor-helpers-1.phone.upload.quota' : 'editor-helpers-1.phone.upload.error');
  }
}
