import { useWorkspaceCore } from './useWorkspaceCore';
import { usePreviewDocument } from './usePreviewDocument';
import { usePreviewSources } from './usePreviewSources';
import { usePreviewParity } from './usePreviewParity';
import { useEditDispatch } from './useEditDispatch';
import { useTransport } from './useTransport';
import { useTimelineEdits } from './useTimelineEdits';
import { useKeymapChords } from './useKeymapChords';
import { usePageLayout } from './usePageLayout';
import { useShellChoice } from './useShellChoice';
import { useCommands } from './useCommands';
import { useTimelineCallbacks } from './useTimelineCallbacks';
import { useClipAndViewerActions } from './useClipAndViewerActions';
import { usePoolPlacement } from './usePoolPlacement';
import { useTimelineZoom } from './useTimelineZoom';
import { useWorkspacePanels } from './useWorkspacePanels';
import { useShellState } from './useShellState';
import type { EditorWorkspaceInput } from './types';

/**
 * Everything the editor workspace knows and can do, with no markup: the domain hooks, called in a fixed order, each
 * given what the earlier ones produced. Any shell (the full workspace, the phone surface, a simpler one) reads this.
 */
export function useEditorController(input: EditorWorkspaceInput) {
  const c1 = { ...input, ...useWorkspaceCore(input) };
  const c2 = { ...c1, ...usePreviewDocument(c1) };
  const c3 = { ...c2, ...usePreviewSources(c2) };
  const c4 = { ...c3, ...usePreviewParity(c3) };
  const c5 = { ...c4, ...useEditDispatch(c4) };
  const c6 = { ...c5, ...useTransport(c5) };
  const c7 = { ...c6, ...useTimelineEdits(c6) };
  const c8 = { ...c7, ...useKeymapChords(c7) };
  const c9 = { ...c8, ...usePageLayout(c8) };
  const c9s = { ...c9, ...useShellChoice(c9) };
  const c10 = { ...c9s, ...useCommands(c9s) };
  const c11 = { ...c10, ...useTimelineCallbacks(c10) };
  const c12 = { ...c11, ...useClipAndViewerActions(c11) };
  const c13 = { ...c12, ...usePoolPlacement(c12) };
  const c14 = { ...c13, ...useTimelineZoom(c13) };
  const c15 = { ...c14, ...useWorkspacePanels(c14) };
  const c16 = { ...c15, ...useShellState(c15) };
  return c16;
}

export type EditorController = ReturnType<typeof useEditorController>;
