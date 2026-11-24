import { describe, it, expect } from 'vitest';
import { AssistantTurnRefusal, rejectTurnRefusal, turnRefusalCodeOf, turnRefusalOf } from './assistantTurnRefusal';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

describe('turnRefusalOf', () => {
  it('reads the 429 turn cap with its sentence', async () => {
    const refusal = await turnRefusalOf(json(429, { code: 'TURN_CONCURRENCY_LIMIT', message: 'Wait a moment.' }));
    expect(refusal).toBeInstanceOf(AssistantTurnRefusal);
    expect(refusal?.code).toBe('TURN_CONCURRENCY_LIMIT');
    expect(refusal?.message).toBe('Wait a moment.');
  });

  it('ignores other statuses, codes, blank messages and non-JSON bodies', async () => {
    expect(await turnRefusalOf(json(500, { code: 'TURN_CONCURRENCY_LIMIT', message: 'x' }))).toBeNull();
    expect(await turnRefusalOf(json(429, { code: 'OTHER', message: 'x' }))).toBeNull();
    expect(await turnRefusalOf(json(429, { code: 'TURN_CONCURRENCY_LIMIT', message: ' ' }))).toBeNull();
    expect(await turnRefusalOf(new Response('slow down', { status: 429 }))).toBeNull();
  });

  it('leaves the body of a response it does not claim unread', async () => {
    const response = new Response('slow down', { status: 429 });
    await rejectTurnRefusal(response);
    expect(await response.text()).toBe('slow down');
  });

  it('throws the refusal from onResponse', async () => {
    await expect(rejectTurnRefusal(json(429, { code: 'TURN_CONCURRENCY_LIMIT', message: 'Busy.' }))).rejects.toThrow('Busy.');
  });
});

describe('turnRefusalCodeOf', () => {
  it('recognises the stored refusal and nothing else', () => {
    expect(turnRefusalCodeOf(new AssistantTurnRefusal('TURN_CONCURRENCY_LIMIT', 'Busy.'))).toBe('TURN_CONCURRENCY_LIMIT');
    expect(turnRefusalCodeOf({ code: 'TURN_CONCURRENCY_LIMIT', message: 'Busy.' })).toBe('TURN_CONCURRENCY_LIMIT');
    expect(turnRefusalCodeOf({ code: 'unknown', message: 'Status 500: boom' })).toBeNull();
    expect(turnRefusalCodeOf('boom')).toBeNull();
    expect(turnRefusalCodeOf(undefined)).toBeNull();
  });
});
