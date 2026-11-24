import { describe, expect, it } from 'vitest';
import {
  COMPUTE_ROUTING_NOTE,
  COMPUTE_PREFERENCE_OPTIONS,
  computePreferenceOrDefault,
  computePreferenceLabel,
  parseComputePreference,
  runnerAvailabilityNote,
} from './compute-preference';

describe('parseComputePreference', () => {
  it('accepts the four values the engine switches over', () => {
    for (const value of ['Auto', 'CloudOnly', 'PreferRunner', 'RunnerOnly']) {
      expect(parseComputePreference(value)).toBe(value);
    }
  });

  // The plan named three of these; the shipped enum has four. Raw material that follows the plan
  // rather than the code must not become a stored value.
  it('refuses anything else, including the plan\'s own three-value spelling', () => {
    expect(parseComputePreference('Cloud')).toBeNull();
    expect(parseComputePreference('auto')).toBeNull();
    expect(parseComputePreference(null)).toBeNull();
    expect(parseComputePreference(undefined)).toBeNull();
    expect(parseComputePreference(3)).toBeNull();
  });
});

describe('computePreferenceOrDefault', () => {
  // Auto is the column default AND the engine's fail-open answer for an unreadable org row, so an
  // absent value and a stored Auto are the same fact here.
  it('reads an absent or unrecognised value as Auto', () => {
    expect(computePreferenceOrDefault(undefined)).toBe('Auto');
    expect(computePreferenceOrDefault('Wat')).toBe('Auto');
    expect(computePreferenceOrDefault('RunnerOnly')).toBe('RunnerOnly');
  });

  it('labels every value, and falls back to Auto\'s label for one it does not know', () => {
    expect(computePreferenceLabel('RunnerOnly')).toBe('Only my machines');
    expect(computePreferenceLabel(undefined)).toBe('Automatic (the default)');
  });
});

describe('the copy the settings screen depends on', () => {
  it('offers exactly the four engine values, Auto first', () => {
    expect(COMPUTE_PREFERENCE_OPTIONS.map((o) => o.value)).toEqual(['Auto', 'CloudOnly', 'PreferRunner', 'RunnerOnly']);
    expect(COMPUTE_PREFERENCE_OPTIONS.every((o) => o.label && o.description)).toBe(true);
  });

  it('states in one neutral sentence how machines and ReelBolt split the work', () => {
    expect(COMPUTE_ROUTING_NOTE).toBe("We use your paired machines for media work when they're online; everything else runs on ReelBolt.");
    expect(COMPUTE_ROUTING_NOTE).not.toMatch(/guarantee|request/i);
  });

  it('describes Automatic without a cloud-pool fallback', () => {
    const auto = COMPUTE_PREFERENCE_OPTIONS.find((o) => o.value === 'Auto');
    expect(auto?.description).toBe('Use a paired machine when one is online; otherwise ReelBolt runs it.');
  });

  it('warns on the one choice that turns an offline machine into a failed run', () => {
    const runnerOnly = COMPUTE_PREFERENCE_OPTIONS.find((o) => o.value === 'RunnerOnly');
    expect(runnerOnly?.description).toContain('the step stops with a clear message');
    expect(runnerOnly?.warning).toContain('container runtime');
    expect(COMPUTE_PREFERENCE_OPTIONS.filter((o) => o.warning).length).toBe(1);
    expect(runnerOnly?.description).not.toMatch(/quietly|cloud pool|falls back/);
  });
});

describe('runnerAvailabilityNote', () => {
  it('states what is actually known: paired, and heard from recently', () => {
    expect(runnerAvailabilityNote({ paired: 0, online: 0 })).toBe(
      'This workspace has no paired machines, so nothing can run on one yet.',
    );
    expect(runnerAvailabilityNote({ paired: 1, online: 0 })).toContain('none heard from in the last few minutes');
    expect(runnerAvailabilityNote({ paired: 3, online: 1 })).toBe(
      'This workspace has 3 paired machines, 1 heard from in the last few minutes.',
    );
    expect(runnerAvailabilityNote({ paired: 1, online: 1 })).toContain('1 paired machine,');
  });
});
