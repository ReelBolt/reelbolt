/**
 * What each AI operation of the thumbnail editor needs, in the words the AI panel shows, and the
 * function that turns the panel's fields into the contract's `OperationRequest`.
 *
 * The table mirrors section 5 of docs/design/thumbnail-editor.md; the operation names themselves
 * are pinned to it by `lib/types/__tests__/image-document-enum-drift.test.ts`. Nothing here talks to
 * the network. Which provider and model serve an operation is the server's decision (one default
 * row per capability, an explicit `providerId`, or a per-operation `model`); the panel only shows
 * what the server answered with.
 */

import { MAX_SUBJECTS, subjectLimit } from '@/lib/avatars/subjects';
import type { EditImageOperation, ImageOperationCapabilities, ImageOperationRequest } from '@/lib/types/image-document';

export type PromptRule = 'required' | 'optional' | 'none';

export type ParamField =
  | { key: string; label: string; kind: 'number'; min: number; max: number; step: number; defaultValue: number }
  | { key: string; label: string; kind: 'choice'; options: readonly number[]; defaultValue: number };

export interface OperationInfo {
  operation: EditImageOperation;
  title: string;
  /** One plain sentence: what it does to the picture. */
  summary: string;
  /** The button label. */
  action: string;
  /** Needs a bitmap layer selected. */
  needsLayer: boolean;
  /** Needs a brush mask painted on that layer (white = the area to act on). */
  needsMask: boolean;
  prompt: PromptRule;
  promptLabel: string;
  promptPlaceholder: string;
  params: readonly ParamField[];
}

const EDGE = (key: string, label: string): ParamField => ({
  key,
  label,
  kind: 'number',
  min: 0,
  max: 2048,
  step: 16,
  defaultValue: 0,
});

export const OPERATION_INFO: Record<EditImageOperation, OperationInfo> = {
  erase: {
    operation: 'erase',
    title: 'Erase',
    summary: 'Paint over something with the brush and remove it, filling the gap to match.',
    action: 'Erase the painted area',
    needsLayer: true,
    needsMask: true,
    prompt: 'none',
    promptLabel: '',
    promptPlaceholder: '',
    params: [],
  },
  fill: {
    operation: 'fill',
    title: 'Fill',
    summary: 'Paint over an area with the brush and describe what should appear there instead.',
    action: 'Fill the painted area',
    needsLayer: true,
    needsMask: true,
    prompt: 'required',
    promptLabel: 'What should appear',
    promptPlaceholder: 'a clear blue sky',
    params: [],
  },
  expand: {
    operation: 'expand',
    title: 'Expand',
    summary: 'Grow the picture to a new shape (a 9:16 reel cover, a square post) and let the AI continue the scene.',
    action: 'Expand the picture',
    needsLayer: true,
    needsMask: false,
    prompt: 'optional',
    promptLabel: 'What is out there (optional)',
    promptPlaceholder: 'more of the same street',
    params: [EDGE('top', 'Top (px)'), EDGE('right', 'Right (px)'), EDGE('bottom', 'Bottom (px)'), EDGE('left', 'Left (px)')],
  },
  replaceBackground: {
    operation: 'replaceBackground',
    title: 'New background',
    summary: 'Keep the subject and replace everything behind it.',
    action: 'Replace the background',
    needsLayer: true,
    needsMask: false,
    prompt: 'required',
    promptLabel: 'New background',
    promptPlaceholder: 'a neon-lit city at night',
    params: [],
  },
  relight: {
    operation: 'relight',
    title: 'Relight',
    summary: 'Change the lighting on the picture.',
    action: 'Relight the picture',
    needsLayer: true,
    needsMask: false,
    prompt: 'optional',
    promptLabel: 'Lighting (optional)',
    promptPlaceholder: 'warm light from the left, like a sunset',
    params: [],
  },
  upscale: {
    operation: 'upscale',
    title: 'Upscale',
    summary: 'Make the picture sharper and larger.',
    action: 'Upscale the picture',
    needsLayer: true,
    needsMask: false,
    prompt: 'none',
    promptLabel: '',
    promptPlaceholder: '',
    params: [{ key: 'factor', label: 'Make it', kind: 'choice', options: [2], defaultValue: 2 }],
  },
  instruct: {
    operation: 'instruct',
    title: 'Edit with words',
    summary: 'Describe a change and the AI makes it.',
    action: 'Make the change',
    needsLayer: true,
    needsMask: false,
    prompt: 'required',
    promptLabel: 'What to change',
    promptPlaceholder: 'make the shirt red',
    params: [],
  },
  generate: {
    operation: 'generate',
    title: 'Create a picture',
    summary: 'Make a new picture from a description. It is added behind your other layers.',
    action: 'Create the picture',
    needsLayer: false,
    needsMask: false,
    prompt: 'required',
    promptLabel: 'Describe it',
    promptPlaceholder: 'a dramatic mountain at sunrise, wide shot',
    params: [
      { key: 'width', label: 'Width (px)', kind: 'number', min: 256, max: 4096, step: 16, defaultValue: 1280 },
      { key: 'height', label: 'Height (px)', kind: 'number', min: 256, max: 4096, step: 16, defaultValue: 720 },
    ],
  },
  removeBackground: {
    operation: 'removeBackground',
    title: 'Remove background',
    summary: 'Cut the main subject out of the picture, with soft edges.',
    action: 'Remove the background',
    needsLayer: true,
    needsMask: false,
    prompt: 'none',
    promptLabel: '',
    promptPlaceholder: '',
    params: [],
  },
};

/** The order the AI panel lists its cards in: the everyday edits first. */
export const OPERATION_ORDER: readonly EditImageOperation[] = [
  'removeBackground',
  'replaceBackground',
  'erase',
  'fill',
  'instruct',
  'expand',
  'relight',
  'upscale',
  'generate',
];

export interface OperationInput {
  layerId: string | null;
  /** True when a non-empty brush mask is ready for the selected layer. */
  hasMask: boolean;
  prompt: string;
  params: Record<string, number>;
  /**
   * Expand only: the shape to grow the picture to (`'9:16'`), or null/absent to grow by the pixels per side
   * in `params` (top, right, bottom, left - pixels of the layer's own picture).
   */
  aspect?: string | null;
  /**
   * "Edit with words" only (see {@link takesReferencePicture}): the project file ids of the reference
   * pictures — pictures of the person or thing the change is about, in the order the AI should see them
   * ("the second picture is the badge"). Empty/null/absent: edit by description alone.
   */
  referenceProjectFileIds?: readonly string[] | null;
  /**
   * "Create a picture" and "edit with words" only (see {@link takesSubjects}): the ids of the project's avatars
   * and objects to show, at most three and at most one of them a person. Empty/null/absent: none.
   */
  subjectIds?: readonly string[] | null;
}

/** The shapes the expand tool offers, besides the canvas's own: reel/story, feed, square, landscape and photo. */
export const EXPAND_SHAPES: readonly string[] = ['9:16', '4:5', '1:1', '16:9', '3:4', '4:3', '2:3', '3:2'];

/** What a shape is for, in the words the panel shows next to it. */
export const EXPAND_SHAPE_LABELS: Readonly<Record<string, string>> = {
  '9:16': 'Reel / story',
  '4:5': 'Feed post',
  '1:1': 'Square',
  '16:9': 'Landscape video',
  '3:4': 'Portrait',
  '4:3': 'Classic',
  '2:3': 'Tall photo',
  '3:2': 'Wide photo',
};

function gcd(a: number, b: number): number {
  return b === 0 ? a : gcd(b, a % b);
}

/** A size as a reduced `W:H` shape: 720x1280 is `'9:16'`, 1000x667 stays `'1000:667'`. */
export function aspectOf(width: number, height: number): string {
  const w = Math.max(1, Math.round(width));
  const h = Math.max(1, Math.round(height));
  const d = gcd(w, h);
  let rw = w / d;
  let rh = h / d;
  // The server takes whole numbers up to 10000 on each side.
  while (rw > 10000 || rh > 10000) {
    rw = Math.max(1, Math.round(rw / 2));
    rh = Math.max(1, Math.round(rh / 2));
  }
  return `${rw}:${rh}`;
}

/** The width/height ratio of a `W:H` shape, or null for anything that is not one. */
export function shapeRatio(aspect: string | null | undefined): number | null {
  const match = /^\s*(\d+)\s*:\s*(\d+)\s*$/.exec(aspect ?? '');
  if (!match) return null;
  const w = Number(match[1]);
  const h = Number(match[2]);
  return w > 0 && h > 0 ? w / h : null;
}

/** True when a picture already has (within 1%) the shape, so expanding to it would add nothing. */
export function hasShape(size: { width: number; height: number }, aspect: string): boolean {
  const ratio = shapeRatio(aspect);
  if (ratio === null || size.width <= 0 || size.height <= 0) return false;
  return Math.abs(size.width / size.height - ratio) / ratio < 0.01;
}

/**
 * The shape the expand tool starts on: the canvas's own when the picture does not already have it (the
 * common case - "make this photo fill my 9:16 cover"), otherwise the first offered shape it does not have.
 */
export function defaultExpandAspect(
  canvas: { width: number; height: number },
  layerSize: { width: number; height: number } | null,
): string {
  const canvasShape = aspectOf(canvas.width, canvas.height);
  if (!layerSize || !hasShape(layerSize, canvasShape)) return canvasShape;
  return EXPAND_SHAPES.find((shape) => !hasShape(layerSize, shape)) ?? '1:1';
}

/** The largest margin the panel offers per side: the service's documented cap, if it has one. */
export function expandMarginMax(caps: ImageOperationCapabilities | null | undefined): number {
  if (!caps?.expand) return 2048;
  return caps.expand.maxMarginPx ?? 10000;
}

/**
 * The most reference pictures "edit with words" may be given, and the server's own cap: it refuses a
 * fifth with `400 { code, message }`. One number, here, so the panel stops offering "add" and the
 * tests assert the same limit the server enforces.
 */
export const MAX_REFERENCE_PICTURES = 4;

/**
 * True for the one tool that can be given a reference picture: "edit with words". Erase, fill, expand,
 * a new background, relight, upscale, create and the segmentation tools take none, and the server
 * refuses one sent to them — so the panel offers the control here and nowhere else.
 */
export function takesReferencePicture(op: EditImageOperation): boolean {
  return op === 'instruct';
}

/**
 * True for the two tools that can show the project's avatars and objects: "create a picture" and "edit with
 * words". The server refuses `subjectIds` sent to any other, so the picker is offered here and nowhere else.
 */
export function takesSubjects(op: EditImageOperation): boolean {
  return op === 'generate' || op === 'instruct';
}

/**
 * True for the one tool whose run moves to another AI service once avatars or objects are chosen: "create a
 * picture" with subjects is made by the picture-edit service from the subjects' own pictures, so what it costs and
 * whether it can run at all are that service's (the capabilities route asked with `subjects=true`). "Edit with
 * words" stays on its own service either way.
 */
export function subjectsChangeRoute(op: EditImageOperation): boolean {
  return op === 'generate';
}

/** True when this run goes to the picture-edit service because avatars or objects are sent with "create a picture". */
export function runsOnSubjectRoute(op: EditImageOperation, input: Pick<OperationInput, 'subjectIds'>): boolean {
  return subjectsChangeRoute(op) && (input.subjectIds ?? []).length > 0;
}

/** What the model set up for a tool says about avatars and objects. */
export interface SubjectSupport {
  /** False only when the server said the current model cannot be shown them. */
  supported: boolean;
  /** The most it takes at once, never more than {@link MAX_SUBJECTS}. */
  maxSubjects: number;
  /** When it cannot: the models that can, by name. */
  models: string[];
}

/**
 * Reads `capabilities.subjects`. A server from before avatars in pictures sends none, and so does a tool whose
 * capabilities are still loading: both read as "supported, up to three", and the server has the last word.
 */
export function subjectSupport(caps: Pick<ImageOperationCapabilities, 'subjects'> | null | undefined): SubjectSupport {
  const raw = caps?.subjects;
  if (!raw) return { supported: true, maxSubjects: MAX_SUBJECTS, models: [] };
  return {
    supported: raw.supported !== false,
    maxSubjects: subjectLimit(raw.maxSubjects),
    models: (raw.models ?? []).filter((m): m is string => typeof m === 'string' && m.trim().length > 0),
  };
}

/** The prompt rule as the service set up for the tool honours it: no prompt field for one that takes none. */
export function promptRuleFor(op: EditImageOperation, caps: ImageOperationCapabilities | null | undefined): PromptRule {
  const rule = OPERATION_INFO[op].prompt;
  return caps?.takesPrompt === false && rule !== 'required' ? 'none' : rule;
}

/**
 * What an expand to a shape will do, in one plain sentence: where the picture grows and to which size, in
 * the picture's own pixels (the service may make it smaller; the result is placed to cover the original).
 */
export function expandShapeSummary(aspect: string, layerSize: { width: number; height: number } | null): string {
  const ratio = shapeRatio(aspect);
  if (ratio === null) return 'Choose a shape.';
  if (!layerSize) return `The picture grows until it is ${aspect}.`;
  if (hasShape(layerSize, aspect)) return `The picture is already ${aspect}. Choose another shape.`;
  const { width, height } = layerSize;
  if (width / height > ratio) {
    const tall = Math.round(width / ratio);
    return `Adds picture above and below: ${width} × ${height} becomes about ${width} × ${tall}.`;
  }
  const wide = Math.round(height * ratio);
  return `Adds picture on the left and right: ${width} × ${height} becomes about ${wide} × ${height}.`;
}

/**
 * Why this operation cannot run yet, in plain words, or null when it can. The button is disabled
 * with this text beside it so the user is never left guessing.
 */
export function operationBlocker(
  op: EditImageOperation,
  input: OperationInput,
  selectedIsBitmap: boolean,
): string | null {
  const info = OPERATION_INFO[op];
  if (info.needsLayer) {
    if (!input.layerId) return 'Select a layer first.';
    if (!selectedIsBitmap) return 'Text layers have no pixels to edit. Select a picture layer.';
  }
  if (info.needsMask && !input.hasMask) return 'Paint over the area on the picture first.';
  if (info.prompt === 'required' && input.prompt.trim().length === 0) return `Describe it: ${info.promptLabel.toLowerCase()}.`;
  if (op === 'expand') {
    if (input.aspect) {
      if (shapeRatio(input.aspect) === null) return 'Choose a shape.';
      return null;
    }
    const total = ['top', 'right', 'bottom', 'left'].reduce((sum, k) => sum + (input.params[k] ?? 0), 0);
    if (total <= 0) return 'Choose how far to expand on at least one side.';
  }
  return null;
}

/**
 * The contract's `OperationRequest` for the panel's fields. Fields an operation does not use are
 * left out entirely (the server refuses unknown shapes less gracefully than missing ones), and the
 * numeric params are exactly the ones the operation declares.
 */
export function buildOperationRequest(
  op: EditImageOperation,
  input: OperationInput,
  maskKey?: string,
  caps?: ImageOperationCapabilities | null,
): ImageOperationRequest {
  const info = OPERATION_INFO[op];
  const request: ImageOperationRequest = { operation: op };
  if (info.needsLayer && input.layerId) request.layerId = input.layerId;
  if (info.needsMask && maskKey) request.maskKey = maskKey;
  if (promptRuleFor(op, caps) !== 'none' && input.prompt.trim()) request.prompt = input.prompt.trim();
  if (takesReferencePicture(op)) {
    // In the order given, and only when there is at least one: an empty list is "no reference pictures",
    // which the server reads as the key being absent, so it is left out entirely.
    const referenceIds = (input.referenceProjectFileIds ?? []).filter((id) => id.trim().length > 0);
    if (referenceIds.length > 0) request.referenceProjectFileIds = [...referenceIds];
  }
  if (takesSubjects(op)) {
    // Distinct, in the order chosen, and only when there is at least one: the key is left out otherwise.
    const subjectIds = [...new Set((input.subjectIds ?? []).filter((id) => id.trim().length > 0))];
    if (subjectIds.length > 0) request.subjectIds = subjectIds;
  }
  if (op === 'expand' && input.aspect) {
    // A shape: the server works out the margins from the picture's own size and the service's limits.
    request.params = { aspect: input.aspect };
    return request;
  }
  if (info.params.length > 0) {
    const params: Record<string, number> = {};
    for (const field of info.params) {
      let raw = input.params[field.key] ?? field.defaultValue;
      if (op === 'expand') {
        raw = Math.min(Math.max(0, Math.round(raw)), expandMarginMax(caps));
        if (raw === 0) continue; // only the sides that grow
      }
      params[field.key] = raw;
    }
    if (Object.keys(params).length > 0) request.params = params;
  }
  return request;
}

/** The defaults the panel seeds its fields with. */
export function defaultParams(op: EditImageOperation, canvas?: { width: number; height: number }): Record<string, number> {
  const out: Record<string, number> = {};
  for (const field of OPERATION_INFO[op].params) out[field.key] = field.defaultValue;
  if (op === 'generate' && canvas) {
    out.width = Math.min(4096, Math.max(256, canvas.width));
    out.height = Math.min(4096, Math.max(256, canvas.height));
  }
  return out;
}
