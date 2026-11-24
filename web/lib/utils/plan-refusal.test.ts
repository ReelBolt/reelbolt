import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { failedRunPlanRefusal, planFeatureOfStep, planRefusalFromBody, planRefusalOf } from './plan-refusal';
import type { WorkflowExecution } from '@/lib/types/workflow';

describe('plan refusal reading', () => {
  it('reads the gate body of a 402: feature and the plan that unlocks it', () => {
    const err = new ApiError('no', 402, [], { code: 'PLAN_NOT_ENTITLED', feature: 'resolution', unlockPlan: 'creator', unlockPlanName: 'Creator' });
    expect(planRefusalOf(err)).toEqual({ feature: 'resolution', unlockPlanKey: 'creator', unlockPlanName: 'Creator' });
  });

  it('reads an assistant tool result that holds the gate under planGate', () => {
    expect(planRefusalFromBody({ error: 'x', planGate: { code: 'PLAN_NOT_ENTITLED', feature: 'rooms' } }))
      .toEqual({ feature: 'rooms', unlockPlanKey: null, unlockPlanName: null });
  });

  it('is null for anything that is not a plan refusal', () => {
    expect(planRefusalOf(new ApiError('credits', 402, [], { code: 'INSUFFICIENT_CREDITS' }))).toBeNull();
    expect(planRefusalOf(new ApiError('bad', 400, [], { code: 'PLAN_NOT_ENTITLED' }))).toBeNull();
    expect(planRefusalOf(new Error('x'))).toBeNull();
  });

  it('maps a failed step to its feature by step type, and ignores other failures', () => {
    expect(planFeatureOfStep('VideoGenerate')).toBe('video_generation');
    expect(planFeatureOfStep('AudioGenerate')).toBe('audio_generation');
    expect(planFeatureOfStep('EditRoom')).toBe('rooms');
    expect(planFeatureOfStep('Agent')).toBeNull();
    const run = (code: string, stepType: string) => ({ stepResults: [{ status: 'Failed', failureCode: code, stepType }] }) as unknown as WorkflowExecution;
    expect(failedRunPlanRefusal(run('plan_not_entitled', 'VideoGenerate'))?.feature).toBe('video_generation');
    expect(failedRunPlanRefusal(run('INSUFFICIENT_CREDITS', 'VideoGenerate'))).toBeNull();
  });
});
