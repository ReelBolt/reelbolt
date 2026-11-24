import type { StepChangeRequest, StepChangeStepCapability } from '@/lib/types/change-requests';

export function capability(over: Partial<StepChangeStepCapability> = {}): StepChangeStepCapability {
  return {
    stepOrder: 3, stepResultId: 'sr3', stepType: 'Voiceover', label: 'Narration', supported: true,
    notSupportedReason: null, route: null, itemKind: 'Line',
    items: [
      { id: 'vo1', status: 'Completed', durationSec: 3, text: 'Hello there.' },
      { id: 'vo2', status: 'Completed', durationSec: 4, text: 'Welcome to ReelBolt.' },
    ],
    overrides: [
      { key: 'text', type: 'Text', min: null, max: null, maxLength: 500, maxEntries: null },
      { key: 'voiceId', type: 'Text', min: null, max: null, maxLength: 100, maxEntries: null },
      { key: 'speed', type: 'Number', min: 0.5, max: 2, maxLength: null, maxEntries: null },
      { key: 'pronunciation', type: 'PronunciationList', min: null, max: null, maxLength: null, maxEntries: 8 },
    ],
    ...over,
  };
}

export function changeRequest(over: Partial<StepChangeRequest> = {}): StepChangeRequest {
  return {
    id: 'cr1', executionId: 'ex2', workflowDefinitionId: 'wf2', sourceExecutionId: 'ex1', targetStepOrder: 3,
    targetStepResultId: 'sr3', stepType: 'Voiceover', instruction: 'Slower first line', itemIds: ['vo1'],
    status: 'Completed', costCredits: 1.5, createdAt: '2026-10-25T10:00:00Z', plan: { redone: 2, reused: 5, skipped: 0 },
    ...over,
  };
}

const spec = (key: string, type: string, over: Partial<StepChangeStepCapability['overrides'][number]> = {}) => ({
  key, type, min: null, max: null, maxLength: null, maxEntries: null, values: null, ...over,
});

/** A finished VideoGenerate step: shot v1 with two takes, shot v2 with one. */
export function clipCapability(over: Partial<StepChangeStepCapability> = {}): StepChangeStepCapability {
  return {
    stepOrder: 4, stepResultId: 'sr4', stepType: 'VideoGenerate', label: 'Clips', supported: true,
    notSupportedReason: null, route: null, itemKind: 'Shot',
    items: [
      { id: 'v1', status: 'succeeded', durationSec: 5, text: 'A drone shot over the harbour' },
      { id: 'v1t1', status: 'succeeded', durationSec: 5, text: 'A drone shot over the harbour' },
      { id: 'v1t2', status: 'failed', durationSec: null, text: 'A drone shot over the harbour' },
      { id: 'v2', status: 'succeeded', durationSec: 6, text: 'Waves at sunset' },
    ],
    overrides: [spec('prompt', 'Text', { maxLength: 2000 }), spec('retake', 'Flag')],
    ...over,
  };
}

/** A finished AudioGenerate step with two takes. */
export function musicCapability(over: Partial<StepChangeStepCapability> = {}): StepChangeStepCapability {
  return {
    stepOrder: 5, stepResultId: 'sr5', stepType: 'AudioGenerate', label: 'Music', supported: true,
    notSupportedReason: null, route: null, itemKind: 'Take',
    items: [
      { id: 'a1', status: 'succeeded', durationSec: 30, text: 'Calm piano' },
      { id: 'a2', status: 'succeeded', durationSec: 30, text: 'Upbeat strings' },
    ],
    overrides: [spec('prompt', 'Text', { maxLength: 4000 }), spec('durationSec', 'Number', { min: 1, max: 120 })],
    ...over,
  };
}

/** A finished ThumbnailGenerate step: no items to pick from. */
export function thumbnailCapability(over: Partial<StepChangeStepCapability> = {}): StepChangeStepCapability {
  return {
    stepOrder: 6, stepResultId: 'sr6', stepType: 'ThumbnailGenerate', label: 'Thumbnail', supported: true,
    notSupportedReason: null, route: null, itemKind: 'None',
    items: [{ id: 'thumbnail', status: 'Completed', durationSec: null, text: 'thumb.png' }],
    overrides: [
      spec('overlayText', 'Text', { maxLength: 400 }),
      spec('atTimeSec', 'Number', { min: 0, max: 3600 }),
      spec('overlayPosition', 'Enum', { values: ['None', 'LowerThird', 'UpperThird', 'Center'] }),
      spec('overlayFontColor', 'Enum', { values: ['white', 'black', 'yellow'] }),
      spec('overlayBoxColor', 'Enum', { values: ['black@0.45', 'black@0.6', 'white@0.4', 'none'] }),
    ],
    ...over,
  };
}

/** A finished scriptwriter step: scenes, with the standing cost note. */
export function scriptCapability(over: Partial<StepChangeStepCapability> = {}): StepChangeStepCapability {
  return {
    stepOrder: 2, stepResultId: 'sr2', stepType: 'Agent', label: 'Script', supported: true,
    notSupportedReason: null, route: null, itemKind: 'Scene', costNote: 'script', allowsInstructionOnly: true,
    items: [
      { id: 'sc1', status: null, durationSec: 10, text: 'Meet the new way to make videos.' },
      { id: 'sc2', status: null, durationSec: 10, text: 'It writes, speaks and edits for you.' },
    ],
    overrides: [
      spec('voiceover', 'Text', { maxLength: 1000 }),
      spec('onScreenText', 'Text', { maxLength: 600, maxEntries: 6, multiLine: true }),
      spec('visualDescription', 'Text', { maxLength: 1000 }),
    ],
    ...over,
  };
}
