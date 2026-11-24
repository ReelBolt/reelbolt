// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AssistantAction } from '../types/assistant';
import {
  ASSISTANT_ACTION_DONE_EVENT,
  announceAssistantActionDone,
  onAssistantActionDone,
  resetAnnouncedAssistantActions,
} from './events';

const action = (over: Partial<AssistantAction> = {}): AssistantAction => ({
  id: 'a1',
  state: 'done',
  mode: 'ask',
  kind: 'workflow',
  risk: 'normal',
  title: 'Add a review step',
  changes: [],
  refresh: { projectId: 'p1', workflowId: 'w1' },
  ...over,
});

beforeEach(() => resetAnnouncedAssistantActions());
afterEach(() => resetAnnouncedAssistantActions());

describe('announcing a finished assistant action', () => {
  it('reaches every listener as a window event, once per action id', () => {
    const heard = vi.fn();
    const raw = vi.fn();
    const stop = onAssistantActionDone(heard);
    window.addEventListener(ASSISTANT_ACTION_DONE_EVENT, raw);

    expect(announceAssistantActionDone(action())).toBe(true);
    expect(announceAssistantActionDone(action())).toBe(false);
    expect(announceAssistantActionDone(action({ id: 'a2', kind: 'project', refresh: { projectId: 'p2' } }))).toBe(true);

    expect(heard.mock.calls.map(([d]) => d.id)).toEqual(['a1', 'a2']);
    expect(raw).toHaveBeenCalledTimes(2);
    stop();
    window.removeEventListener(ASSISTANT_ACTION_DONE_EVENT, raw);
  });

  it('tells listeners the kind, the plain title and what to reload — nothing else', () => {
    const heard = vi.fn();
    const stop = onAssistantActionDone(heard);

    announceAssistantActionDone(action({ summary: 'ignored', outcome: 'ignored too' }));

    expect(heard).toHaveBeenCalledWith({
      id: 'a1',
      kind: 'workflow',
      title: 'Add a review step',
      refresh: { projectId: 'p1', workflowId: 'w1' },
    });
    stop();
  });

  it('announces a finished action that named nothing to reload, without a refresh member', () => {
    const heard = vi.fn();
    const stop = onAssistantActionDone(heard);

    announceAssistantActionDone(action({ refresh: undefined }));

    expect(heard.mock.calls[0][0]).not.toHaveProperty('refresh');
    stop();
  });

  it.each(['pending', 'allowed', 'declined', 'expired', 'failed'] as const)(
    'announces nothing for a %s action, and still announces it later when it is done',
    (state) => {
      const heard = vi.fn();
      const stop = onAssistantActionDone(heard);

      expect(announceAssistantActionDone(action({ state }))).toBe(false);
      expect(heard).not.toHaveBeenCalled();
      expect(announceAssistantActionDone(action({ state: 'done' }))).toBe(true);
      stop();
    },
  );

  it('stops delivering once the listener unsubscribes', () => {
    const heard = vi.fn();
    const stop = onAssistantActionDone(heard);
    stop();

    announceAssistantActionDone(action());

    expect(heard).not.toHaveBeenCalled();
  });

  it('forgets nothing on its own: a remounted drawer cannot announce the same action again', () => {
    const heard = vi.fn();
    const stop = onAssistantActionDone(heard);

    announceAssistantActionDone(action());
    // The drawer closes and reopens; every message renders again with the same frames.
    announceAssistantActionDone(action());
    announceAssistantActionDone(action());

    expect(heard).toHaveBeenCalledTimes(1);
    stop();
  });
});
