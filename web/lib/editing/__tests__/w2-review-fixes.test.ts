// @vitest-environment jsdom
/**
 * Wave 2 review fixes: the Compose list lets editor shortcuts through (and nothing else loosens), a remap
 * beats a new default chord, a compose picture tool never survives a page change (and the tool chord that
 * opens Compose still gets its tool), and entering a tool can leave focus where it is.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { buildKeymap, conflicts, effectiveChords, liveChords, shortcutBlocked } from '../keymap';
import { createEditorStore } from '../store';
import { COMPOSE_VIEWER_TOOLS, resetComposeToolOnPageChange } from '../compose/tool-lifecycle';
import { runComposeCommand, type ComposeCommandEnv } from '../compose/commands';
import { enterViewerTool } from '../viewer-transform';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

afterEach(() => {
  document.body.innerHTML = '';
});

const el = (html: string): HTMLElement => {
  const host = document.createElement('div');
  host.innerHTML = html;
  document.body.appendChild(host);
  return host.firstElementChild as HTMLElement;
};

describe('the Compose list and editor shortcuts', () => {
  const LIST = '<div role="listbox" data-editor-shortcuts="through"><button role="option" aria-selected="true">Mask, off</button></div>';
  const entry = () => el(LIST).querySelector('[role="option"]') as HTMLElement;

  it('lets every editor chord through from a focused entry, Escape and Space included', () => {
    const option = entry();
    for (const chord of ['[', ']', 'Alt+Backspace', 'Shift+M', 'Shift+F', 'Shift+P', 'Escape', 'Space', 'Mod+Z', 'Mod+Shift+Z', 'Delete', 'J', 'Shift+ArrowDown']) {
      expect(shortcutBlocked(option, chord), chord).toBe(false);
    }
  });

  it('keeps the list its own arrow keys, Home, End and Enter', () => {
    const option = entry();
    for (const chord of ['ArrowUp', 'ArrowDown', 'Home', 'End', 'Enter']) expect(shortcutBlocked(option, chord), chord).toBe(true);
  });

  it('does not weaken real menus, a Select listbox, text fields or dialogs', () => {
    for (const html of [
      '<div role="menu"><div role="menuitem" tabindex="0">Cut</div></div>',
      '<div role="listbox"><div role="option">Blur</div></div>',
      '<div role="listbox" aria-label="Select"><button role="option">Sharpen</button></div>',
      '<input />',
      '<textarea></textarea>',
      '<div role="dialog"><button>OK</button></div>',
    ]) {
      const target = el(html);
      const focus = (target.querySelector('[role="menuitem"],[role="option"],button') as HTMLElement | null) ?? target;
      for (const chord of ['[', 'Escape', 'Shift+M', 'Mod+Z']) expect(shortcutBlocked(focus, chord), `${html} ${chord}`).toBe(true);
    }
  });

  it('a text field inside the list still owns its keys, and an open modal still blocks the list', () => {
    const host = el('<div role="listbox" data-editor-shortcuts="through"><input /></div>');
    expect(shortcutBlocked(host.querySelector('input'), 'Shift+M')).toBe(true);
    const option = entry();
    el('<div role="dialog" aria-modal="true"></div>');
    expect(shortcutBlocked(option, 'Shift+M')).toBe(true);
  });
});

describe('saved keymaps', () => {
  it('a user remap beats a default chord even when the default action is listed first', () => {
    // Shift+M is toolMask's default (Compose group, listed before Pages); the user gave it to "Go to Cut".
    const overrides = { pageCut: 'Shift+M' };
    expect(buildKeymap(overrides).get('Shift+M')).toBe('pageCut');
    expect(conflicts(overrides).map((c) => c.chord)).toContain('Shift+M');
    expect(buildKeymap({}).get('Shift+M')).toBe('toolMask');
  });

  it('every chord a remap takes is taken from the default, for each new Compose chord', () => {
    for (const chord of ['Shift+M', 'Shift+F', 'Shift+P', 'Shift+S', 'Shift+I', '[', ']', 'Alt+Backspace']) {
      expect(buildKeymap({ pageMedia: chord }).get(chord), chord).toBe('pageMedia');
      expect(buildKeymap({ commandPalette: chord }).get(chord), chord).toBe('commandPalette');
    }
  });

  it('among two remaps onto one chord the action listed first wins', () => {
    expect(buildKeymap({ pageCut: 'Shift+9', pageEdit: 'Shift+9' }).get('Shift+9')).toBe('pageCut');
  });

  it('a beaten default is not advertised as live, a surviving one is', () => {
    expect(liveChords({ pageCut: 'Shift+M' }).toolMask).toEqual([]);
    expect(liveChords({ pageCut: 'Shift+M' }).pageCut).toEqual(['Shift+M']);
    expect(liveChords({}).toolMask).toEqual(['Shift+M']);
    expect(liveChords({ toolMask: 'Shift+9' }).toolMask).toEqual(['Shift+9']);
    expect(effectiveChords({ pageCut: 'Shift+M' }).toolMask).toEqual(['Shift+M']);
  });
});

describe('a compose tool never survives a page change', () => {
  const env: ComposeCommandEnv = {
    readOnly: false,
    phone: false,
    newId: () => 'id',
    labels: { maskShape: 's', maskInvert: 'i', removeMask: 'm', removeReframe: 'r', removeKeyer: 'k', removePin: 'p', addAdjustment: 'a' },
    adjustmentNames: { clip: 'A', track: 'T' },
  };
  const setup = (workspace: 'edit' | 'compose' | 'color' | 'cut', viewerTool: 'select' | 'mask' | 'reframe' | 'pin' | 'window') => {
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 });
    store.setUi({ selection: ['c1'], workspace, viewerTool });
    const stop = resetComposeToolOnPageChange(store);
    return { store, stop };
  };

  it.each(COMPOSE_VIEWER_TOOLS)('leaving Compose with the %s tool returns the viewer to select', (tool) => {
    const { store, stop } = setup('compose', tool as 'mask');
    store.setUi({ workspace: 'edit' });
    expect(store.getState().ui.viewerTool).toBe('select');
    stop();
  });

  it('never leaves a draw layer on Colour, from Edit or from Compose', () => {
    const fromEdit = setup('edit', 'mask');
    fromEdit.store.setUi({ workspace: 'color' });
    expect(fromEdit.store.getState().ui.viewerTool).toBe('select');
    const fromCompose = setup('compose', 'pin');
    fromCompose.store.setUi({ workspace: 'color' });
    expect(fromCompose.store.getState().ui.viewerTool).toBe('select');
  });

  it('the Inspector\'s Add mask on Edit keeps its tool: no page changes', () => {
    const { store } = setup('edit', 'select');
    enterViewerTool(store, 'mask');
    store.setUi({ playheadFrame: 5 });
    expect(store.getState().ui.workspace).toBe('edit');
    expect(store.getState().ui.viewerTool).toBe('mask');
  });

  it('the tool chord that opens Compose still gets its tool, even over another tool', () => {
    const { store } = setup('edit', 'mask');
    const setWorkspace = (workspace: 'compose') => store.setUi({ workspace });
    runComposeCommand('toolCornerPin', store, env, setWorkspace);
    expect(store.getState().ui.workspace).toBe('compose');
    expect(store.getState().ui.viewerTool).toBe('pin');
    // and leaving Compose afterwards resets it
    store.setUi({ workspace: 'edit' });
    expect(store.getState().ui.viewerTool).toBe('select');
  });

  it('leaves Colour\'s window tool and a same-page change alone', () => {
    const { store } = setup('color', 'window');
    store.setUi({ workspace: 'edit' });
    expect(store.getState().ui.viewerTool).toBe('window');
    const compose = setup('compose', 'mask');
    compose.store.setUi({ composeEntry: 'reframe' });
    expect(compose.store.getState().ui.viewerTool).toBe('mask');
  });
});

describe('entering a viewer tool', () => {
  const frame = () => el('<div data-testid="program-frame" tabindex="-1"></div>');
  it('moves focus to the picture by default and not when asked to keep it', () => {
    const picture = frame();
    const list = el('<button>entry</button>');
    const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([]), revision: 1 });
    list.focus();
    enterViewerTool(store, 'mask', { focusPicture: false });
    expect(store.getState().ui.viewerTool).toBe('mask');
    expect(store.getState().ui.playbackSpeed).toBe(0);
    expect(document.activeElement).toBe(list);
    enterViewerTool(store, 'reframe');
    expect(document.activeElement).toBe(picture);
    vi.restoreAllMocks();
  });
});
