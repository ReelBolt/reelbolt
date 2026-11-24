import { describe, expect, it } from 'vitest';
import {
  ASSISTANT_APPROVAL_TOAST_MS,
  pendingApprovalsOf,
  type AssistantMessageLike,
} from './approvals';

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
};

/** An assistant reply, running unless the test says otherwise. */
const assistant = (
  unstableData: unknown,
  status: AssistantMessageLike['status'] = { type: 'running' },
): AssistantMessageLike => ({
  role: 'assistant',
  status,
  metadata: { unstable_data: unstableData },
});

describe('pendingApprovalsOf', () => {
  it('reads one request from a running reply, in the words the card uses', () => {
    expect(pendingApprovalsOf([assistant([frame(PENDING)])])).toEqual([
      { id: 'a1', title: 'Delete the file “notes.txt”', kind: 'file' },
    ]);
  });

  it('drops a request once the server settles it, whatever the outcome', () => {
    for (const state of ['allowed', 'declined', 'expired', 'done', 'failed']) {
      const messages = [assistant([frame(PENDING), frame({ id: 'a1', state })])];
      expect(pendingApprovalsOf(messages), state).toEqual([]);
    }
  });

  it('drops a pending request whose reply stopped without answering', () => {
    expect(pendingApprovalsOf([assistant([frame(PENDING)], { type: 'complete' })])).toEqual([]);
    expect(pendingApprovalsOf([assistant([frame(PENDING)], null)])).toEqual([]);
    expect(pendingApprovalsOf([assistant([frame(PENDING)], {})])).toEqual([]);
  });

  it('keeps a pending request in a reply that is still running', () => {
    expect(pendingApprovalsOf([assistant([frame(PENDING)], { type: 'running' })])).toHaveLength(1);
  });

  it('collects requests across every assistant reply, not just the newest', () => {
    const older = assistant([frame(PENDING)]);
    const newer = assistant([frame({ ...PENDING, id: 'a2', title: 'Rename the project', kind: 'project' })]);

    expect(pendingApprovalsOf([older, newer]).map((a) => a.id)).toEqual(['a1', 'a2']);
  });

  it('ignores messages that are not the assistant speaking', () => {
    const messages = [
      { role: 'user', status: { type: 'complete' }, metadata: { unstable_data: [frame(PENDING)] } },
      { role: 'system', status: { type: 'complete' }, metadata: { unstable_data: [frame(PENDING)] } },
    ];
    expect(pendingApprovalsOf(messages)).toEqual([]);
  });

  it('dedupes by id, keeping the first appearance', () => {
    const first = assistant([frame(PENDING)]);
    const repeated = assistant([frame({ ...PENDING, title: 'A later title' })]);

    const approvals = pendingApprovalsOf([first, repeated]);
    expect(approvals).toHaveLength(1);
    expect(approvals[0].title).toBe('Delete the file “notes.txt”');
  });

  it('preserves the order ids first appeared', () => {
    const messages = [
      assistant([
        frame({ ...PENDING, id: 'first', title: 'First' }),
        frame({ ...PENDING, id: 'second', title: 'Second' }),
      ]),
    ];

    expect(pendingApprovalsOf(messages).map((a) => a.id)).toEqual(['first', 'second']);
  });

  it('returns nothing for a conversation that is null, absent or unreadable', () => {
    expect(pendingApprovalsOf(null)).toEqual([]);
    expect(pendingApprovalsOf(undefined)).toEqual([]);
    expect(pendingApprovalsOf([])).toEqual([]);
    expect(pendingApprovalsOf([assistant(undefined)])).toEqual([]);
    expect(pendingApprovalsOf([assistant({})])).toEqual([]);
    expect(pendingApprovalsOf([{}])).toEqual([]);
  });

  it('ignores frames that are not permission requests', () => {
    expect(pendingApprovalsOf([assistant([{ name: 'workflowDiff', data: PENDING }])])).toEqual([]);
  });
});

describe('ASSISTANT_APPROVAL_TOAST_MS', () => {
  it('is the brief’s eight seconds, defined once here', () => {
    expect(ASSISTANT_APPROVAL_TOAST_MS).toBe(8000);
  });
});
