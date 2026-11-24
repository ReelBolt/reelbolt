import { describe, expect, it, vi } from 'vitest';
import { PLACE_ACTIONS, commandUnavailable, runCommand, type CommandContext } from '../commands';
import { KEY_ACTIONS, buildKeymap, conflicts, eventToChord, keyOf } from '../keymap';
import { createEditorStore } from '../store';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';

const store = () => createEditorStore({ timelineId: 't', projectId: 'p', doc: doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0)])]), revision: 1 });

function ctxOf(place: CommandContext['place'], event?: CommandContext['event']): CommandContext {
  return {
    store: store(), endFrame: 100, undo: vi.fn(), redo: vi.fn(), addMarker: vi.fn(), liftSelection: vi.fn(), extractSelection: vi.fn(),
    nudgeSelection: vi.fn(), bladeAtPlayhead: vi.fn(), playInToOut: vi.fn(() => true), setMarks: vi.fn(),
    editing: { copy: vi.fn(), cut: vi.fn(), paste: vi.fn(), duplicate: vi.fn(), linkSelected: vi.fn(), unlinkSelected: vi.fn(), moveToTrack: vi.fn() },
    setWorkspace: vi.fn(), openCheatsheet: vi.fn(), openPalette: vi.fn(), place, event,
  };
}

describe('placement actions (T-018)', () => {
  it('the four chords are F8, Shift+F8, F9 and F10 and collide with nothing in the catalogue', () => {
    const chord = (id: string) => KEY_ACTIONS.find((a) => a.id === id)?.defaults;
    expect(chord('placeInsert')).toEqual(['F9']);
    expect(chord('placeOverwrite')).toEqual(['F10']);
    expect(chord('placeAppend')).toEqual(['F8']);
    expect(chord('placeOnTop')).toEqual(['Shift+F8']);
    expect(conflicts({})).toEqual([]);
  });

  it('function keys survive the chord normaliser', () => {
    expect(keyOf({ code: 'F9', key: 'F9' })).toBe('F9');
    expect(eventToChord({ code: 'F8', key: 'F8', ctrlKey: false, metaKey: false, shiftKey: true, altKey: false })).toBe('Shift+F8');
    const map = buildKeymap({});
    expect(map.get('F9')).toBe('placeInsert');
    expect(map.get('F10')).toBe('placeOverwrite');
    expect(map.get('F8')).toBe('placeAppend');
    expect(map.get('Shift+F8')).toBe('placeOnTop');
    expect(map.get('F11')).toBeUndefined();
    expect(map.get('F12')).toBeUndefined();
    expect(map.get('Shift+F10')).toBeUndefined();
  });

  it('a saved user chord wins over the default', () => {
    const map = buildKeymap({ placeInsert: 'Mod+Shift+9' });
    expect(map.get('Mod+Shift+9')).toBe('placeInsert');
    expect(map.get('F9')).toBeUndefined();
  });

  it('each action places its mode and prevents the key default', () => {
    for (const [id, mode] of Object.entries(PLACE_ACTIONS)) {
      const place = vi.fn();
      const preventDefault = vi.fn();
      runCommand(id as keyof typeof PLACE_ACTIONS, ctxOf(place, { preventDefault }));
      expect(place).toHaveBeenCalledWith(mode);
      expect(preventDefault).toHaveBeenCalledTimes(1);
    }
  });

  it('without a source it does nothing, and the palette says why', () => {
    const c = ctxOf(undefined);
    expect(() => runCommand('placeInsert', c)).not.toThrow();
    expect(commandUnavailable('placeInsert', { store: c.store, canUndo: false, canRedo: false, hasClipboard: false, hasSource: false })).toBe('noSource');
    expect(commandUnavailable('placeInsert', { store: c.store, canUndo: false, canRedo: false, hasClipboard: false, hasSource: true })).toBeNull();
    expect(commandUnavailable('placeOnTop', { store: c.store, canUndo: false, canRedo: false, hasClipboard: false, hasSource: true, readOnly: true })).toBe('readOnly');
    expect(commandUnavailable('placeOnTop', { store: c.store, canUndo: false, canRedo: false, hasClipboard: false, hasSource: true, phone: true })).toBe('desktopOnly');
  });
});
