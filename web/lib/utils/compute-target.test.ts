import { describe, expect, it } from 'vitest';
import { executionComputeChip, parseComputeTarget, stepComputeBadge } from './compute-target';

describe('parseComputeTarget', () => {
  it('accepts the three targets the engine persists', () => {
    expect(parseComputeTarget('Local')).toBe('Local');
    expect(parseComputeTarget('CloudPool')).toBe('CloudPool');
    expect(parseComputeTarget('OrgRunner')).toBe('OrgRunner');
  });

  // The value is written by the engine, not by this bundle: a fourth target must render as
  // "not shown", never as one of the three this build happens to know.
  it('refuses anything it does not know, including a plausible near-miss', () => {
    expect(parseComputeTarget('Runner')).toBeNull();
    expect(parseComputeTarget('local')).toBeNull();
    expect(parseComputeTarget('')).toBeNull();
    expect(parseComputeTarget(1)).toBeNull();
    expect(parseComputeTarget(null)).toBeNull();
    expect(parseComputeTarget(undefined)).toBeNull();
  });

  it('falls back to the persisted FromRunner tag only when it is exactly true', () => {
    expect(parseComputeTarget(undefined, true)).toBe('OrgRunner');
    expect(parseComputeTarget('CloudPool', true)).toBe('CloudPool');
    expect(parseComputeTarget(undefined, false)).toBeNull();
    expect(parseComputeTarget(undefined, 'true')).toBeNull();
    expect(parseComputeTarget('Wat', true)).toBe('OrgRunner');
  });
});

describe('stepComputeBadge', () => {
  it('names the machine a runner step ran on when the caller resolved it', () => {
    const badge = stepComputeBadge(
      { computeTarget: 'OrgRunner', fromRunner: true, runnerId: 'abcdef12-3456', runnerJobId: '99999999-0000' },
      { 'abcdef12-3456': 'Studio workstation' },
    );
    expect(badge?.label).toBe('Ran on Studio workstation');
    expect(badge?.tooltip).toContain('device abcdef12');
    expect(badge?.tooltip).toContain('job 99999999');
  });

  // A device that was revoked disappears from GET /api/v1/runners, so the name is often
  // unresolvable for an old run — the badge says what it does know rather than inventing one.
  it('falls back to "your machine" when the device is no longer in the workspace list', () => {
    const badge = stepComputeBadge({ computeTarget: 'OrgRunner', runnerId: 'gone-forever-1' }, {});
    expect(badge?.label).toBe('Ran on your machine');
    expect(badge?.tooltip).toContain('device gone-for');
  });

  it('never calls the cloud pool, or the platform itself, one of the customer\'s machines', () => {
    expect(stepComputeBadge({ computeTarget: 'CloudPool' })?.label).toBe('Ran in ReelBolt cloud');
    expect(stepComputeBadge({ computeTarget: 'Local' })?.label).toBe('Ran on this server');
    expect(stepComputeBadge({ computeTarget: 'Local' })?.tooltip).toContain('did this step itself');
  });

  it('renders nothing when the run does not say where the step ran', () => {
    expect(stepComputeBadge({})).toBeNull();
    expect(stepComputeBadge({ computeTarget: null })).toBeNull();
    expect(stepComputeBadge({ computeTarget: 'Lambda' })).toBeNull();
  });
});

describe('executionComputeChip', () => {
  it('marks a run whose step ran on a paired machine', () => {
    expect(
      executionComputeChip({ stepResults: [{ computeTarget: 'Local' }, { computeTarget: 'OrgRunner' }] })?.label,
    ).toBe('Ran on your machine');
  });

  it('leaves a run alone when nothing ran on a runner — and when the results are not loaded', () => {
    expect(executionComputeChip({ stepResults: [{ computeTarget: 'Local' }, { computeTarget: 'CloudPool' }] })).toBeNull();
    expect(executionComputeChip({ stepResults: [] })).toBeNull();
    expect(executionComputeChip({})).toBeNull();
  });

  it('trusts the persisted FromRunner tag on a payload that omits the target', () => {
    expect(executionComputeChip({ stepResults: [{ fromRunner: true }] })).not.toBeNull();
  });
});
