import { describe, expect, it } from 'vitest';
import {
  assembleFeedbackPayload,
  candidatesFromPath,
  defaultSelection,
  lastAssistantTurns,
  MAX_TURNS,
  presentFields,
  type FeedbackCandidates,
  type FeedbackSelection
} from './context';

const PROJECT = '11111111-1111-4111-8111-111111111111';
const EXECUTION = '22222222-2222-4222-8222-222222222222';

const candidates: FeedbackCandidates = {
  page: '/projects/abc',
  projectId: PROJECT,
  executionId: EXECUTION,
  assistantTurns: [{ role: 'user', content: 'render it' }]
};

const all: FeedbackSelection = { page: true, projectId: true, executionId: true, assistantTurns: true };
const none: FeedbackSelection = { page: false, projectId: false, executionId: false, assistantTurns: false };

describe('assembleFeedbackPayload', () => {
  it('sends every field the user attached', () => {
    const payload = assembleFeedbackPayload('bug', 'It broke', candidates, all);

    expect(payload.kind).toBe('bug');
    expect(payload.message).toBe('It broke');
    expect(payload.context).toEqual({
      page: '/projects/abc',
      projectId: PROJECT,
      executionId: EXECUTION,
      assistantTurns: [{ role: 'user', content: 'render it' }]
    });
  });

  it('collects nothing the user did not attach', () => {
    const payload = assembleFeedbackPayload('feedback', 'Just a note', candidates, none);

    expect(payload.context).toEqual({});
    expect(Object.keys(payload.context)).toHaveLength(0);
  });

  it('omits a single field the user switched off, and keeps the rest', () => {
    const payload = assembleFeedbackPayload('bug', 'x', candidates, { ...all, projectId: false });

    expect(payload.context).not.toHaveProperty('projectId');
    expect(payload.context.page).toBe('/projects/abc');
    expect(payload.context.executionId).toBe(EXECUTION);
  });

  it('cannot attach a field the page never offered', () => {
    const onlyPage: FeedbackCandidates = { page: '/dashboard' };
    const payload = assembleFeedbackPayload('bug', 'x', onlyPage, {
      ...all // even with every switch on, an absent candidate is not invented
    });

    expect(payload.context).toEqual({ page: '/dashboard' });
  });

  it('trims the message', () => {
    expect(assembleFeedbackPayload('bug', '  hi  ', candidates, none).message).toBe('hi');
  });

  it('caps the attached turns', () => {
    const many: FeedbackCandidates = {
      assistantTurns: Array.from({ length: MAX_TURNS + 5 }, (_, i) => ({
        role: 'user' as const,
        content: `m${i}`
      }))
    };

    expect(assembleFeedbackPayload('bug', 'x', many, all).context.assistantTurns).toHaveLength(MAX_TURNS);
  });
});

describe('candidatesFromPath', () => {
  it('strips the /app base path and takes the page', () => {
    expect(candidatesFromPath('/app/dashboard').page).toBe('/dashboard');
  });

  it('takes the project and execution ids a route names', () => {
    const found = candidatesFromPath(`/app/projects/${PROJECT}/workflows/w1/executions/${EXECUTION}`);
    expect(found.projectId).toBe(PROJECT);
    expect(found.executionId).toBe(EXECUTION);
  });

  it('never takes a non-id path segment as an id', () => {
    const found = candidatesFromPath('/app/editor/thumbnails/not-an-id');
    expect(found.projectId).toBeUndefined();
    expect(found.executionId).toBeUndefined();
  });
});

describe('defaultSelection / presentFields', () => {
  it('defaults to attaching everything present, and lists only what is present', () => {
    expect(presentFields(candidates)).toEqual(['page', 'projectId', 'executionId', 'assistantTurns']);
    expect(defaultSelection(candidates)).toEqual(all);
    expect(defaultSelection({ page: '/x' })).toEqual({
      page: true,
      projectId: false,
      executionId: false,
      assistantTurns: false
    });
  });
});

describe('lastAssistantTurns', () => {
  it('keeps only user/assistant turns, newest last, capped', () => {
    const turns = lastAssistantTurns(
      {
        id: 't',
        title: 't',
        projectId: null,
        messages: [
          { id: '1', role: 'system', content: 'sys', sequence: 0, createdAt: '' },
          { id: '2', role: 'user', content: 'a', sequence: 1, createdAt: '' },
          { id: '3', role: 'tool', content: 'tool', sequence: 2, createdAt: '' },
          { id: '4', role: 'assistant', content: 'b', sequence: 3, createdAt: '' }
        ]
      },
      10
    );

    expect(turns).toEqual([
      { role: 'user', content: 'a' },
      { role: 'assistant', content: 'b' }
    ]);
  });

  it('returns nothing for a missing thread', () => {
    expect(lastAssistantTurns(null)).toEqual([]);
  });
});

describe('diagnostics opt-in', () => {
  const diag = { buildId: 'x' };
  it('is absent when unticked and present when ticked', () => {
    expect(assembleFeedbackPayload('bug', 'm', {}, { ...none, diagnostics: false }, diag).context.diagnostics).toBeUndefined();
    expect(assembleFeedbackPayload('bug', 'm', {}, none, diag).context.diagnostics).toBeUndefined();
    expect(assembleFeedbackPayload('bug', 'm', {}, { ...none, diagnostics: true }, diag).context.diagnostics).toEqual(diag);
  });
});
