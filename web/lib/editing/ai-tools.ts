import type { ComponentType } from 'react';
import type { WorkspaceId } from '@/lib/editing/store';
import type { ProposalAgent } from '@/lib/editing/sync/api';

/** The groups the "AI tools" list is sectioned into, in display order. */
export const EDITOR_AI_TOOL_GROUPS = ['picture', 'sound', 'edit', 'check'] as const;
export type EditorAiToolGroup = (typeof EDITOR_AI_TOOL_GROUPS)[number];

/** What a tool needs before it can run: nothing, any clip, a video clip, or an in/out range on the timeline. */
export type EditorAiToolNeeds = 'none' | 'clip' | 'videoClip' | 'range';

/** The phone sheets a tool can open (the phone editor's dock and overflow sheets). */
export type EditorAiToolSheet = 'media' | 'edit' | 'audio' | 'look' | 'captions' | 'assist' | 'review' | 'history' | 'deliver';

/**
 * What the editor workspace gives a tool to open itself. Built once in `EditorWorkspace`; a tool only calls
 * these, it never reaches into the workspace's state.
 */
export interface EditorAiToolContext {
  /** The single selected clip, or null. */
  selectedClipId: string | null;
  /** Kind of that clip: a video clip, an audio clip, or any other item (title, caption, ...); null with no selection. */
  selectedClipKind: 'video' | 'audio' | 'other' | null;
  /** True when the timeline has an in point and an out point. */
  hasRange: boolean;
  /** Opens a side-panel tab by its value (`analyse`, `transcript`, `inspector`, `assist`, ...), switching to the Edit page if needed. */
  openPanel: (value: string) => void;
  /** Switches to a whole page of the editor (`audio`, `edit`, ...). */
  openWorkspace: (workspace: WorkspaceId) => void;
  /** Opens the Remove silence dialog. */
  openRemoveSilence: () => void;
  /** Opens the Cut to the music dialog. */
  openCutToBeat: () => void;
  /** Opens the inspector and scrolls the section with this DOM id into view. */
  openInspectorSection: (id: string) => void;
  /** Opens the AI suggestions panel, with one helper preselected when given. */
  openAssist: (helper?: ProposalAgent) => void;
  /** Opens the Deliver page, where renders are made. */
  openDeliver: () => void;
  /** Phone only: opens a bottom sheet. Absent on desktop and tablet. */
  openSheet?: (name: EditorAiToolSheet) => void;
}

/** One entry of the editor's "AI tools" list. */
export interface EditorAiToolDef {
  /** Stable id, unique across tools. Registering the same id again replaces the entry. */
  id: string;
  icon: ComponentType<{ size?: number; stroke?: number; 'aria-hidden'?: boolean }>;
  group: EditorAiToolGroup;
  /** Keys under the `editor-ai-tools` catalogue namespace, e.g. `tools.magicBlur.label`. Plain words, no technical names. */
  labelKey: string;
  descriptionKey: string;
  cost: 'free' | 'credits';
  needs: EditorAiToolNeeds;
  /** Activity job types that mean "this tool is running" (shown as a chip on the row while one is active in this project). */
  activityTypes?: string[];
  open: (ctx: EditorAiToolContext) => void;
}

const tools = new Map<string, EditorAiToolDef>();

/**
 * How a future AI feature adds itself to the editor's "AI tools" list: call this once at module load with the
 * feature's definition (and add its `tools.<id>.label` / `.description` text to
 * `messages/{en,it}/editor-ai-tools.json`). It then shows in the list with its cost, its running chip and the
 * "select a clip first" hint, with no change to the launcher.
 */
export function registerEditorAiTool(def: EditorAiToolDef): void {
  tools.set(def.id, def);
}

/** Every registered tool, in registration order. */
export function listEditorAiTools(): EditorAiToolDef[] {
  return [...tools.values()];
}

/** Whether the current selection satisfies a tool's need. */
export function toolNeedMet(needs: EditorAiToolNeeds, ctx: Pick<EditorAiToolContext, 'selectedClipId' | 'selectedClipKind' | 'hasRange'>): boolean {
  if (needs === 'clip') return ctx.selectedClipId !== null;
  if (needs === 'videoClip') return ctx.selectedClipKind === 'video';
  if (needs === 'range') return ctx.hasRange;
  return true;
}

/** Test helper: drop one tool. */
export function unregisterEditorAiTool(id: string): void {
  tools.delete(id);
}
