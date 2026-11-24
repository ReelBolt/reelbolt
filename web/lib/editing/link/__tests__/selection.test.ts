/** @vitest-environment node */
/** R08.1: selecting one half of a linked pair selects the group; "edit sound separately" opts out. */

import { describe, expect, it } from 'vitest';

import { EditorStore } from '../../store';
import { doc, media, track } from '../../__tests__/fixtures';
import { reconcileLinkedSelection } from '..';

const make = () =>
  new EditorStore({
    timelineId: 't',
    projectId: 'p',
    revision: 1,
    doc: doc([
      track('V1', 'video', [media('v1', 0, 100, 0, { linkId: 'L1' }), media('v2', 100, 200)]),
      track('A1', 'audio', [media('a1', 0, 100, 0, { linkId: 'L1' }), media('a2', 100, 200)]),
    ]),
  });

describe('R08.1 group selection in the store', () => {
  it('selecting a clip selects its partner; unlinked clips select alone', () => {
    const s = make();
    s.setUi({ selection: ['v1'] });
    expect(s.getState().ui.selection).toEqual(['v1', 'a1']);
    s.setUi({ selection: ['v2'] });
    expect(s.getState().ui.selection).toEqual(['v2']);
  });

  it('deselecting one half of a fully selected pair drops the whole group', () => {
    const s = make();
    s.setUi({ selection: ['v1', 'v2'] });
    expect(s.getState().ui.selection).toEqual(['v1', 'v2', 'a1']);
    expect(reconcileLinkedSelection(s.getState().doc, ['v1', 'a1'], ['a1'])).toEqual([]);
    expect(reconcileLinkedSelection(s.getState().doc, ['v1', 'a1', 'v2'], ['a1', 'v2'])).toEqual(['v2']);
  });

  it('edit sound separately selects just the clicked clip', () => {
    const s = make();
    s.setUi({ editSeparately: true });
    s.setUi({ selection: ['a1'] });
    expect(s.getState().ui.selection).toEqual(['a1']);
  });
});
