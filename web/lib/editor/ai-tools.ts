/**
 * The AI tools as the picker lists them: cutting a subject out (its own route, `/subjects`), designing
 * a graphic (its own long-running route, `/graphics`) plus every operation of `POST …/operations`,
 * each with a title and one plain sentence, grouped the way
 * people look for them. Which canvas tool a choice arms (the brush for erase and fill, the subject
 * clicker for a cut-out) is decided here too, so the picker and the toolbar cannot disagree.
 *
 * Pure: no React. The icons live in the picker component.
 */

import { OPERATION_INFO } from './ai-operations';
import type { EditImageOperation } from '@/lib/types/image-document';

export type AiToolId = EditImageOperation | 'subject' | 'designGraphic';

/** The tools that are not an operation of `POST …/operations` (each has its own route and panel). */
export const NON_OPERATION_TOOLS = ['subject', 'designGraphic'] as const;

/** True when the tool is one of `POST …/operations` (so it has an `OPERATION_INFO` entry and a form). */
export function isOperationTool(id: AiToolId): id is EditImageOperation {
  return !(NON_OPERATION_TOOLS as readonly string[]).includes(id);
}

export interface AiToolInfo {
  id: AiToolId;
  title: string;
  /** What it does, in one short sentence. */
  description: string;
  /** Works on the selected picture layer (everything but creating a new picture). */
  needsLayer: boolean;
  /** The canvas tool it arms: the brush for a painted area, the clicker for a subject. */
  canvasTool: 'brush' | 'subject' | null;
}

export interface AiToolGroup {
  title: string;
  tools: readonly AiToolId[];
}

export const AI_TOOL_GROUPS: readonly AiToolGroup[] = [
  { title: 'Cut out', tools: ['subject', 'removeBackground', 'replaceBackground'] },
  { title: 'Fix and change', tools: ['erase', 'fill', 'instruct'] },
  { title: 'Size and light', tools: ['expand', 'upscale', 'relight'] },
  { title: 'Make something new', tools: ['generate', 'designGraphic'] },
];

/** Every tool in picker order. */
export const AI_TOOL_ORDER: readonly AiToolId[] = AI_TOOL_GROUPS.flatMap((g) => g.tools);

export function aiToolInfo(id: AiToolId): AiToolInfo {
  if (id === 'subject') {
    return {
      id,
      title: 'Cut out a subject',
      description: 'Click a person or thing, drag a box around it, or name it, and it becomes its own layer.',
      needsLayer: false,
      canvasTool: 'subject',
    };
  }
  if (id === 'designGraphic') {
    return {
      id,
      title: 'Design a graphic',
      description:
        'Describe a title, badge, price tag, arrow or shape and an AI designer draws it as a new layer with a transparent background.',
      needsLayer: false,
      canvasTool: null,
    };
  }
  const info = OPERATION_INFO[id];
  return {
    id,
    title: info.title,
    description: info.summary,
    needsLayer: info.needsLayer,
    canvasTool: info.needsMask ? 'brush' : null,
  };
}

/** True for the tools that act on a painted area (they need the brush). */
export function usesBrush(id: AiToolId | null): boolean {
  return id !== null && aiToolInfo(id).canvasTool === 'brush';
}

/**
 * The operation whose service a tool runs on, for asking up front whether it is set up (the
 * capabilities route). A cut-out uses the same segmentation service as "Remove background", so it is
 * probed as that; "Design a graphic" is an agent run with its own availability, so it has none.
 */
export function availabilityOperation(id: AiToolId): EditImageOperation | null {
  if (id === 'subject') return 'removeBackground';
  if (id === 'designGraphic') return null;
  return id;
}
