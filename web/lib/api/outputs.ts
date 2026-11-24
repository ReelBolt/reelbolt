import { apiFetch } from './client';

export interface OutputVideo {
  stepResultId: string;
  workflowExecutionId: string;
  workflowStepId: string;
  storageKey: string;
  fileName: string;
  producedAt: string;
  // Appended context (all optional so older servers still type-check): which workflow and run
  // produced the render, whether it is that run's finished video, its length and size, and the
  // run's customer warnings (contract C1).
  workflowDefinitionId?: string | null;
  workflowName?: string | null;
  runNumber?: number | null;
  runStartedAt?: string | null;
  executionStatus?: string | null;
  stepLabel?: string | null;
  stepType?: string | null;
  iterationNumber?: number | null;
  durationSec?: number | null;
  width?: number | null;
  height?: number | null;
  isFinal?: boolean;
  warnings?: { code: string; message: string }[] | null;
  // Takes (docs/video-editing.md "Takes: the latest render first"): every render of one workflow
  // step is a take of that step's video. takeIndex 1 is the latest take.
  takeGroupKey?: string | null;
  takeIndex?: number;
  takeCount?: number;
  isLatestTake?: boolean;
  /** A render its run replaced (a review-loop pass or a retry). */
  isIntermediate?: boolean;
  /** Pinned by the owner: never removed by "delete earlier takes". */
  isPinned?: boolean;
}

export function listOutputVideos(projectId: string): Promise<OutputVideo[]> {
  return apiFetch<OutputVideo[]>(`/api/v1/projects/${projectId}/outputs`);
}

/** Returns the URL used to stream/download the output video inline. */
export function getOutputVideoUrl(projectId: string, stepResultId: string): string {
  return `/api/v1/projects/${projectId}/outputs/${stepResultId}/download`;
}

/**
 * A small WebP preview of an output PICTURE, `width` px wide (server-allowed: 160, 320, 480, 640, 960, 1280).
 * For images only: the server ignores `w` for video. Use it for thumbnails; keep the full URL for full-size views and downloads.
 */
export function getOutputPreviewUrl(projectId: string, stepResultId: string, width: number): string {
  return `${getOutputVideoUrl(projectId, stepResultId)}?w=${width}`;
}

/**
 * The URL that SAVES an output under `fileName` (the stored name is an id): the endpoint answers an
 * attachment instead of an inline stream, so a click downloads a file rather than opening a bare player.
 */
export function getOutputDownloadUrl(projectId: string, stepResultId: string, fileName: string): string {
  return `${getOutputVideoUrl(projectId, stepResultId)}?saveAs=${encodeURIComponent(fileName)}`;
}

/** Deletes one earlier take (its video, and its entry in Files). The latest take and pinned takes are refused (409). */
export function deleteOutputTake(projectId: string, stepResultId: string): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${projectId}/outputs/${stepResultId}`, { method: 'DELETE' });
}

/** Pins or unpins a take; a pinned take is kept by every cleanup. */
export function setOutputTakePinned(projectId: string, stepResultId: string, pinned: boolean): Promise<{ pinned: boolean }> {
  return apiFetch(`/api/v1/projects/${projectId}/outputs/${stepResultId}/pin`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pinned }),
  });
}

/** "Delete earlier takes": keeps the newest `keepLatest` takes of one video and every pinned take. */
export function pruneOutputTakes(projectId: string, takeGroupKey: string, keepLatest = 1): Promise<{ deleted: number; kept: number }> {
  return apiFetch(`/api/v1/projects/${projectId}/outputs/prune`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ takeGroupKey, keepLatest }),
  });
}
