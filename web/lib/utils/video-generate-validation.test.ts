import { describe, it, expect } from 'vitest';
import type { VideoGenerateStepConfig } from '@/lib/types/workflow';
import {
  PLAN_SOURCE_STEP_REF_REQUIRED_ERROR,
  validateVideoGenerateConfig,
  findInvalidVideoGenerateStep,
  type VideoGenerateValidatableStep,
} from './video-generate-validation';
import { createDefaultVideoGenerateStepConfig } from '@/components/workflows/VideoGenerateStepConfig';

function valid(overrides: Partial<VideoGenerateStepConfig> = {}): VideoGenerateStepConfig {
  return {
    planSource: 'Inline',
    inlinePrompt: 'A serene landscape',
    model: 'MiniMax-H3',
    resolution: '768P',
    durationSeconds: 6,
    ratio: '16:9',
    providerId: null,
    takes: 1,
    maxClips: 1,
    maxSpendCredits: 1,
    allowSourceMediaEgress: false,
    pollIntervalSeconds: 10,
    timeoutSeconds: 1800,
    ...overrides,
  };
}

describe('validateVideoGenerateConfig', () => {
  it('returns no errors for a valid config', () => {
    expect(validateVideoGenerateConfig(valid())).toEqual([]);
  });

  describe('maxSpendCredits', () => {
    it.each([0, -1, Number.NaN, Number.POSITIVE_INFINITY])('rejects %s', (maxSpendCredits) => {
      expect(validateVideoGenerateConfig(valid({ maxSpendCredits }))).toEqual(['Max Spend (credits) must be greater than 0']);
    });
    it('accepts 0.01', () => {
      expect(validateVideoGenerateConfig(valid({ maxSpendCredits: 0.01 }))).toEqual([]);
    });
  });

  describe('inlinePrompt', () => {
    it.each([null, '', '   ', '\n\t '])('requires a non-whitespace prompt (%j)', (inlinePrompt) => {
      expect(validateVideoGenerateConfig(valid({ inlinePrompt }))).toEqual(['Inline Prompt is required']);
    });
    it('accepts exactly 7000 characters', () => {
      expect(validateVideoGenerateConfig(valid({ inlinePrompt: 'a'.repeat(7000) }))).toEqual([]);
    });
    it('rejects 7001 characters', () => {
      expect(validateVideoGenerateConfig(valid({ inlinePrompt: 'a'.repeat(7001) }))).toEqual([
        'Inline Prompt must be at most 7000 characters',
      ]);
    });
    it.each(['line1\nline2', 'a\r\nb', 'a\tb'])('allows \\n, \\r and \\t (%j)', (inlinePrompt) => {
      expect(validateVideoGenerateConfig(valid({ inlinePrompt }))).toEqual([]);
    });
    it.each(['bell\u0007', 'nul\u0000x', 'del\u007F', 'c1\u0085', 'esc\u001B'])('rejects control char (%j)', (inlinePrompt) => {
      expect(validateVideoGenerateConfig(valid({ inlinePrompt }))).toEqual([
        'Inline Prompt contains unsupported control characters',
      ]);
    });
  });

  describe('takes / maxClips', () => {
    it.each([0, 1.5, Number.NaN])('rejects takes %s', (takes) => {
      expect(validateVideoGenerateConfig(valid({ takes, maxClips: 64 }))).toEqual(['Takes must be between 1 and 64']);
    });
    it('rejects takes 65 (also exceeding a valid maxClips of 64)', () => {
      expect(validateVideoGenerateConfig(valid({ takes: 65, maxClips: 64 }))).toEqual([
        'Takes must be between 1 and 64',
        'Takes cannot exceed Max Clips',
      ]);
    });
    it.each([0, 65, 2.5])('rejects maxClips %s (and skips the takes-vs-maxClips check)', (maxClips) => {
      expect(validateVideoGenerateConfig(valid({ takes: 1, maxClips }))).toEqual(['Max Clips must be between 1 and 64']);
    });
    it('rejects takes > maxClips when maxClips is valid', () => {
      expect(validateVideoGenerateConfig(valid({ takes: 3, maxClips: 2 }))).toEqual(['Takes cannot exceed Max Clips']);
    });
    it('accepts takes == maxClips == 8 (no product cap of 4)', () => {
      expect(validateVideoGenerateConfig(valid({ takes: 8, maxClips: 8 }))).toEqual([]);
    });
    it('accepts takes == maxClips == 64', () => {
      expect(validateVideoGenerateConfig(valid({ takes: 64, maxClips: 64 }))).toEqual([]);
    });
  });

  describe('durationSeconds', () => {
    it.each([0, -3, Number.NaN])('rejects %s', (durationSeconds) => {
      expect(validateVideoGenerateConfig(valid({ durationSeconds }))).toEqual(['Duration must be greater than 0']);
    });
  });

  it('returns every message in table order', () => {
    expect(
      validateVideoGenerateConfig(
        valid({ maxSpendCredits: 0, inlinePrompt: `${'a'.repeat(7001)}\u0007`, takes: 65, maxClips: 0, durationSeconds: 0 }),
      ),
    ).toEqual([
      'Max Spend (credits) must be greater than 0',
      'Inline Prompt must be at most 7000 characters',
      'Inline Prompt contains unsupported control characters',
      'Takes must be between 1 and 64',
      'Max Clips must be between 1 and 64',
      'Duration must be greater than 0',
    ]);
    expect(validateVideoGenerateConfig(valid({ maxSpendCredits: 0, inlinePrompt: null, takes: 3, maxClips: 2 }))).toEqual([
      'Max Spend (credits) must be greater than 0',
      'Inline Prompt is required',
      'Takes cannot exceed Max Clips',
    ]);
  });
});

describe('validateVideoGenerateConfig poll/timeout bounds', () => {
  const POLL = 'Poll Interval must be between 1 and 300 seconds';
  const TIMEOUT = 'Timeout must be between 60 and 7200 seconds';
  const CROSS = 'Timeout must be at least the Poll Interval';

  it.each([1, 300])('accepts pollIntervalSeconds %s', (pollIntervalSeconds) => {
    expect(validateVideoGenerateConfig(valid({ pollIntervalSeconds }))).toEqual([]);
  });
  it.each([0, 301, -1, 1.5, Number.NaN])('rejects pollIntervalSeconds %s', (pollIntervalSeconds) => {
    expect(validateVideoGenerateConfig(valid({ pollIntervalSeconds }))).toEqual([POLL]);
  });

  it.each([60, 7200])('accepts timeoutSeconds %s', (timeoutSeconds) => {
    expect(validateVideoGenerateConfig(valid({ timeoutSeconds }))).toEqual([]);
  });
  it.each([59, 7201, 0, 120.5, Number.POSITIVE_INFINITY])('rejects timeoutSeconds %s', (timeoutSeconds) => {
    expect(validateVideoGenerateConfig(valid({ timeoutSeconds }))).toEqual([TIMEOUT]);
  });

  it('emits the cross-field message when both are valid and timeout < poll', () => {
    expect(validateVideoGenerateConfig(valid({ pollIntervalSeconds: 200, timeoutSeconds: 100 }))).toEqual([CROSS]);
  });
  it('accepts timeout equal to poll', () => {
    expect(validateVideoGenerateConfig(valid({ pollIntervalSeconds: 120, timeoutSeconds: 120 }))).toEqual([]);
  });
  it('omits the cross-field message when poll is out of range', () => {
    expect(validateVideoGenerateConfig(valid({ pollIntervalSeconds: 301, timeoutSeconds: 60 }))).toEqual([POLL]);
  });
  it('omits the cross-field message when timeout is out of range', () => {
    expect(validateVideoGenerateConfig(valid({ pollIntervalSeconds: 200, timeoutSeconds: 59 }))).toEqual([TIMEOUT]);
  });

  describe('planSource', () => {
    it('leaves the Inline path byte-identical when plan/analysis are unset', () => {
      // The default step config is Inline (no plan); its list must be exactly what it was before.
      expect(validateVideoGenerateConfig(valid({ inlinePrompt: null }))).toEqual(['Inline Prompt is required']);
      expect(validateVideoGenerateConfig(createDefaultVideoGenerateStepConfig())).toEqual([
        'Max Spend (credits) must be greater than 0',
        'Inline Prompt is required',
      ]);
    });

    it('accepts StepRef with a Previous-step plan and no inline prompt', () => {
      expect(
        validateVideoGenerateConfig(
          valid({ planSource: 'StepRef', inlinePrompt: null, plan: { from: 'Previous' } }),
        ),
      ).toEqual([]);
    });

    it('accepts StepRef with a specific-step plan carrying a step order', () => {
      expect(
        validateVideoGenerateConfig(
          valid({ planSource: 'StepRef', inlinePrompt: null, plan: { from: 'Step', stepOrder: 2 } }),
        ),
      ).toEqual([]);
    });

    it('requires a plan reference when planSource is StepRef', () => {
      expect(validateVideoGenerateConfig(valid({ planSource: 'StepRef', plan: null }))).toEqual([
        PLAN_SOURCE_STEP_REF_REQUIRED_ERROR,
      ]);
      expect(validateVideoGenerateConfig(valid({ planSource: 'StepRef' }))).toEqual([
        PLAN_SOURCE_STEP_REF_REQUIRED_ERROR,
      ]);
    });

    it('reports Max Spend before the missing plan reference', () => {
      expect(validateVideoGenerateConfig(valid({ planSource: 'StepRef', plan: null, maxSpendCredits: 0 }))).toEqual([
        'Max Spend (credits) must be greater than 0',
        PLAN_SOURCE_STEP_REF_REQUIRED_ERROR,
      ]);
    });

    it('appends the plan reference error after every pre-existing rule', () => {
      expect(
        validateVideoGenerateConfig(
          valid({
            planSource: 'StepRef',
            plan: null,
            maxSpendCredits: 0,
            takes: 65,
            maxClips: 65,
            durationSeconds: 0,
            pollIntervalSeconds: 0,
            timeoutSeconds: 0,
          }),
        ),
      ).toEqual([
        'Max Spend (credits) must be greater than 0',
        'Takes must be between 1 and 64',
        'Max Clips must be between 1 and 64',
        'Duration must be greater than 0',
        POLL,
        TIMEOUT,
        PLAN_SOURCE_STEP_REF_REQUIRED_ERROR,
      ]);
    });

    it.each(['Accumulated', 'ProjectFiles'] as const)('flags a plan sourced from %s', (from) => {
      expect(validateVideoGenerateConfig(valid({ planSource: 'StepRef', plan: { from } }))).toEqual([
        'Plan must be sourced from the previous step or a specific step',
      ]);
    });

    it('flags a specific-step plan with no step order', () => {
      expect(
        validateVideoGenerateConfig(valid({ planSource: 'StepRef', plan: { from: 'Step', stepOrder: null } })),
      ).toEqual(['Plan with a specific step requires a step order']);
    });

    it('never requires analysis, but checks its structure when set', () => {
      expect(
        validateVideoGenerateConfig(valid({ planSource: 'StepRef', plan: { from: 'Previous' }, analysis: null })),
      ).toEqual([]);
      expect(
        validateVideoGenerateConfig(
          valid({ planSource: 'StepRef', plan: { from: 'Previous' }, analysis: { from: 'Step', stepOrder: null } }),
        ),
      ).toEqual(['Analysis with a specific step requires a step order']);
    });

    it('reports the plan error before the analysis error', () => {
      expect(
        validateVideoGenerateConfig(
          valid({ planSource: 'StepRef', plan: null, analysis: { from: 'ProjectFiles' } }),
        ),
      ).toEqual([
        PLAN_SOURCE_STEP_REF_REQUIRED_ERROR,
        'Analysis must be sourced from the previous step or a specific step',
      ]);
    });
  });

  it('appends the new messages after every existing one, in table order', () => {
    expect(
      validateVideoGenerateConfig(
        valid({ maxSpendCredits: 0, inlinePrompt: '', takes: 65, maxClips: 65, durationSeconds: 0, pollIntervalSeconds: 0, timeoutSeconds: 0 }),
      ),
    ).toEqual([
      'Max Spend (credits) must be greater than 0',
      'Inline Prompt is required',
      'Takes must be between 1 and 64',
      'Max Clips must be between 1 and 64',
      'Duration must be greater than 0',
      POLL,
      TIMEOUT,
    ]);
  });

  it('keeps the default step save message unchanged', () => {
    expect(
      findInvalidVideoGenerateStep([{ stepType: 'VideoGenerate', videoGenerateConfig: createDefaultVideoGenerateStepConfig() }]),
    ).toBe('Step 1: Max Spend (credits) must be greater than 0');
  });
});

describe('findInvalidVideoGenerateStep', () => {
  const agentStep: VideoGenerateValidatableStep = { stepType: 'Agent', videoGenerateConfig: null };

  it('returns null when there are no VideoGenerate steps', () => {
    expect(findInvalidVideoGenerateStep([agentStep, agentStep])).toBeNull();
  });

  it('returns null when every VideoGenerate step is valid', () => {
    expect(findInvalidVideoGenerateStep([agentStep, { stepType: 'VideoGenerate', videoGenerateConfig: valid() }])).toBeNull();
  });

  it('names the first invalid step (1-based) with its first message', () => {
    expect(
      findInvalidVideoGenerateStep([
        agentStep,
        { stepType: 'VideoGenerate', videoGenerateConfig: valid() },
        { stepType: 'VideoGenerate', videoGenerateConfig: valid({ maxSpendCredits: 0, inlinePrompt: null }) },
        { stepType: 'VideoGenerate', videoGenerateConfig: valid({ takes: 0 }) },
      ]),
    ).toBe('Step 3: Max Spend (credits) must be greater than 0');
  });

  it('reports a default (Max Spend 0) VideoGenerate step as step 1', () => {
    expect(
      findInvalidVideoGenerateStep([{ stepType: 'VideoGenerate', videoGenerateConfig: valid({ maxSpendCredits: 0 }) }]),
    ).toBe('Step 1: Max Spend (credits) must be greater than 0');
  });

  it('treats a VideoGenerate step with a null config as invalid', () => {
    expect(findInvalidVideoGenerateStep([agentStep, { stepType: 'VideoGenerate', videoGenerateConfig: null }])).toBe(
      'Step 2: Max Spend (credits) must be greater than 0',
    );
  });

  it('blocks a plan-sourced step that has no planner step reference', () => {
    expect(
      findInvalidVideoGenerateStep([
        { stepType: 'VideoGenerate', videoGenerateConfig: valid({ planSource: 'StepRef', inlinePrompt: null }) },
      ]),
    ).toBe(`Step 1: ${PLAN_SOURCE_STEP_REF_REQUIRED_ERROR}`);
  });

  it('still reports Max Spend first for a plan-sourced step that also has no spend cap', () => {
    expect(
      findInvalidVideoGenerateStep([
        { stepType: 'VideoGenerate', videoGenerateConfig: valid({ planSource: 'StepRef', plan: null, maxSpendCredits: 0 }) },
      ]),
    ).toBe('Step 1: Max Spend (credits) must be greater than 0');
  });

  it('accepts a fully-specified plan-sourced step', () => {
    expect(
      findInvalidVideoGenerateStep([
        {
          stepType: 'VideoGenerate',
          videoGenerateConfig: valid({ planSource: 'StepRef', inlinePrompt: null, plan: { from: 'Step', stepOrder: 1 } }),
        },
      ]),
    ).toBeNull();
  });
});
