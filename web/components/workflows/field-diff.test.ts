import { describe, it, expect } from 'vitest';
import { computeStepFieldDiff, flattenLeaves } from './field-diff';
import type { StepData } from './WorkflowStepList';
import type { CreateWorkflowStepRequest } from '@/lib/types/workflow';

const step = (over: Partial<StepData> = {}): StepData => ({
  id: 's1', label: 'Review', agentDefinitionId: 'a', stepType: 'ReviewLoop', conditionExpression: null,
  loopSourceExpression: null, loopTargetStepOrder: 2, maxIterations: 3, minScore: 7, inputMappingJson: null,
  agentInputContextMode: null, cacheMode: null, selectedPriorStepOrders: [], trueBranchStepOrder: null,
  falseBranchStepOrder: null, parallelAgentIds: [], extractConfig: null, videoAnalyzeConfig: null,
  videoCompileConfig: null, videoGenerateConfig: null, voiceoverConfig: null, thumbnailGenerateConfig: null, ...over,
});

describe('computeStepFieldDiff', () => {
  it('returns nothing without a proposal', () => {
    expect(computeStepFieldDiff(step(), undefined).size).toBe(0);
  });

  it('flags changed step columns under col.*', () => {
    const proposed: CreateWorkflowStepRequest = { agentDefinitionId: 'a', stepOrder: 1, label: 'Review', minScore: 9, maxIterations: 3 };
    const diff = computeStepFieldDiff(step(), proposed);
    expect([...diff.keys()]).toEqual(['col.minScore']);
    expect(diff.get('col.minScore')).toEqual({ before: 7, after: 9 });
  });

  it('flags nested config JSON leaves under config.*', () => {
    const current = step({
      stepType: 'VideoAnalyze',
      videoAnalyzeConfig: { transcription: 'Off', language: null } as never,
    });
    const proposed: CreateWorkflowStepRequest = {
      agentDefinitionId: 'a', stepOrder: 1, label: 'x',
      videoAnalyzeConfigJson: JSON.stringify({ transcription: 'Optional', language: null, source: { kind: 'ProjectFile', projectFileId: 'f1' } }),
    };
    const diff = computeStepFieldDiff(current, proposed);
    expect(diff.get('config.transcription')).toEqual({ before: 'Off', after: 'Optional' });
    expect(diff.has('config.source.kind')).toBe(true);
    expect(diff.has('config.source.projectFileId')).toBe(true);
    expect(diff.has('config.language')).toBe(false);
  });

  it('compares raw room config strings by parsed content', () => {
    const current = step({ stepType: 'ColorGradeRoom', colorGradeRoomConfigJson: '{"version":1,"rounds":2,"conveneGate":"Off"}' });
    const proposed: CreateWorkflowStepRequest = {
      agentDefinitionId: 'a', stepOrder: 1, label: 'Review',
      colorGradeRoomConfigJson: '{ "conveneGate": "Gate", "rounds": 2, "version": 1 }',
    };
    expect([...computeStepFieldDiff(current, proposed).keys()]).toEqual(['config.conveneGate']);
  });

  it('treats arrays as a single leaf', () => {
    expect([...flattenLeaves({ a: [1, 2], b: { c: 1 } }).keys()]).toEqual(['a', 'b.c']);
  });
});
