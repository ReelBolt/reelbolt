import type { EditorStore, ViewerTool } from '../store/editor-store';

/** The viewer tools a Compose entry owns. `window` (Colour) and `transform` are separate and never reset here. */
export const COMPOSE_VIEWER_TOOLS: readonly ViewerTool[] = ['mask', 'reframe', 'pin'];

/**
 * One rule, one place: a compose picture tool (mask, reframe, pin) never survives a page change. Its draw layer
 * and handles would otherwise sit on every page that shares the picture (over Colour's window handles, for one).
 *
 * The reset runs when the page changes, before the new page does anything. A tool chord that opens Compose
 * changes the page first and then enters its tool (`runComposeCommand`), so the chord's own tool is entered
 * after the reset and survives; the Inspector's "Add mask" on Edit changes no page, so it is untouched.
 * Returns the unsubscribe.
 */
export function resetComposeToolOnPageChange(store: EditorStore): () => void {
  let workspace = store.getState().ui.workspace;
  return store.subscribe(() => {
    const ui = store.getState().ui;
    if (ui.workspace === workspace) return;
    workspace = ui.workspace;
    if (COMPOSE_VIEWER_TOOLS.includes(ui.viewerTool)) store.setUi({ viewerTool: 'select' });
  });
}
