'use client';

/**
 * "Preview 3 s" (R10.4): the one client of the preview routes under
 * `/api/v1/projects/{projectId}/edit-timelines/{id}/render/preview` and the preview form of the estimate.
 * The server builds the render itself; nothing here names a preset, a kind or a storage location.
 */

import { apiFetch } from '@/lib/api/client';
import { editTimelineKey, type EditTimelineRenderEstimate } from '../sync/api';

export interface PreviewWindow {
  atFrame: number;
  seconds: number;
}

export interface PreviewStartResponse {
  executionId: string;
}

/** `GET …/render/preview/{jobId}`. */
export interface PreviewStatus {
  executionId?: string;
  status: string;
  errorMessage?: string | null;
  durationSec?: number | null;
  ready: boolean;
}

export interface Preview3sApi {
  estimate: (projectId: string, timelineId: string, window: PreviewWindow) => Promise<EditTimelineRenderEstimate>;
  start: (projectId: string, timelineId: string, window: PreviewWindow) => Promise<PreviewStartResponse>;
  status: (projectId: string, timelineId: string, jobId: string) => Promise<PreviewStatus>;
}

const base = (projectId: string, timelineId: string): string => `${editTimelineKey(projectId, timelineId)}/render`;

/** Same-origin URL the overlay's video element plays (cookie auth, Range). */
export const previewContentUrl = (projectId: string, timelineId: string, jobId: string): string =>
  `${base(projectId, timelineId)}/preview/${encodeURIComponent(jobId)}/content`;

export const preview3sApi: Preview3sApi = {
  estimate: (projectId, timelineId, window) =>
    apiFetch<EditTimelineRenderEstimate>(`${base(projectId, timelineId)}/estimate`, {
      method: 'POST',
      body: JSON.stringify({ preview: window }),
    }),
  start: (projectId, timelineId, window) =>
    apiFetch<PreviewStartResponse>(`${base(projectId, timelineId)}/preview`, {
      method: 'POST',
      body: JSON.stringify(window),
    }),
  status: (projectId, timelineId, jobId) =>
    apiFetch<PreviewStatus>(`${base(projectId, timelineId)}/preview/${encodeURIComponent(jobId)}`),
};
