/**
 * "Design a graphic": the editor-side rules around a design job, kept out of React so they can be
 * tested on their own. What the panel offers (placements, the frame size), the request it posts,
 * which layers can be revised, what the cost line says, which jobs are still working (and where on
 * the canvas), and which finished jobs still have to be brought into the open document.
 *
 * Pure: no React, no DOM, no fetch.
 */

import { creditsNeededLine } from '@/lib/utils/billing';
import type { Region } from './ai-effect';
import type { EditorLayer } from './model';
import type {
  CreateImageGraphicRequest,
  GraphicJobStatus,
  GraphicPlacement,
  ImageGraphicEstimate,
  ImageGraphicJob,
} from '@/lib/types/image-document';

/** The description's length limit (the server refuses anything longer). */
export const GRAPHIC_DESCRIPTION_MAX = 2000;
/** The frame's edge limits for a custom size, in pixels. */
export const GRAPHIC_FRAME_MIN = 64;
export const GRAPHIC_FRAME_MAX = 4096;

/** The placement choices, in the order the panel lists them, with plain names. */
export const GRAPHIC_PLACEMENT_OPTIONS: readonly { value: GraphicPlacement; label: string }[] = [
  { value: 'center', label: 'Centre' },
  { value: 'top', label: 'Top' },
  { value: 'bottom', label: 'Bottom' },
  { value: 'left', label: 'Left' },
  { value: 'right', label: 'Right' },
  { value: 'topLeft', label: 'Top left' },
  { value: 'topRight', label: 'Top right' },
  { value: 'bottomLeft', label: 'Bottom left' },
  { value: 'bottomRight', label: 'Bottom right' },
];

/** What the job is doing, in plain words. */
export const GRAPHIC_STATUS_LABEL: Record<GraphicJobStatus, string> = {
  Queued: 'Waiting to start',
  Running: 'Designing',
  Passed: 'Done',
  Failed: 'Did not work',
  Cancelled: 'Stopped',
};

/** True while a job may still change: it is waiting or being designed. */
export function isGraphicJobActive(job: Pick<ImageGraphicJob, 'status'>): boolean {
  return job.status === 'Queued' || job.status === 'Running';
}

/** The facts "Change this graphic" needs from a designed layer, or null when it cannot be revised. */
export interface RevisableGraphic {
  layerId: string;
  /** What the graphic was designed from (the original description). */
  prompt: string | null;
  graphicId: string | null;
  workspaceVersionId: string;
}

/**
 * A layer can be revised when an AI designer made it and its design version is known — that version
 * is what the designer opens again. Any other layer (a photo, an AI fill, text) is not a design.
 */
export function revisableGraphic(layer: Pick<EditorLayer, 'id' | 'provenance'> | null | undefined): RevisableGraphic | null {
  const p = layer?.provenance;
  if (!layer || !p || p.operation !== 'designGraphic') return null;
  if (typeof p.workspaceVersionId !== 'string' || p.workspaceVersionId.length === 0) return null;
  return {
    layerId: layer.id,
    prompt: typeof p.prompt === 'string' && p.prompt ? p.prompt : null,
    graphicId: typeof p.graphicId === 'string' ? p.graphicId : null,
    workspaceVersionId: p.workspaceVersionId,
  };
}

/** The panel's form. `size: 'fit'` designs in the whole picture's frame. */
export interface GraphicForm {
  description: string;
  placement: GraphicPlacement;
  size: 'fit' | 'custom';
  width: number;
  height: number;
}

export function defaultGraphicForm(canvas: { width: number; height: number }): GraphicForm {
  return {
    description: '',
    placement: 'center',
    size: 'fit',
    width: clampFrame(Math.round(canvas.width / 2)),
    height: clampFrame(Math.round(canvas.height / 3)),
  };
}

function clampFrame(value: number): number {
  if (!Number.isFinite(value)) return GRAPHIC_FRAME_MIN;
  return Math.min(GRAPHIC_FRAME_MAX, Math.max(GRAPHIC_FRAME_MIN, Math.round(value)));
}

/**
 * Why the form cannot be sent yet, in plain words, or null when it can. The availability and the
 * cost are checked by the panel itself, since they come from the server.
 */
export function graphicFormBlocker(form: GraphicForm, revising: boolean): string | null {
  const text = form.description.trim();
  if (!text) return revising ? 'Say what should change.' : 'Describe the graphic you want.';
  if (text.length > GRAPHIC_DESCRIPTION_MAX) {
    return `Keep the description under ${GRAPHIC_DESCRIPTION_MAX} characters.`;
  }
  if (form.size === 'custom') {
    const ok = (v: number) => Number.isFinite(v) && v >= GRAPHIC_FRAME_MIN && v <= GRAPHIC_FRAME_MAX;
    if (!ok(form.width) || !ok(form.height)) {
      return `Width and height must each be between ${GRAPHIC_FRAME_MIN} and ${GRAPHIC_FRAME_MAX} pixels.`;
    }
  }
  return null;
}

/**
 * The body of `POST …/graphics`. The canvas size is always sent (the server uses it only when the
 * document stores none); the frame only for a custom size; `revisesLayerId` only when revising, and
 * then the description is the change request. A revision keeps the original's frame and place, so it
 * sends neither.
 */
export function buildGraphicRequest(
  form: GraphicForm,
  canvas: { width: number; height: number },
  revisesLayerId?: string | null,
): CreateImageGraphicRequest {
  const body: CreateImageGraphicRequest = {
    description: form.description.trim(),
    canvasWidth: canvas.width,
    canvasHeight: canvas.height,
  };
  if (revisesLayerId) {
    body.revisesLayerId = revisesLayerId;
    return body;
  }
  body.placement = form.placement;
  if (form.size === 'custom') {
    body.width = Math.round(form.width);
    body.height = Math.round(form.height);
  }
  return body;
}

/** A credit figure as people read it: whole numbers, or one decimal for small amounts. */
function credits(value: number): string {
  if (!Number.isFinite(value)) return '0';
  const rounded = value >= 10 ? Math.round(value) : Math.round(value * 10) / 10;
  return rounded.toLocaleString('en-US');
}

/** What the cost line says, what a design needs to start, and whether the run can be afforded. */
export interface GraphicCostLine {
  text: string;
  /** "Needs 30 credits available to start; it can cost more." — rounded up; null when nothing is held. */
  needs: string | null;
  affordable: boolean;
}

export function graphicCostLine(estimate: ImageGraphicEstimate | null | undefined): GraphicCostLine | null {
  if (!estimate) return null;
  if (!estimate.billingEnabled) return { text: 'Billing is off on this installation.', needs: null, affordable: true };
  const low = credits(estimate.lowCredits);
  const high = credits(estimate.highCredits);
  const range = low === high ? `About ${low} credits` : `About ${low}–${high} credits`;
  const needs = creditsNeededLine(estimate.requiredCredits);
  if (!estimate.affordable) {
    return { text: `${range}. You have ${credits(estimate.balanceCredits)} credits.`, needs, affordable: false };
  }
  return { text: `${range}.`, needs, affordable: true };
}

/** The canvas rectangle of a job's expected landing place, in the effect's own shape. */
export function graphicTargetRegion(job: Pick<ImageGraphicJob, 'target'>): Region | null {
  const t = job.target;
  if (!t) return null;
  if (![t.x, t.y, t.width, t.height].every(Number.isFinite) || t.width <= 0 || t.height <= 0) return null;
  return { x: t.x, y: t.y, w: t.width, h: t.height };
}

/** The newest job still working (the list is newest first), or null. */
export function activeGraphicJob(jobs: readonly ImageGraphicJob[] | null | undefined): ImageGraphicJob | null {
  return jobs?.find(isGraphicJobActive) ?? null;
}

/**
 * Finished jobs whose layer the open document does not have yet: `Passed`, carrying the inserted
 * layer, not already handled by this editor (`settled`), and not already in the document (it may
 * have arrived with an ordinary reload). Oldest first, so the layers land in the order they were made.
 *
 * A job finished long ago whose layer the user has since deleted is harmless here: bringing it in
 * reads the server's document, which no longer has that layer, so nothing appears.
 */
export function jobsToAdopt(
  jobs: readonly ImageGraphicJob[] | null | undefined,
  localLayerIds: ReadonlySet<string>,
  settled: ReadonlySet<string>,
): ImageGraphicJob[] {
  return (jobs ?? [])
    .filter((job) => job.status === 'Passed' && !!job.layer && !settled.has(job.id) && !localLayerIds.has(job.layer.id))
    .reverse();
}

/**
 * The latest stage caption of a design's live progress feed ("Designing your graphic"), or null.
 * `events` is newest first, as the execution stream keeps them; only `step.progress` carries a stage.
 */
export function latestProgressStage(
  events: readonly { type: string; payload: Record<string, unknown> }[],
): string | null {
  for (const event of events) {
    if (event.type !== 'step.progress') continue;
    const stage = event.payload.stage ?? event.payload.Stage;
    if (typeof stage === 'string' && stage.trim()) return stage.trim();
  }
  return null;
}


/**
 * Why a design failed, in words a person can use. The run's own message is kept when it already reads as a
 * sentence; a message that is really an infrastructure trace (a host name and port, a refused connection, a
 * timeout) becomes a plain one, so the panel never shows `Resource temporarily unavailable (sandbox-executor:8080)`.
 */
export function plainGraphicError(error: string | null | undefined): string {
  const text = (error ?? '').trim();
  if (!text) return 'The graphic could not be designed.';
  if (/[a-z0-9-]+:\d{2,5}\b|temporarily unavailable|connection refused|econnrefused|name or service not known|no such host|socket|http ?request|timed out|timeout/i.test(text)) {
    return 'The designer could not start on this installation, so no graphic was made. Try again in a moment, or ask whoever runs ReelBolt to check that designing is set up.';
  }
  return text;
}
