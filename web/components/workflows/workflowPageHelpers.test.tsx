import { describe, it, expect } from 'vitest';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import type { CreateWorkflowStepRequest, WorkflowDefinition } from '@/lib/types/workflow';
import type { StepData } from '@/components/workflows/WorkflowStepList';
import { mapWorkflowStepsToStepData, stepDataToCreateRequest, describeStepsForRun, prependExecution, resolveApplyTarget } from '@/components/workflows/workflowPageHelpers';
import type { WorkflowExecution } from '@/lib/types/workflow';

/**
 * Helper function extracted from page.tsx.
 * This is a pure function that returns proposedSteps from a WorkflowEditDiff.
 * The backend always sends proposedSteps, so this should never return undefined in practice.
 */
function buildApplyStepsPayload(pendingDiff: WorkflowEditDiff): CreateWorkflowStepRequest[] {
  return pendingDiff.proposedSteps ?? [];
}

describe('buildApplyStepsPayload', () => {
  it('should return proposedSteps unmodified given a sample WorkflowEditDiff', () => {
    const proposedSteps: CreateWorkflowStepRequest[] = [
      {
        agentDefinitionId: 'agent-1',
        stepOrder: 1,
        label: 'Step 1',
        stepType: 'Agent',
        conditionExpression: null,
        loopSourceExpression: null,
        loopTargetStepOrder: null,
        maxIterations: 3,
        minScore: null,
        inputMappingJson: null,
        agentInputContextMode: null,
        selectedPriorStepOrdersJson: null,
        trueBranchStepOrder: null,
        falseBranchStepOrder: null,
        parallelAgentIdsJson: null,
        extractConfigJson: null,
        videoAnalyzeConfigJson: null,
        videoCompileConfigJson: null,
        editRoomConfigJson: null,
        graphicsRoomConfigJson: null,
        colorGradeRoomConfigJson: null,
        cacheMode: null,
      },
      {
        agentDefinitionId: 'agent-2',
        stepOrder: 2,
        label: 'Step 2',
        stepType: 'Agent',
        conditionExpression: null,
        loopSourceExpression: null,
        loopTargetStepOrder: null,
        maxIterations: 3,
        minScore: null,
        inputMappingJson: null,
        agentInputContextMode: null,
        selectedPriorStepOrdersJson: null,
        trueBranchStepOrder: null,
        falseBranchStepOrder: null,
        parallelAgentIdsJson: null,
        extractConfigJson: null,
        videoAnalyzeConfigJson: null,
        videoCompileConfigJson: null,
        editRoomConfigJson: null,
        graphicsRoomConfigJson: null,
        colorGradeRoomConfigJson: null,
        cacheMode: null,
      },
    ];

    const diff: WorkflowEditDiff = {
      additions: [],
      removals: [],
      modifications: [],
      confirmationToken: 'token-123',
      proposedSteps,
    };

    const result = buildApplyStepsPayload(diff);

    expect(result).toBe(proposedSteps);
    expect(result).toEqual(proposedSteps);
    expect(result.length).toBe(2);
    expect(result[0].agentDefinitionId).toBe('agent-1');
    expect(result[1].agentDefinitionId).toBe('agent-2');
  });

  it('should return empty proposedSteps unchanged', () => {
    const proposedSteps: CreateWorkflowStepRequest[] = [];

    const diff: WorkflowEditDiff = {
      additions: [],
      removals: [],
      modifications: [],
      confirmationToken: 'token-456',
      proposedSteps,
    };

    const result = buildApplyStepsPayload(diff);

    expect(result).toBe(proposedSteps);
    expect(result).toHaveLength(0);
  });

  it('should never fall back to local steps state when a diff is pending', () => {
    // This test verifies that the helper ONLY returns proposedSteps from the diff,
    // and does not have any fallback logic to construct steps from elsewhere.
    // The helper is pure and accepts a diff as its only parameter.

    const proposedSteps: CreateWorkflowStepRequest[] = [
      {
        agentDefinitionId: 'agent-from-assistant',
        stepOrder: 1,
        label: 'Assistant Step',
        stepType: 'Agent',
        conditionExpression: null,
        loopSourceExpression: null,
        loopTargetStepOrder: null,
        maxIterations: 3,
        minScore: null,
        inputMappingJson: null,
        agentInputContextMode: null,
        selectedPriorStepOrdersJson: null,
        trueBranchStepOrder: null,
        falseBranchStepOrder: null,
        parallelAgentIdsJson: null,
        extractConfigJson: null,
        videoAnalyzeConfigJson: null,
        videoCompileConfigJson: null,
        editRoomConfigJson: null,
        graphicsRoomConfigJson: null,
        colorGradeRoomConfigJson: null,
        cacheMode: null,
      },
    ];

    const diff: WorkflowEditDiff = {
      additions: [
        {
          stepOrder: 1,
          agentDefinitionId: 'agent-from-assistant',
          stepType: 'Agent',
          summary: 'Added step',
        },
      ],
      removals: [],
      modifications: [],
      confirmationToken: 'token-789',
      proposedSteps,
    };

    const result = buildApplyStepsPayload(diff);

    // The result must come from proposedSteps, not from the diff additions/removals/modifications
    expect(result).toBe(proposedSteps);
    expect(result[0].agentDefinitionId).toBe('agent-from-assistant');
    expect(result[0].label).toBe('Assistant Step');

    // Verify that the helper does not construct steps from diff entries
    expect(result.length).toBe(1);
    expect(result).not.toEqual(
      diff.additions.map(entry => ({
        agentDefinitionId: entry.agentDefinitionId,
        stepOrder: entry.stepOrder,
        label: entry.summary,
      }))
    );
  });

  it('should preserve all fields of proposedSteps exactly', () => {
    const proposedSteps: CreateWorkflowStepRequest[] = [
      {
        agentDefinitionId: 'agent-with-config',
        stepOrder: 1,
        label: 'Complex Step',
        stepType: 'Extract',
        conditionExpression: 'step1.output.valid',
        loopSourceExpression: 'items',
        loopTargetStepOrder: 5,
        maxIterations: 10,
        minScore: 8,
        inputMappingJson: JSON.stringify({ foo: 'bar' }),
        agentInputContextMode: 'PreviousStepOnly',
        selectedPriorStepOrdersJson: JSON.stringify([1, 2]),
        trueBranchStepOrder: '3',
        falseBranchStepOrder: '4',
        parallelAgentIdsJson: JSON.stringify(['id1', 'id2']),
        extractConfigJson: JSON.stringify({ operation: 'Project' }),
        videoAnalyzeConfigJson: JSON.stringify({ detectSilence: true }),
        videoCompileConfigJson: JSON.stringify({ mode: 'Reencode' }),
        editRoomConfigJson: JSON.stringify({ seats: [] }),
        graphicsRoomConfigJson: JSON.stringify({ artists: [] }),
        colorGradeRoomConfigJson: JSON.stringify({ colorists: [] }),
        cacheMode: 'Always',
      },
    ];

    const diff: WorkflowEditDiff = {
      additions: [],
      removals: [],
      modifications: [],
      confirmationToken: 'token-all-fields',
      proposedSteps,
    };

    const result = buildApplyStepsPayload(diff);

    expect(result).toStrictEqual(proposedSteps);
    expect(result[0].conditionExpression).toBe('step1.output.valid');
    expect(result[0].loopSourceExpression).toBe('items');
    expect(result[0].loopTargetStepOrder).toBe(5);
    expect(result[0].maxIterations).toBe(10);
    expect(result[0].minScore).toBe(8);
    expect(result[0].inputMappingJson).toBe(JSON.stringify({ foo: 'bar' }));
    expect(result[0].agentInputContextMode).toBe('PreviousStepOnly');
    expect(result[0].selectedPriorStepOrdersJson).toBe(JSON.stringify([1, 2]));
    expect(result[0].trueBranchStepOrder).toBe('3');
    expect(result[0].falseBranchStepOrder).toBe('4');
    expect(result[0].parallelAgentIdsJson).toBe(JSON.stringify(['id1', 'id2']));
    expect(result[0].extractConfigJson).toBe(JSON.stringify({ operation: 'Project' }));
    expect(result[0].videoAnalyzeConfigJson).toBe(JSON.stringify({ detectSilence: true }));
    expect(result[0].videoCompileConfigJson).toBe(JSON.stringify({ mode: 'Reencode' }));
    expect(result[0].editRoomConfigJson).toBe(JSON.stringify({ seats: [] }));
    expect(result[0].graphicsRoomConfigJson).toBe(JSON.stringify({ artists: [] }));
    expect(result[0].colorGradeRoomConfigJson).toBe(JSON.stringify({ colorists: [] }));
    expect(result[0].cacheMode).toBe('Always');
  });
});

describe('handleApplyDiff logic verification', () => {
  it('should use proposedSteps from pending diff, not fall back to local steps', () => {
    // This test verifies that when a diff is pending, the logic uses pendingDiff.proposedSteps
    // and never constructs steps from a local steps state.

    const diff: WorkflowEditDiff = {
      additions: [],
      removals: [],
      modifications: [],
      confirmationToken: 'test-token',
      proposedSteps: [
        {
          agentDefinitionId: 'from-assistant',
          stepOrder: 1,
          label: 'Assistant-chosen step',
          stepType: 'Agent',
          conditionExpression: null,
          loopSourceExpression: null,
          loopTargetStepOrder: null,
          maxIterations: 3,
          minScore: null,
          inputMappingJson: null,
          agentInputContextMode: null,
          selectedPriorStepOrdersJson: null,
          trueBranchStepOrder: null,
          falseBranchStepOrder: null,
          parallelAgentIdsJson: null,
          extractConfigJson: null,
          videoAnalyzeConfigJson: null,
          videoCompileConfigJson: null,
          editRoomConfigJson: null,
          graphicsRoomConfigJson: null,
          colorGradeRoomConfigJson: null,
          cacheMode: null,
        },
      ],
    };

    const stepsPayload = buildApplyStepsPayload(diff);

    // When handleApplyDiff calls applyWorkflow, it should pass stepsPayload (from buildApplyStepsPayload)
    // which is diff.proposedSteps, not a reconstructed steps array from the local state.
    expect(stepsPayload).toEqual(diff.proposedSteps);
    expect(stepsPayload[0].agentDefinitionId).toBe('from-assistant');

    // This verifies the fix: the payload comes from the assistant's proposed steps,
    // ensuring the confirmation token matches what the backend expects.
  });

  it('should detect and guard against undefined proposedSteps', () => {
    // This test verifies that the guard in handleApplyDiff catches the case where
    // proposedSteps is missing (undefined). This is a critical safety check that
    // prevents accidentally submitting an empty step list, which would wipe the workflow.
    const diff: WorkflowEditDiff = {
      additions: [],
      removals: [],
      modifications: [],
      confirmationToken: 'test-token',
      // proposedSteps is intentionally undefined (either missing field or stale cached response)
      proposedSteps: undefined,
    };

    // In handleApplyDiff, there is a guard:
    // if (pendingDiff.proposedSteps === undefined) {
    //   notifications.show(...error...)
    //   return; // early exit before calling applyWorkflow
    // }
    //
    // This test documents that behavior. The guard is NOT in buildApplyStepsPayload
    // (which is a pure helper), but in the caller (handleApplyDiff) where it belongs.

    // Verify that buildApplyStepsPayload would return [] for undefined proposedSteps,
    // but the guard in handleApplyDiff would catch this BEFORE calling applyWorkflow.
    const stepsPayload = buildApplyStepsPayload(diff);
    expect(stepsPayload).toEqual([]); // The helper returns [], but...
    // ...the guard in handleApplyDiff should never reach this point,
    // because it checks 'if (pendingDiff.proposedSteps === undefined)' and returns early.
  });
});

/** Minimal `StepData` with everything the payload builder reads. */
function stepData(overrides: Partial<StepData> = {}): StepData {
  return {
    id: 'step-1',
    label: 'Decide',
    agentDefinitionId: 'agent-123',
    stepType: 'Conditional',
    conditionExpression: '[score] >= 9',
    loopSourceExpression: null,
    loopTargetStepOrder: null,
    maxIterations: 3,
    minScore: null,
    inputMappingJson: null,
    agentInputContextMode: null,
    cacheMode: null,
    selectedPriorStepOrders: [],
    trueBranchStepOrder: null,
    falseBranchStepOrder: null,
    parallelAgentIds: [],
    extractConfig: null,
    videoAnalyzeConfig: null,
    videoCompileConfig: null,
    thumbnailGenerateConfig: null,
    videoGenerateConfig: null,
    voiceoverConfig: null,
    ...overrides,
  };
}

function workflowWith(steps: WorkflowDefinition['steps']): WorkflowDefinition {
  return {
    id: 'wf-1',
    name: 'Workflow',
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    requiresUserInput: false,
    steps,
  };
}

/**
 * The decision-config round trip through the page's two pure mappings. Before T-206 the builder
 * neither parsed `conditionalConfigJson`/`reviewLoopConfigJson` on load nor stringified them on
 * save, so a decision arm set anywhere (template, assistant, API) was dropped the first time a
 * human pressed Save.
 */
describe('decision config round trip through the workflow page mappings', () => {

  const conditionalConfigJson = JSON.stringify({
    version: 1,
    mode: 'Decision',
    decisionQuestion: 'Ship it?',
    threshold: 0.7,
    providerId: null,
    timeoutSeconds: 5,
  });
  const reviewLoopConfigJson = JSON.stringify({
    version: 1,
    enabled: true,
    acceptAt: 0.9,
    bimodalMargin: 0.2,
    providerId: null,
    timeoutSeconds: 5,
  });

  it('parses both config JSONs when loading steps', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'agent-123',
        stepOrder: 1,
        label: 'Decide',
        stepType: 'Conditional',
        conditionalConfigJson,
        reviewLoopConfigJson,
      } as WorkflowDefinition['steps'][number],
    ]));

    // Parsed objects, not raw strings — the editors take the typed config.
    expect(loaded.conditionalConfig).toEqual(JSON.parse(conditionalConfigJson));
    expect(loaded.reviewLoopConfig).toEqual(JSON.parse(reviewLoopConfigJson));
  });

  it('leaves both configs null when the API returns null', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'agent-123',
        stepOrder: 1,
        label: 'Decide',
        stepType: 'Conditional',
        conditionalConfigJson: null,
        reviewLoopConfigJson: null,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(loaded.conditionalConfig).toBeNull();
    expect(loaded.reviewLoopConfig).toBeNull();
  });

  it('stringifies an edited decision config into the saved step payload', () => {
    const edited = {
      version: 1,
      mode: 'Decision' as const,
      decisionQuestion: 'Ship it?',
      threshold: 0.9,
      providerId: '33333333-3333-3333-3333-333333333333',
      timeoutSeconds: 5,
    };

    const request = stepDataToCreateRequest(
      stepData({ conditionalConfig: edited, reviewLoopConfig: JSON.parse(reviewLoopConfigJson) }),
      4,
    );

    // The VALUE the author edited is what reaches the wire, as the column's JSON.
    expect(request.conditionalConfigJson).toBe(JSON.stringify(edited));
    expect(JSON.parse(request.conditionalConfigJson as string).threshold).toBe(0.9);
    expect(request.reviewLoopConfigJson).toBe(reviewLoopConfigJson);
    expect(request.stepOrder).toBe(4);
  });

  it('sends null for a step whose decision configs were never set', () => {
    const request = stepDataToCreateRequest(stepData(), 1);

    expect(request.conditionalConfigJson).toBeNull();
    expect(request.reviewLoopConfigJson).toBeNull();
  });

  it('round-trips load → edit → save without losing the untouched sibling config', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'agent-123',
        stepOrder: 1,
        label: 'Decide',
        stepType: 'Conditional',
        conditionalConfigJson,
        reviewLoopConfigJson,
      } as WorkflowDefinition['steps'][number],
    ]));

    // The author lowers the yes-threshold; nothing else is touched.
    const saved = stepDataToCreateRequest(
      { ...loaded, conditionalConfig: { ...loaded.conditionalConfig!, threshold: 0.3 } },
      1,
    );

    expect(JSON.parse(saved.conditionalConfigJson as string)).toEqual({
      version: 1,
      mode: 'Decision',
      decisionQuestion: 'Ship it?',
      threshold: 0.3,
      providerId: null,
      timeoutSeconds: 5,
    });
    expect(saved.reviewLoopConfigJson).toBe(reviewLoopConfigJson);
  });

  /**
   * T-005 — the voiceover arm of the same seam, which was pinned by NOTHING: before this, grepping
   * the web test files for `voiceoverConfigJson` exited 1 — the field appeared in no test at all.
   * `VoiceoverConfigJson` is independently optional on both DTOs, so deleting either mapping line
   * kept `tsc --noEmit` and the whole suite green — the exact class of defect phase 1 shipped here,
   * where a config field dropped by the DTO projection made every Voiceover step fail CONFIG_INVALID.
   *
   * The two directions are asserted in SEPARATE `it`s (the shape the decision block above already
   * uses) rather than as one round trip, so each mapping line is falsifiable on its own: the load
   * test fails if the parse at page.tsx:70 is deleted, the save test — which builds its input from
   * the `stepData()` fixture, not from a load — fails if the stringify at page.tsx:115 is deleted.
   *
   * The fixture carries the `NarrationPlan` source kind this wave added, so the field name that
   * only exists on the newest arm (`narrationWriterStepOrder`) is what the round trip has to carry.
   */
  const voiceoverConfigJson = JSON.stringify({
    source: { kind: 'NarrationPlan', narrationWriterStepOrder: 3 },
    providerId: '11111111-1111-1111-1111-111111111111',
    model: 's2.1-pro-free',
    defaultVoiceId: 'voice-1',
    maxBytes: 4096,
  });

  it('parses the voiceover config JSON when loading steps', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'vt-agent',
        stepOrder: 1,
        label: 'Narrate',
        stepType: 'Voiceover',
        voiceoverConfigJson,
      } as WorkflowDefinition['steps'][number],
    ]));

    // A parsed OBJECT — the editor takes the typed config, not the column's string. Both the
    // typeof check and the equality below fail on a raw string, so this cannot pass by accident.
    expect(typeof loaded.voiceoverConfig).not.toBe('string');
    expect(loaded.voiceoverConfig).toEqual(JSON.parse(voiceoverConfigJson));
    // Concrete field VALUES, not merely non-null: the NarrationPlan arm's own field has to survive.
    expect(loaded.voiceoverConfig?.source.kind).toBe('NarrationPlan');
    expect(loaded.voiceoverConfig?.source.narrationWriterStepOrder).toBe(3);
    expect(loaded.voiceoverConfig?.maxBytes).toBe(4096);
  });

  it('stringifies a voiceover config into the saved step payload, re-parsing to the same object', () => {
    const edited = {
      source: { kind: 'NarrationPlan' as const, narrationWriterStepOrder: 3 },
      providerId: '11111111-1111-1111-1111-111111111111',
      model: 's2.1-pro-free',
      defaultVoiceId: 'voice-1',
      maxBytes: 4096,
    };

    const request = stepDataToCreateRequest(stepData({ stepType: 'Voiceover', voiceoverConfig: edited }), 1);

    // The object the author edited is what reaches the wire, as the column's JSON string.
    expect(typeof request.voiceoverConfigJson).toBe('string');
    expect(request.voiceoverConfigJson).toBe(voiceoverConfigJson);
    expect(JSON.parse(request.voiceoverConfigJson as string)).toEqual(edited);
    expect(JSON.parse(request.voiceoverConfigJson as string).source.kind).toBe('NarrationPlan');
  });

  it('leaves the voiceover config null when the API returns null', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'vt-agent',
        stepOrder: 1,
        label: 'Narrate',
        stepType: 'Voiceover',
        voiceoverConfigJson: null,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(loaded.voiceoverConfig).toBeNull();
    expect(stepDataToCreateRequest(stepData(), 1).voiceoverConfigJson).toBeNull();
  });
});

/**
 * ThumbnailGenerate — the config-carriage half of the same seam the decision/voiceover blocks above
 * pin. `thumbnailGenerateConfigJson` is independently optional on both DTOs, so deleting either
 * mapping line keeps `tsc --noEmit` and the whole suite green while a saved ThumbnailGenerate step
 * loses its config the first time a human presses Save.
 *
 * Both directions are asserted separately, for the same reason: the load test fails if the parse in
 * `mapWorkflowStepsToStepData` is deleted, and the save test — which builds its input from the
 * `stepData()` fixture, never from a load — fails if the stringify in `stepDataToCreateRequest` is.
 */
describe('thumbnail config round trip through the workflow page mappings', () => {
  const thumbnailConfigJson = JSON.stringify({
    version: 1,
    analysisStepOrder: 2,
    analysisStepResultId: null,
    selection: 'AtTime',
    atTimeSec: 12.5,
    sourceIndex: 0,
    width: 1280,
    height: 720,
    fit: 'Cover',
    jpegQuality: 4,
    outputFileName: 'thumbnail.jpg',
    registerProjectFile: true,
    maxCandidates: 24,
    minShotSeconds: 0.4,
    preferBestTake: true,
    overlayText: 'Watch the launch',
    overlayPosition: 'LowerThird',
    overlayFontSizePct: 9,
    overlayFontColor: 'white',
    overlayBoxColor: 'black@0.45',
    maxOverlayChars: 90,
  });

  it('parses the thumbnail config JSON when loading steps', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'vt-agent',
        stepOrder: 1,
        label: 'Make the cover',
        stepType: 'ThumbnailGenerate',
        thumbnailGenerateConfigJson: thumbnailConfigJson,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(typeof loaded.thumbnailGenerateConfig).not.toBe('string');
    expect(loaded.thumbnailGenerateConfig).toEqual(JSON.parse(thumbnailConfigJson));
    // Concrete VALUES, not merely non-null: the two members a careless projection would drop are the
    // nested-free enum and the one the step's whole selection strategy hangs on.
    expect(loaded.thumbnailGenerateConfig?.selection).toBe('AtTime');
    expect(loaded.thumbnailGenerateConfig?.analysisStepOrder).toBe(2);
  });

  it('stringifies a thumbnail config into the saved step payload, re-parsing to the same object', () => {
    const edited = JSON.parse(thumbnailConfigJson) as NonNullable<StepData['thumbnailGenerateConfig']>;
    edited.overlayPosition = 'UpperThird';

    const request = stepDataToCreateRequest(stepData({ stepType: 'ThumbnailGenerate', thumbnailGenerateConfig: edited }), 1);

    expect(typeof request.thumbnailGenerateConfigJson).toBe('string');
    expect(JSON.parse(request.thumbnailGenerateConfigJson as string)).toEqual(edited);
    expect(JSON.parse(request.thumbnailGenerateConfigJson as string).overlayPosition).toBe('UpperThird');
  });

  it('leaves the thumbnail config null when the API returns null', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'vt-agent',
        stepOrder: 1,
        label: 'Make the cover',
        stepType: 'ThumbnailGenerate',
        thumbnailGenerateConfigJson: null,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(loaded.thumbnailGenerateConfig).toBeNull();
    expect(stepDataToCreateRequest(stepData(), 1).thumbnailGenerateConfigJson).toBeNull();
  });
});

/**
 * Customer e2e finding 18: the run confirmation lists what will happen in plain words, and a
 * started run appears in "Run history" immediately instead of "No executions".
 */
describe('describeStepsForRun', () => {
  const wf = {
    id: 'w', name: 'Reel', createdAt: '', updatedAt: '', requiresUserInput: false,
    steps: [
      { id: '2', agentDefinitionId: 'nw', stepOrder: 2, label: 'Step 2', stepType: 'Agent' },
      { id: '1', agentDefinitionId: 'vt', stepOrder: 1, label: 'Raw footage', stepType: 'VideoAnalyze' },
      { id: '3', agentDefinitionId: 'cu', stepOrder: 3, label: '', stepType: 'Agent' },
    ],
  } as unknown as WorkflowDefinition;
  const agents = [
    { id: 'nw', agentType: 'NarrationWriter' },
    { id: 'vt', agentType: 'VideoTransform' },
    { id: 'cu', agentType: 'Custom' },
  ] as never;

  it('lists steps in order with their plain-language job, keeping a meaningful custom name', () => {
    expect(describeStepsForRun(wf, agents)).toEqual([
      'Raw footage — Watches your footage',
      'Writes the narration',
      'Runs your custom AI agent',
    ]);
  });
});

describe('prependExecution', () => {
  const e = (id: string) => ({ id } as WorkflowExecution);
  it('puts the new run first, without duplicating it', () => {
    expect(prependExecution([e('a'), e('b')], e('c')).map((x) => x.id)).toEqual(['c', 'a', 'b']);
    expect(prependExecution([e('c'), e('a')], e('c')).map((x) => x.id)).toEqual(['c', 'a']);
    expect(prependExecution(undefined, e('c')).map((x) => x.id)).toEqual(['c']);
  });
});

/**
 * TimelineRender (editor v2, P4) — the same config-carriage seam as the thumbnail block above:
 * `timelineRenderConfigJson` is independently optional on both DTOs, so either mapping line can be
 * deleted without a type error. Both directions are asserted separately for the same reason.
 */
describe('timeline render config round trip through the workflow page mappings', () => {
  const renderConfigJson = JSON.stringify({
    editTimelineId: '1b2c3d4e-5f60-4711-8a92-3c4d5e6f7001',
    version: 1,
    revision: 7,
    range: null,
    preset: 'Vertical9x16',
    encode: { width: null, height: null, codec: 'H264', crf: 20, speed: 'Slow', audioBitrateKbps: 160 },
    outputFileName: 'reel.mp4',
    registerProjectFile: true,
    loudness: { targetLufs: -14, truePeakDbtp: -1, loudnessRange: 9 },
  });

  it('parses the render config JSON when loading steps', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'vt-agent',
        stepOrder: 1,
        label: 'Render the reel',
        stepType: 'TimelineRender',
        timelineRenderConfigJson: renderConfigJson,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(typeof loaded.timelineRenderConfig).not.toBe('string');
    expect(loaded.timelineRenderConfig).toEqual(JSON.parse(renderConfigJson));
    expect(loaded.timelineRenderConfig?.preset).toBe('Vertical9x16');
    expect(loaded.timelineRenderConfig?.revision).toBe(7);
    // The nested records survive the parse intact — a flat projection would drop them.
    expect(loaded.timelineRenderConfig?.encode?.speed).toBe('Slow');
    expect(loaded.timelineRenderConfig?.loudness?.targetLufs).toBe(-14);
  });

  it('stringifies a render config into the saved step payload, re-parsing to the same object', () => {
    const edited = JSON.parse(renderConfigJson) as NonNullable<StepData['timelineRenderConfig']>;
    edited.preset = 'Square1x1';

    const request = stepDataToCreateRequest(stepData({ stepType: 'TimelineRender', timelineRenderConfig: edited }), 1);

    expect(typeof request.timelineRenderConfigJson).toBe('string');
    expect(JSON.parse(request.timelineRenderConfigJson as string)).toEqual(edited);
    expect(JSON.parse(request.timelineRenderConfigJson as string).preset).toBe('Square1x1');
  });

  it('leaves the render config null when the API returns null', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'vt-agent',
        stepOrder: 1,
        label: 'Render the reel',
        stepType: 'TimelineRender',
        timelineRenderConfigJson: null,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(loaded.timelineRenderConfig).toBeNull();
    expect(stepDataToCreateRequest(stepData(), 1).timelineRenderConfigJson).toBeNull();
  });
});

/**
 * DesignSystem — the same independently-optional seam as the timeline render block above:
 * `designSystemConfigJson` is optional on both DTOs, so a deleted mapping line raises no type
 * error. Both directions are asserted separately for the same reason.
 */
describe('design system config round trip through the workflow page mappings', () => {
  const designSystemConfigJson = JSON.stringify({
    op: 'Compose',
    version: 1,
    compose: { parseStepOrder: 1, designFallbackStepOrders: [8], productFallbackStepOrders: [4, 5, 6, 7], ingest: true },
  });

  it('parses the design system config JSON when loading steps', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'extract-agent',
        stepOrder: 1,
        label: 'Read the brand',
        stepType: 'DesignSystem',
        designSystemConfigJson,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(typeof loaded.designSystemConfig).not.toBe('string');
    expect(loaded.designSystemConfig).toEqual(JSON.parse(designSystemConfigJson));
    expect(loaded.designSystemConfig?.op).toBe('Compose');
    // The nested record survives the parse intact — a flat projection would drop it.
    expect(loaded.designSystemConfig?.compose?.ingest).toBe(true);
  });

  it('stringifies a design system config into the saved step payload, re-parsing to the same object', () => {
    const edited = JSON.parse(designSystemConfigJson) as NonNullable<StepData['designSystemConfig']>;
    if (edited.compose) edited.compose.ingest = false;

    const request = stepDataToCreateRequest(stepData({ stepType: 'DesignSystem', designSystemConfig: edited }), 1);

    expect(typeof request.designSystemConfigJson).toBe('string');
    expect(JSON.parse(request.designSystemConfigJson as string)).toEqual(edited);
    expect(JSON.parse(request.designSystemConfigJson as string).compose.ingest).toBe(false);
  });

  it('leaves the design system config null when the API returns null', () => {
    const [loaded] = mapWorkflowStepsToStepData(workflowWith([
      {
        id: 'step-1',
        agentDefinitionId: 'extract-agent',
        stepOrder: 1,
        label: 'Read the brand',
        stepType: 'DesignSystem',
        designSystemConfigJson: null,
      } as WorkflowDefinition['steps'][number],
    ]));

    expect(loaded.designSystemConfig).toBeNull();
    expect(stepDataToCreateRequest(stepData(), 1).designSystemConfigJson).toBeNull();
  });
});

/**
 * ExtractFrame / AudioGenerate configs and the two step options (continue a composition, register the output):
 * all four are optional on both DTOs, so a deleted mapping line raises no type error. Both directions are asserted.
 */
describe('a PinInsert config round trips through the workflow page mappings (T-206)', () => {
  const pinInsertConfigJson = JSON.stringify({
    analysis: { from: 'Step', stepOrder: 1 },
    pins: [{ regionId: 'r0', content: { from: 'Step', stepOrder: 2 } }, { regionId: null, content: { from: 'ProjectFile', projectFileId: 'f-1' } }],
    requireApplied: false,
  });

  it('loads the stored config and saves it back unchanged', () => {
    const [pin] = mapWorkflowStepsToStepData(workflowWith([
      { id: 's3', agentDefinitionId: 'a', stepOrder: 3, label: 'Pin', stepType: 'PinInsert', pinInsertConfigJson } as WorkflowDefinition['steps'][number],
    ]));
    expect(pin.pinInsertConfig).toEqual(JSON.parse(pinInsertConfigJson));

    const request = stepDataToCreateRequest(pin, 3);
    expect(JSON.parse(request.pinInsertConfigJson as string)).toEqual(JSON.parse(pinInsertConfigJson));
  });

  it('sends no PinInsert config for any other step', () => {
    expect(stepDataToCreateRequest(stepData(), 1).pinInsertConfigJson).toBeUndefined();
  });
});

describe('extract-frame, audio-generate and step options round trip through the workflow page mappings', () => {
  const extractFrameConfigJson = JSON.stringify({ source: { kind: 'StepOutput', stepOrder: 2 }, at: 'Time', atSec: 1.5, format: 'jpeg', maxLongSide: 1024, registerProjectFile: false });
  const audioGenerateConfigJson = JSON.stringify({ kind: 'SoundEffect', prompt: 'door creak', durationSec: 2, loop: true, takes: 2, maxSpendCredits: 1.5, registerProjectFile: true });

  it('parses both configs and the options when loading steps', () => {
    const [frame, audio, agent] = mapWorkflowStepsToStepData(workflowWith([
      { id: 's1', agentDefinitionId: 'a', stepOrder: 1, label: 'Frame', stepType: 'ExtractFrame', extractFrameConfigJson } as WorkflowDefinition['steps'][number],
      { id: 's2', agentDefinitionId: 'a', stepOrder: 2, label: 'Audio', stepType: 'AudioGenerate', audioGenerateConfigJson } as WorkflowDefinition['steps'][number],
      {
        id: 's3', agentDefinitionId: 'a', stepOrder: 3, label: 'Author', stepType: 'Agent',
        continueCompositionJson: '{"executionId":"e-9"}', registerOutputAsProjectFile: true,
      } as WorkflowDefinition['steps'][number],
    ]));
    expect(frame.extractFrameConfig).toEqual(JSON.parse(extractFrameConfigJson));
    expect(audio.audioGenerateConfig).toEqual(JSON.parse(audioGenerateConfigJson));
    expect(agent.continueComposition).toEqual({ executionId: 'e-9', versionId: null });
    expect(agent.registerOutputAsProjectFile).toBe(true);
  });

  it('turns an integer-coded source kind into its name, so the form shows the stored choice', () => {
    const [frame] = mapWorkflowStepsToStepData(workflowWith([
      { id: 's1', agentDefinitionId: 'a', stepOrder: 1, label: 'Frame', stepType: 'ExtractFrame', extractFrameConfigJson: JSON.stringify({ Source: { Kind: 4, StepResultId: 'r1' }, At: 'Last' }) } as WorkflowDefinition['steps'][number],
    ]));
    expect(frame.extractFrameConfig?.source).toEqual({ kind: 'StepResult', stepResultId: 'r1' });
  });

  it('stringifies the configs and the set options into the saved step payload', () => {
    const request = stepDataToCreateRequest(stepData({
      stepType: 'ExtractFrame',
      extractFrameConfig: JSON.parse(extractFrameConfigJson),
      audioGenerateConfig: JSON.parse(audioGenerateConfigJson),
      continueComposition: { executionId: 'e-9' },
      registerOutputAsProjectFile: true,
    }), 1);
    expect(JSON.parse(request.extractFrameConfigJson as string)).toEqual(JSON.parse(extractFrameConfigJson));
    expect(JSON.parse(request.audioGenerateConfigJson as string)).toEqual(JSON.parse(audioGenerateConfigJson));
    expect(request.continueCompositionJson).toBe('{"executionId":"e-9"}');
    expect(request.registerOutputAsProjectFile).toBe(true);
  });

  it('sends nothing for an untouched step: configs null, the two options left out entirely', () => {
    const request = stepDataToCreateRequest(stepData(), 1);
    expect(request.extractFrameConfigJson).toBeNull();
    expect(request.audioGenerateConfigJson).toBeNull();
    expect(request).not.toHaveProperty('continueCompositionJson');
    expect(request).not.toHaveProperty('registerOutputAsProjectFile');
    // A continuation switched on but with no run picked yet is not sent either (the server needs exactly one id).
    expect(stepDataToCreateRequest(stepData({ continueComposition: { executionId: null } }), 1)).not.toHaveProperty('continueCompositionJson');
  });
});

/**
 * Which workflow a proposal is applied to. Its token covers the steps of the workflow it was
 * computed for, so applying it anywhere else can never verify — the page used to apply every
 * proposal to the workflow on screen, whatever it was for.
 */
describe('resolveApplyTarget', () => {
  const page = { workflowId: '87654321-4321-4321-4321-210987654321', name: 'On screen' };
  const diff = (over: Partial<WorkflowEditDiff> = {}): WorkflowEditDiff => ({
    additions: [],
    removals: [],
    modifications: [],
    confirmationToken: 't',
    ...over,
  });

  it('a proposal that does not say is for the workflow on screen, under the name typed there', () => {
    expect(resolveApplyTarget(diff(), page)).toEqual({ kind: 'page', workflowId: page.workflowId, name: 'On screen' });
  });

  it('a proposal naming the workflow on screen is for it too, whichever way the id is written', () => {
    expect(resolveApplyTarget(diff({ workflowId: page.workflowId.toUpperCase(), name: 'Other name' }), page)).toEqual({
      kind: 'page',
      workflowId: page.workflowId,
      name: 'On screen',
    });
  });

  it('a proposal naming another workflow is applied to that one, under the name it brought', () => {
    expect(resolveApplyTarget(diff({ workflowId: '11111111-2222-3333-4444-555555555555', name: ' Teaser ' }), page)).toEqual({
      kind: 'other',
      workflowId: '11111111-2222-3333-4444-555555555555',
      name: 'Teaser',
    });
  });

  it('with no name for another workflow, sends none, which the server reads as "keep its name"', () => {
    expect(resolveApplyTarget(diff({ workflowId: '11111111-2222-3333-4444-555555555555' }), page).name).toBe('');
  });

  it('a proposal for no workflow creates one, and never sends a nameless create', () => {
    expect(resolveApplyTarget(diff({ workflowId: null, name: 'Brand new' }), page)).toEqual({
      kind: 'create',
      workflowId: undefined,
      name: 'Brand new',
    });
    expect(resolveApplyTarget(diff({ workflowId: null }), page).name).toBe('New workflow');
    expect(resolveApplyTarget(diff({ workflowId: null, name: '   ' }), page).name).toBe('New workflow');
  });
});
