import { describe, expect, it } from 'vitest';
import { ariaChord, buildKeymap, displayChord, effectiveChords, KEY_ACTIONS } from '../keymap';

describe('T-007 live chords', () => {
  it('ariaChord uses Control/Meta and the word Space, with no glyphs', () => {
    expect(ariaChord('Mod+Shift+Z')).toBe('Control+Shift+Z');
    expect(ariaChord('Mod+Shift+Z', true)).toBe('Meta+Shift+Z');
    expect(ariaChord('Space')).toBe('Space');
    expect(ariaChord('Shift+ArrowLeft')).toBe('Shift+ArrowLeft');
    for (const chord of ['Mod+Z', 'Space', 'Shift+1']) {
      expect(ariaChord(chord)).not.toMatch(/[⌘␣]/);
    }
  });

  it('displayChord stays the visible copy (glyphs included)', () => {
    expect(displayChord('Mod+Z')).toBe('Ctrl+Z');
    expect(displayChord('Mod+Z', true)).toBe('⌘Z');
    expect(displayChord('Space')).toBe('␣');
  });

  it('the eight new actions keep the default chords and the same effective/build path', () => {
    const chords = effectiveChords({});
    const pages = ['pageMedia', 'pageCut', 'pageEdit', 'pageCompose', 'pageColour', 'pageAudio', 'pageDeliver'] as const;
    pages.forEach((id, i) => expect(chords[id]).toEqual([`Shift+${i + 1}`]));
    expect(chords.commandPalette).toEqual(['Mod+K']);
    const map = buildKeymap({});
    expect(map.get('Shift+3')).toBe('pageEdit');
    expect(map.get('Mod+K')).toBe('commandPalette');
  });

  it('an override moves both the cheatsheet chord and the keymap', () => {
    const chords = effectiveChords({ pageEdit: 'Shift+9' });
    expect(chords.pageEdit).toEqual(['Shift+9']);
    expect(buildKeymap({ pageEdit: 'Shift+9' }).get('Shift+9')).toBe('pageEdit');
  });

  it('the catalogue is 77 actions with a Pages group', () => {
    expect(KEY_ACTIONS).toHaveLength(77);
    expect(KEY_ACTIONS.filter((a) => a.group === 'Pages').map((a) => a.id)).toEqual([
      'pageMedia', 'pageCut', 'pageEdit', 'pageCompose', 'pageColour', 'pageAudio', 'pageDeliver',
    ]);
  });
});
