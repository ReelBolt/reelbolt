import { describe, expect, it } from 'vitest';
import { needsResume, pendingCardsLeftByStop, RESUME_HIDDEN_MS } from './resume';

const action = (state: string) => ({
  name: 'assistantAction',
  data: { id: 'a1', state, mode: 'ask', kind: 'project', risk: 'normal', title: 'Delete it' },
});

const reply = (status: { type: string; reason?: string }, actionState?: string) => ({
  role: 'assistant',
  status,
  metadata: { unstable_data: actionState ? [action(actionState)] : [] },
});

describe('needsResume', () => {
  it('leaves a finished, settled conversation alone', () => {
    expect(needsResume({ messages: [reply({ type: 'complete' }, 'done')], isRunning: false, hiddenMs: 0 })).toBe(false);
    expect(needsResume({ messages: [], isRunning: false, hiddenMs: 0 })).toBe(false);
    expect(needsResume({ messages: null, isRunning: false, hiddenMs: 0 })).toBe(false);
  });

  it('resumes a reply that ended broken', () => {
    expect(needsResume({ messages: [reply({ type: 'incomplete', reason: 'error' })], isRunning: false, hiddenMs: 0 })).toBe(true);
  });

  it('does not resume a reply the person stopped', () => {
    expect(needsResume({ messages: [reply({ type: 'incomplete', reason: 'cancelled' })], isRunning: false, hiddenMs: 0 })).toBe(false);
  });

  it('resumes a reply that ended with a card still waiting or half done', () => {
    expect(needsResume({ messages: [reply({ type: 'complete' }, 'pending')], isRunning: false, hiddenMs: 0 })).toBe(true);
    expect(needsResume({ messages: [reply({ type: 'complete' }, 'allowed')], isRunning: false, hiddenMs: 0 })).toBe(true);
  });

  it('trusts a running stream, unless the page was hidden long enough to doubt it', () => {
    const running = [reply({ type: 'running' }, 'pending')];
    expect(needsResume({ messages: running, isRunning: true, hiddenMs: 0 })).toBe(false);
    expect(needsResume({ messages: running, isRunning: true, hiddenMs: RESUME_HIDDEN_MS - 1 })).toBe(false);
    expect(needsResume({ messages: running, isRunning: true, hiddenMs: RESUME_HIDDEN_MS })).toBe(true);
  });

  it('only looks at the newest assistant reply', () => {
    const messages = [reply({ type: 'complete' }, 'pending'), { role: 'user' }, reply({ type: 'complete' }, 'done')];
    expect(needsResume({ messages, isRunning: false, hiddenMs: 0 })).toBe(false);
  });

  it('never resumes a turn the person stopped, even with a card still pending', () => {
    const stopped = [reply({ type: 'incomplete', reason: 'cancelled' }, 'pending')];
    expect(needsResume({ messages: stopped, isRunning: false, hiddenMs: 0 })).toBe(false);
  });
});

describe('pendingCardsLeftByStop', () => {
  it('lists the pending cards of a stopped reply, and nothing else', () => {
    expect(pendingCardsLeftByStop([reply({ type: 'incomplete', reason: 'cancelled' }, 'pending')])).toEqual(['a1']);
    expect(pendingCardsLeftByStop([reply({ type: 'incomplete', reason: 'cancelled' }, 'done')])).toEqual([]);
    expect(pendingCardsLeftByStop([reply({ type: 'incomplete', reason: 'error' }, 'pending')])).toEqual([]);
    expect(pendingCardsLeftByStop([reply({ type: 'complete' }, 'pending')])).toEqual([]);
    expect(pendingCardsLeftByStop(null)).toEqual([]);
  });
});
