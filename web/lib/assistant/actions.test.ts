import { describe, expect, it } from 'vitest';
import type { AssistantAction } from '../types/assistant';
import { captionStillTrue, isAbandoned, isSettled, reduceAssistantActions, settledLine } from './actions';

/** A `data-assistantAction` frame as the chat runtime stores it in `unstable_data`. */
const frame = (data: Record<string, unknown>) => ({ name: 'assistantAction', data });

const PENDING = {
  id: 'a1',
  state: 'pending',
  mode: 'ask',
  kind: 'file',
  risk: 'destructive',
  title: 'Delete the file “notes.txt”',
  summary: 'It will be removed from this project.',
  changes: [{ kind: 'remove', text: 'notes.txt' }],
  projectName: 'Launch Teaser',
  expiresAt: '2026-10-11T10:00:00Z',
};

describe('reduceAssistantActions', () => {
  it('reads one request from one frame, with every field the contract names', () => {
    const [action] = reduceAssistantActions([frame(PENDING)]);

    expect(action).toEqual({
      id: 'a1',
      state: 'pending',
      mode: 'ask',
      kind: 'file',
      risk: 'destructive',
      title: 'Delete the file “notes.txt”',
      summary: 'It will be removed from this project.',
      changes: [{ kind: 'remove', text: 'notes.txt' }],
      projectName: 'Launch Teaser',
      cost: undefined,
      outcome: undefined,
      expiresAt: '2026-10-11T10:00:00Z',
      refresh: undefined,
    });
  });

  it('lets the newest frame for an id win, keeping the order in which ids first appeared', () => {
    const actions = reduceAssistantActions([
      frame(PENDING),
      frame({ ...PENDING, id: 'a2', title: 'Second' }),
      frame({ ...PENDING, state: 'allowed' }),
      frame({ ...PENDING, state: 'done', outcome: 'Deleted notes.txt.', refresh: { projectId: 'p1' } }),
    ]);

    expect(actions.map((a) => [a.id, a.state])).toEqual([
      ['a1', 'done'],
      ['a2', 'pending'],
    ]);
    expect(actions[0].outcome).toBe('Deleted notes.txt.');
    expect(actions[0].refresh).toEqual({ projectId: 'p1' });
  });

  it('keeps what an earlier frame said when a later one leaves it out', () => {
    const [action] = reduceAssistantActions([
      frame(PENDING),
      frame({ id: 'a1', state: 'done', outcome: 'Deleted.' }),
    ]);

    expect(action.state).toBe('done');
    expect(action.title).toBe('Delete the file “notes.txt”');
    expect(action.changes).toEqual([{ kind: 'remove', text: 'notes.txt' }]);
    expect(action.projectName).toBe('Launch Teaser');
    expect(action.risk).toBe('destructive');
    expect(action.mode).toBe('ask');
  });

  it('lets a later frame change the mode, risk and changes it does carry', () => {
    const [action] = reduceAssistantActions([
      frame(PENDING),
      frame({ id: 'a1', state: 'done', mode: 'auto', risk: 'normal', changes: [] }),
    ]);

    expect(action.mode).toBe('auto');
    expect(action.risk).toBe('normal');
    expect(action.changes).toEqual([]);
  });

  it.each([
    ['done', 'pending'],
    ['done', 'allowed'],
    ['failed', 'allowed'],
    ['declined', 'pending'],
    ['expired', 'pending'],
    ['allowed', 'pending'],
  ])('never moves a request backwards: a late %s → %s frame is ignored', (settled, late) => {
    const [action] = reduceAssistantActions([
      frame({ ...PENDING, state: settled, outcome: 'First answer.' }),
      frame({ ...PENDING, state: late, outcome: 'Late answer.' }),
    ]);

    expect(action.state).toBe(settled);
    expect(action.outcome).toBe('First answer.');
  });

  it('accepts a request that begins past pending (auto mode only ever sends done or failed)', () => {
    const [action] = reduceAssistantActions([frame({ id: 'a9', state: 'done', mode: 'auto', title: 'Rename it' })]);

    expect(action).toMatchObject({ id: 'a9', state: 'done', mode: 'auto', title: 'Rename it' });
  });

  it('defaults what a frame leaves out and reads unknown words as the neutral value', () => {
    const [action] = reduceAssistantActions([
      frame({ id: 'a1', state: 'pending', kind: 'teleport', risk: 'spicy', mode: 'sometimes' }),
    ]);

    expect(action.kind).toBe('other');
    expect(action.risk).toBe('normal');
    expect(action.mode).toBe('ask');
    expect(action.changes).toEqual([]);
    expect(action.title).toBe('Make a change');
  });

  it.each([
    ['no id', { state: 'pending', title: 'x' }],
    ['a blank id', { id: '  ', state: 'pending' }],
    ['no state', { id: 'a1' }],
    ['a state from the future', { id: 'a1', state: 'paused' }],
    ['not an object', 'pending'],
    ['null', null],
  ])('ignores a frame with %s', (_label, data) => {
    expect(reduceAssistantActions([frame(data as Record<string, unknown>)])).toEqual([]);
  });

  it('ignores items that are not assistantAction frames, and anything that is not a list', () => {
    expect(
      reduceAssistantActions([
        { name: 'toolStatus', data: { caption: 'Looking' } },
        { name: 'workflowDiff', data: PENDING },
        null,
        undefined,
        'x',
      ]),
    ).toEqual([]);
    expect(reduceAssistantActions(undefined)).toEqual([]);
    expect(reduceAssistantActions(null)).toEqual([]);
    expect(reduceAssistantActions({})).toEqual([]);
  });

  it('reads change rows defensively: unknown kinds become info, empty rows are dropped', () => {
    const [action] = reduceAssistantActions([
      frame({
        ...PENDING,
        changes: [
          { kind: 'add', text: 'A new step' },
          { kind: 'rename', text: 'Something odd' },
          { kind: 'change', text: '   ' },
          { kind: 'remove' },
          'nope',
          null,
        ],
      }),
    ]);

    expect(action.changes).toEqual([
      { kind: 'add', text: 'A new step' },
      { kind: 'info', text: 'Something odd' },
    ]);
  });

  it('reads only string ids in refresh, and none at all when there are none', () => {
    const [withIds] = reduceAssistantActions([
      frame({ id: 'a1', state: 'done', refresh: { projectId: 'p', workflowId: 7, documentId: ' ', extra: 'x' } }),
    ]);
    const [without] = reduceAssistantActions([frame({ id: 'a2', state: 'done', refresh: {} })]);

    expect(withIds.refresh).toEqual({ projectId: 'p' });
    expect(without.refresh).toBeUndefined();
  });

  it('never lets an id reach the words a person reads', () => {
    const guid = '12345678-1234-1234-1234-123456789012';
    const [action] = reduceAssistantActions([
      frame({
        id: 'a1',
        state: 'done',
        title: `Rename the project “${guid}”`,
        summary: `It is ${guid}.`,
        projectName: guid,
        outcome: `Renamed (${guid}) just now.`,
        changes: [{ kind: 'change', text: `Workflow ${guid} gets a new name` }],
      }),
    ]);

    const everything = JSON.stringify([action.title, action.summary, action.projectName, action.outcome, action.changes]);
    expect(everything).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}/i);
    expect(action.title).toBe('Rename the project');
    expect(action.outcome).toBe('Renamed just now.');
    expect(action.projectName).toBeUndefined();
    // The id is allowed in exactly one place: the refresh payload, which is never shown.
  });
});

describe('isSettled / isAbandoned', () => {
  const at = (state: AssistantAction['state']): AssistantAction => ({
    ...reduceAssistantActions([frame({ ...PENDING, state })])[0],
  });

  it('settles on every state but pending and allowed', () => {
    expect(['pending', 'allowed', 'declined', 'expired', 'done', 'failed'].map((s) => isSettled(s as never))).toEqual([
      false,
      false,
      true,
      true,
      true,
      true,
    ]);
  });

  it('calls an unfinished request abandoned only once its message has stopped running', () => {
    expect(isAbandoned(at('pending'), true)).toBe(false);
    expect(isAbandoned(at('pending'), false)).toBe(true);
    expect(isAbandoned(at('allowed'), false)).toBe(true);
    expect(isAbandoned(at('done'), false)).toBe(false);
    expect(isAbandoned(at('declined'), false)).toBe(false);
  });
});

describe('settledLine', () => {
  const at = (state: AssistantAction['state'], extra: Record<string, unknown> = {}): AssistantAction =>
    reduceAssistantActions([frame({ ...PENDING, state, ...extra })])[0];

  it('says what happened in plain words for every state', () => {
    expect(settledLine(at('allowed'))).toBe('Working on it…');
    expect(settledLine(at('done', { outcome: 'Deleted notes.txt.' }))).toBe('Deleted notes.txt.');
    expect(settledLine(at('done'))).toBe('Done: Delete the file “notes.txt”');
    expect(settledLine(at('declined'))).toBe('You chose not to allow this. Nothing was changed.');
    expect(settledLine(at('expired'))).toBe('No answer was given, so nothing was changed.');
    expect(settledLine(at('failed', { outcome: 'That file is in use.' }))).toBe('That file is in use.');
    expect(settledLine(at('failed'))).toMatch(/didn't work/i);
  });
});

describe('captionStillTrue', () => {
  const at = (state: AssistantAction['state']): AssistantAction => reduceAssistantActions([frame({ ...PENDING, state })])[0];

  it('keeps "Waiting for your OK" only while a request is waiting', () => {
    expect(captionStillTrue('Waiting for your OK', [at('pending')])).toBe(true);
    expect(captionStillTrue('Waiting for your OK', [at('allowed')])).toBe(false);
    expect(captionStillTrue('Waiting for your OK', [at('declined')])).toBe(false);
    expect(captionStillTrue('Waiting for your OK', [])).toBe(false);
    expect(captionStillTrue('waiting for your ok', [at('done')])).toBe(false);
  });

  it('never touches any other caption', () => {
    expect(captionStillTrue('Looking at your files', [])).toBe(true);
    expect(captionStillTrue('Looking at your files', [at('done')])).toBe(true);
  });
});

describe('autoNote on a data-assistantAction frame', () => {
  it('is read from the frame and kept when a later frame omits it', () => {
    const [action] = reduceAssistantActions([
      frame({ ...PENDING, autoNote: { limitCredits: 25, costCredits: 166 } }),
      frame({ id: 'a1', state: 'allowed' }),
    ]);
    expect(action.autoNote).toEqual({ limitCredits: 25, costCredits: 166 });
  });
});
