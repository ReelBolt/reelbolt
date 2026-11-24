import { describe, expect, it } from 'vitest';
import { fixAttemptWording, pluralAttempts, runFailureText } from './run-failure';

describe('runFailureText', () => {
  const raw = 'Step 10 (DesignSystem) failed after 1 attempts. Last error: PM_V05 routingStrategy: value is 150 characters; the limit is 120.';

  it('replaces the raw engine text with a sentence and keeps the raw text as details', () => {
    const r = runFailureText(raw, 'CONFIG_INVALID');
    expect(r.summary).not.toMatch(/Step 10|PM_V05/);
    expect(r.details).toContain('PM_V05');
    expect(r.details).toContain('1 attempt.');
  });

  const plain400 = ('La frase della scena 3 è troppo lunga per la scena: ha 41 parole ma la scena dura 12.0 s. ').repeat(6).trim().slice(0, 400);

  it('shows a plain message up to 600 chars verbatim', () => {
    expect(plain400.length).toBe(400);
    expect(runFailureText(plain400, 'NARRATION_DOES_NOT_FIT').summary).toBe(plain400);
  });

  it('still replaces raw Step text with the code sentence and details', () => {
    const r = runFailureText('Step 3 (Voiceover) failed after 1 attempts. ' + plain400, 'CONFIG_INVALID');
    expect(r.summary).not.toMatch(/Step 3/);
    expect(r.details).toContain('Step 3 (Voiceover)');
  });

  it('treats a plain message over 600 chars as raw', () => {
    const long = 'x'.repeat(601);
    const r = runFailureText(long, 'CONFIG_INVALID');
    expect(r.summary).not.toBe(long);
    expect(r.details).toBe(long);
  });

  it('falls back to a generic sentence with no code', () => {
    expect(runFailureText(raw).summary).toBe('Something went wrong while making your video.');
  });

  it('shows an already-plain message as is', () => {
    const msg = 'The run was interrupted because the worker stopped. Run it again.';
    expect(runFailureText(msg, 'RUN_INTERRUPTED')).toEqual({ summary: msg, details: 'Code: RUN_INTERRUPTED' });
  });

  it('handles an empty message', () => {
    expect(runFailureText(null, null).details).toBeNull();
  });
});

describe('runFailureText raw variants', () => {
  it.each([
    'Step 4 (Voiceover) failed: INSUFFICIENT_CREDITS - needs 1200 millicredits, has 300',
    'Step 4 (Render) failed and was not retried because the failure is deterministic. Last error: bad config',
    'Interrupted: engine instance lost (lease owner worker-7)',
  ])('hides %s behind details', (msg) => {
    const r = runFailureText(msg, 'INSUFFICIENT_CREDITS');
    expect(r.summary).toBe('There were not enough credits to finish.');
    expect(r.details).toBe(msg);
    expect(runFailureText(msg).summary).toBe('Something went wrong while making your video.');
  });
});

describe('attempt wording', () => {
  it('pluralises', () => {
    expect(pluralAttempts(1)).toBe('1 attempt');
    expect(pluralAttempts(3)).toBe('3 attempts');
    expect(fixAttemptWording('after 1 attempts')).toBe('after 1 attempt');
  });
});
