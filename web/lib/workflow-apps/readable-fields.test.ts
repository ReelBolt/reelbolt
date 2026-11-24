import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/workflows', () => ({ lintWorkflow: vi.fn() }));

import { lintWorkflow } from '../api/workflows';
import { readableBriefFields } from './readable-fields';

const mocked = vi.mocked(lintWorkflow);

const response = (findings: unknown[]) => ({
  findings,
  errors: findings.length,
  warnings: 0,
  infos: 0,
});

describe('readableBriefFields', () => {
  beforeEach(() => mocked.mockReset());

  it('identifies the unread field from the wire tokens, not the English message', async () => {
    mocked.mockResolvedValue(
      response([
        {
          ruleId: 'WL018',
          severity: 'error',
          message: 'The input "Language" is not read by any step of this workflow, so it would be shown and ignored.',
          args: { input: 'Length', inputIndex: '1' },
        },
      ]) as never,
    );

    // The message names "Language", but the finding is about the input at index 1 ("Length").
    await expect(readableBriefFields('p1', 'w1', [], ['Language', 'Length', 'Format'])).resolves.toEqual(['Language', 'Format']);
  });

  it('falls back to the label token when the payload carries no inputIndex', async () => {
    mocked.mockResolvedValue(
      response([
        { ruleId: 'WL018', severity: 'error', message: 'translated sentence', args: { input: 'Format' } },
      ]) as never,
    );

    await expect(readableBriefFields('p1', 'w1', [], ['Language', 'Format'])).resolves.toEqual(['Language']);
  });

  it('does not guess from a finding that carries no args (no English text matching)', async () => {
    mocked.mockResolvedValue(
      response([
        { ruleId: 'WL018', severity: 'error', message: 'The input "Language" is not read by any step of this workflow.' },
      ]) as never,
    );

    await expect(readableBriefFields('p1', 'w1', [], ['Language', 'Format'])).resolves.toEqual(['Language', 'Format']);
  });

  it('maps each batch index back to the candidate it probed', async () => {
    mocked.mockImplementation(async (_projectId, _workflowId, body = {}) => {
      const labels = (body.appManifest?.inputs ?? []).map((input) => input.label);
      const index = labels.indexOf('Format');
      return response([{ ruleId: 'WL018', severity: 'error', message: 'x', args: { inputIndex: String(index) } }]) as never;
    });

    const candidates = ['Language', 'Length', 'Format', 'Resolution', 'Voice', 'Style', 'Tone', 'Design'];
    await expect(readableBriefFields('p1', 'w1', [], candidates)).resolves.toEqual([
      'Language',
      'Length',
      'Resolution',
      'Voice',
      'Style',
      'Tone',
      'Design',
    ]);
  });
});
