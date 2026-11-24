import { describe, expect, it } from 'vitest';
import { KEY_ACTIONS } from '../keymap';
import { TOUCH_PARITY, keyboardOnlyActions } from '../keymap-touch';

describe('R12.6 TOUCH_PARITY', () => {
  it('covers every keymap action (fails when a new action is in neither list)', () => {
    const missing = KEY_ACTIONS.filter((a) => !(a.id in TOUCH_PARITY)).map((a) => a.id);
    expect(missing).toEqual([]);
  });
  it('has no entry for an action the keymap does not have', () => {
    const known = new Set<string>(KEY_ACTIONS.map((a) => a.id));
    expect(Object.keys(TOUCH_PARITY).filter((id) => !known.has(id))).toEqual([]);
  });
  it('gives every touch entry a control name and every keyboard-only entry a sentence', () => {
    for (const [id, e] of Object.entries(TOUCH_PARITY)) {
      if (e.kind === 'touch') expect(e.control.trim().length, id).toBeGreaterThan(0);
      else expect(e.reason.endsWith('.'), id).toBe(true);
    }
  });
  it('lists the keyboard-only actions for the docs', () => {
    const ids = keyboardOnlyActions().map((a) => a.id);
    expect(ids).toEqual(expect.arrayContaining(['shuttleReverse', 'moveTrackUp']));
    expect(ids).not.toContain('deleteSelection');
  });

  it('T-007: the page actions and the palette have the exact touch parity the design names', () => {
    expect(TOUCH_PARITY.pageMedia).toEqual({ kind: 'touch', surface: 'Dock', control: 'Media' });
    expect(TOUCH_PARITY.pageEdit).toEqual({ kind: 'touch', surface: 'Dock', control: 'Edit' });
    expect(TOUCH_PARITY.pageAudio).toEqual({ kind: 'touch', surface: 'Dock', control: 'Audio' });
    expect(TOUCH_PARITY.pageDeliver).toEqual({ kind: 'touch', surface: 'Header', control: 'Export' });
    const reason = "Cut, Compose and Colour are desktop pages; the phone's dock has Media, Edit, Audio, Look and fix, Captions.";
    for (const id of ['pageCut', 'pageCompose', 'pageColour'] as const) expect(TOUCH_PARITY[id]).toEqual({ kind: 'keyboard-only', reason });
    expect(TOUCH_PARITY.commandPalette).toEqual({
      kind: 'keyboard-only',
      reason: "A command palette needs a keyboard; the phone's More sheet lists actions, it does not search commands.",
    });
    const keyboardOnly = keyboardOnlyActions().map((a) => a.id);
    for (const id of ['pageCut', 'pageCompose', 'pageColour', 'commandPalette'] as const) expect(keyboardOnly).toContain(id);
  });

  it('the Events lane actions have a touch way or a plain reason', () => {
    expect(TOUCH_PARITY.toggleEventsLane).toEqual({ kind: 'touch', surface: 'More sheet', control: 'Events' });
    expect(TOUCH_PARITY.actOnEvent).toEqual({ kind: 'touch', surface: 'More sheet', control: 'Events, then Act on this event' });
    expect(TOUCH_PARITY.toggleEventSnap.kind).toBe('keyboard-only');
  });
});
