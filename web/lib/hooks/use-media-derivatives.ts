'use client';

import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import { useEffect, useRef } from 'react';
import { useWorkflowEvents as useSharedWorkflowEvents } from '../activity/events';
import { ApiError } from '../api/client';
import { outputCheckDelay, retryWhilePending } from '../outputs/output-check';
import { requestMediaDerivatives } from '../editing/api/media';
import type { EditTimeline } from '../editing/types';
import type { MediaRef } from '../editing/media/types';

/** The SSE `event:` name the Go API relays derivative progress as (`rabbitmq_service.go`). */
export const MEDIA_DERIVATIVE_PROGRESS_EVENT = 'media.derivative.progress';

/** The closed stage vocabulary (`MediaDerivativeProgress.Stage`). */
export type MediaDerivativeStage =
  | 'Queued'
  | 'Probing'
  | 'Proxy'
  | 'Filmstrip'
  | 'Peaks'
  | 'KeyframeIndex'
  | 'Stabilize'
  | 'Done'
  | 'Skipped'
  | 'Failed';

const MEDIA_DERIVATIVE_TYPES = [MEDIA_DERIVATIVE_PROGRESS_EVENT];

const STAGES: readonly string[] = ['Queued', 'Probing', 'Proxy', 'Filmstrip', 'Peaks', 'KeyframeIndex', 'Stabilize', 'Done', 'Skipped', 'Failed'];

export interface MediaDerivativeProgressEvent {
  /** `file:<id>` for a project file; null for an unregistered render (the relay carries no id for it). */
  mediaRef: MediaRef | null;
  stage: MediaDerivativeStage;
  percentComplete: number | null;
}

/** True for the stages after which no more work is coming for the run. */
export const isTerminalStage = (stage: MediaDerivativeStage): boolean => stage === 'Done' || stage === 'Skipped' || stage === 'Failed';

/** True once the run is past probing: the source's ingest probe is stored, so a cached media pool row is stale. */
export const poolStageStoresProbe = (stage: MediaDerivativeStage): boolean => stage === 'Proxy' || stage === 'Filmstrip' || stage === 'Peaks' || stage === 'KeyframeIndex' || stage === 'Done';

/** Plain words for a stage, for the media pane. Pass `useT()` for the active language. */
export function stageWords(stage: MediaDerivativeStage, t: T = enT): string {
  return t(`libLabels.hooks.stage.${stage}`);
}

/** One sentence for the media pane: where the file's preparation is. */
export function progressSentence(event: Pick<MediaDerivativeProgressEvent, 'stage' | 'percentComplete'>, t: T = enT): string {
  if (event.stage === 'Done') return t('libLabels.hooks.progressDone');
  if (event.stage === 'Skipped') return t('libLabels.hooks.progressSkipped');
  if (event.stage === 'Failed') return t('libLabels.hooks.progressFailed');
  const percent = event.percentComplete !== null ? t('libLabels.hooks.progressPercent', { percent: String(event.percentComplete) }) : '';
  return t('libLabels.hooks.progressActive', { stage: stageWords(event.stage, t), percent });
}

/**
 * Parses one `media.derivative.progress` frame (`{type, projectId, timestamp, data}`) for
 * `projectId`; null for another project's event or a malformed frame.
 */
export function parseMediaDerivativeProgress(raw: string, projectId: string): MediaDerivativeProgressEvent | null {
  let message: unknown;
  try {
    message = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!message || typeof message !== 'object') return null;
  const envelope = message as { projectId?: unknown; data?: unknown };
  const data = (envelope.data && typeof envelope.data === 'object' ? envelope.data : {}) as Record<string, unknown>;
  const eventProject = typeof envelope.projectId === 'string' ? envelope.projectId : typeof data.projectId === 'string' ? data.projectId : null;
  if (!eventProject || eventProject.toLowerCase() !== projectId.toLowerCase()) return null;
  const stage = typeof data.stage === 'string' && STAGES.includes(data.stage) ? (data.stage as MediaDerivativeStage) : null;
  if (!stage) return null;
  const fileId = typeof data.sourceProjectFileId === 'string' && data.sourceProjectFileId.length > 0 ? data.sourceProjectFileId : null;
  const percent = typeof data.percentComplete === 'number' && Number.isFinite(data.percentComplete) ? Math.max(0, Math.min(100, data.percentComplete)) : null;
  return { mediaRef: fileId ? `file:${fileId}` : null, stage, percentComplete: percent };
}

/**
 * Subscribes to the derivative pipeline's progress for one project, on the Go API's global SSE
 * feed (`/api/v1/workflows/events`, which scopes these events by project), through the shared
 * stream in lib/activity/events.ts, which re-opens when the active workspace changes. The callback may change between renders without re-opening the stream.
 */
export function useMediaDerivativeProgress(
  projectId: string | null | undefined,
  onProgress: (event: MediaDerivativeProgressEvent) => void,
): void {
  const callback = useRef(onProgress);
  useEffect(() => {
    callback.current = onProgress;
  });

  useSharedWorkflowEvents(MEDIA_DERIVATIVE_TYPES, (_type, _frame, raw) => {
    if (!projectId) return;
    const parsed = parseMediaDerivativeProgress(raw, projectId);
    if (parsed) callback.current(parsed);
  });
}

/** Why the timeline asked: carried onto the derivative request event. */
export const REQUESTED_BY_TIMELINE_OPEN = 'timeline-open';

/** Every source a document's clips (on its tracks, in its sequences and multicam angles) point at. */
export function timelineMediaRefs(doc: EditTimeline): MediaRef[] {
  const refs = new Set<MediaRef>();
  const visit = (value: unknown, depth: number) => {
    if (depth > 8 || !value || typeof value !== 'object') return;
    if (Array.isArray(value)) {
      for (const entry of value) visit(entry, depth + 1);
      return;
    }
    const record = value as Record<string, unknown>;
    const asset = record.assetRef;
    if (asset && typeof asset === 'object') {
      const a = asset as { kind?: unknown; projectFileId?: unknown; stepResultId?: unknown };
      if (a.kind === 'projectFile' && typeof a.projectFileId === 'string' && a.projectFileId) refs.add(`file:${a.projectFileId}`);
      if (a.kind === 'stepResult' && typeof a.stepResultId === 'string' && a.stepResultId) refs.add(`step_result:${a.stepResultId}`);
    }
    for (const key of ['tracks', 'items', 'sequences', 'angles']) if (key in record) visit(record[key], depth + 1);
  };
  visit(doc, 0);
  return [...refs];
}

/**
 * Opening a timeline asks for the derivatives (proxy, filmstrip, peaks…) of every source its clips
 * use: ONE `POST …/media/derivatives/request` per source per open, sent one after another, never
 * from a hover. A source that appears later (a pool insert) is asked for once too. The server
 * answers 202 and skips work already done, so a request for a ready source costs nothing.
 * Failures are ignored: the request is a courtesy, and the media pane offers the explicit button.
 */
export function useRequestTimelineDerivatives(projectId: string | null | undefined, timelineId: string | null | undefined, doc: EditTimeline): void {
  const asked = useRef<{ key: string; refs: Set<MediaRef> }>({ key: '', refs: new Set() });
  const queue = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    if (!projectId || !timelineId) return;
    const key = `${projectId}/${timelineId}`;
    if (asked.current.key !== key) asked.current = { key, refs: new Set() };
    const fresh = timelineMediaRefs(doc).filter((ref) => !asked.current.refs.has(ref));
    for (const ref of fresh) {
      asked.current.refs.add(ref);
      queue.current = queue.current.then(() =>
        requestMediaDerivatives(projectId, { mediaRef: ref, requestedBy: REQUESTED_BY_TIMELINE_OPEN }).then(
          () => undefined,
          (error: unknown) => {
            // A source still being checked answers "pending": it is asked again later on the back-off, off the queue.
            if (error instanceof ApiError && error.code === 'CONTENT_MODERATION_PENDING') {
              const again = () => requestMediaDerivatives(projectId, { mediaRef: ref, requestedBy: REQUESTED_BY_TIMELINE_OPEN });
              void new Promise<void>((resolve) => setTimeout(resolve, outputCheckDelay(0)))
                .then(() => retryWhilePending(again, { isCancelled: () => asked.current.key !== key }))
                .catch(() => undefined);
            }
          },
        ),
      );
    }
  }, [projectId, timelineId, doc]);
}
