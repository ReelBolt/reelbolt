/**
 * Tests for STEP_CONFIG_SCHEMAS: validation that schemas exist for all StepTypes,
 * match interface field shapes, and reject invalid data.
 */

import { describe, it, expect } from 'vitest';
import Ajv from 'ajv';
import { STEP_CONFIG_SCHEMAS } from './workflow-step-configs';
import { validateStepConfig } from './schema-form';
import { createDefaultVideoGenerateStepConfig } from '@/components/workflows/VideoGenerateStepConfig';
import { createDefaultConditionalStepConfig } from '@/components/workflows/ConditionalStepConfig';
import { createDefaultReviewLoopStepConfig } from '@/components/workflows/ReviewLoopStepConfig';
import { createDefaultRoomConveneConfig } from '@/components/workflows/RoomStepConfigEditor';
import { createDefaultVideoAnalyzeStepConfig } from '@/components/workflows/VideoAnalyzeStepConfig';
import { createDefaultThumbnailGenerateStepConfig } from '@/components/workflows/ThumbnailGenerateStepConfig';
import { createDefaultExtractFrameStepConfig } from '@/components/workflows/ExtractFrameStepConfig';
import { createDefaultPinInsertStepConfig } from '@/components/workflows/PinInsertStepConfig';
import { createDefaultAudioGenerateStepConfig } from '@/components/workflows/AudioGenerateStepConfig';
import { createDefaultImageGenerateStepConfig } from '@/components/workflows/ImageGenerateStepConfig';
import { createDefaultVisualCheckConfig } from '@/components/workflows/ReviewLoopStepConfig';
import { createDefaultVideoCompileStepConfig } from '@/components/workflows/VideoCompileStepConfig';
import { ROOM_DECISION_MODES, VIDEO_DERUSH_PRE_PASS_MODES } from '@/lib/types/workflow';
import type { StepType } from '@/lib/types/workflow';

const ALL_STEP_TYPES: StepType[] = [
  'Agent',
  'Conditional',
  'ForEach',
  'ReviewLoop',
  'Parallel',
  'Extract',
  'VideoAnalyze',
  'VideoCompile',
  'VideoGenerate',
  'EditRoom',
  'GraphicsRoom',
  'ColorGradeRoom',
  'Voiceover',
  'ThumbnailGenerate',
  'Revise',
  'DesignGraphic',
  'DailiesRoom',
  'TimelineRender',
  'DesignSystem',
  'ExtractFrame',
  'AudioGenerate',
  'PinInsert',
  'AudioCleanup',
  'ImageGenerate',
];

describe('STEP_CONFIG_SCHEMAS', () => {
  it('should export a schema for every StepType', () => {
    for (const stepType of ALL_STEP_TYPES) {
      expect(STEP_CONFIG_SCHEMAS[stepType]).toBeDefined();
      expect(STEP_CONFIG_SCHEMAS[stepType]).toHaveProperty('type');
    }
  });

  it('DesignSystem schema pins the three ops', () => {
    const schema = STEP_CONFIG_SCHEMAS.DesignSystem;
    const props = schema.properties as Record<string, Record<string, unknown>>;
    expect(props.op.enum).toEqual(['Parse', 'Compose', 'Require']);
    expect((props.compose.required as string[])).toContain('parseStepOrder');
    expect(schema.required).toContain('op');
    expect(schema.required).toContain('version');
  });

  describe('Extract schema', () => {
    const schema = STEP_CONFIG_SCHEMAS.Extract;

    it('should require version, operation, inputs, and other core fields', () => {
      expect(schema.required).toContain('version');
      expect(schema.required).toContain('operation');
      expect(schema.required).toContain('inputs');
      expect(schema.required).toContain('idPrefix');
      expect(schema.required).toContain('skip');
      expect(schema.required).toContain('onUnknownId');
      expect(schema.required).toContain('includeSummaries');
      expect(schema.required).toContain('includeContent');
      expect(schema.required).toContain('maxCharsPerFile');
      expect(schema.required).toContain('maxOutputChars');
    });

    it('should have properties for all Extract config fields', () => {
      const props = schema.properties as Record<string, unknown>;
      const expectedFields = [
        'version',
        'operation',
        'inputs',
        'path',
        'fields',
        'idField',
        'idPrefix',
        'sortBy',
        'take',
        'skip',
        'idsPath',
        'recordsPath',
        'onUnknownId',
        'categories',
        'includeExtensions',
        'excludePathContains',
        'includeSummaries',
        'includeContent',
        'maxCharsPerFile',
        'maxOutputChars',
        'expect',
      ];
      for (const field of expectedFields) {
        expect(props).toHaveProperty(field);
      }
    });

    it('should have definitions for ExtractInputRef and ExtractExpectation', () => {
      const defs = schema.definitions as Record<string, unknown>;
      expect(defs).toHaveProperty('ExtractInputRef');
      expect(defs).toHaveProperty('ExtractExpectation');
    });
  });

  describe('VideoAnalyze schema', () => {
    const schema = STEP_CONFIG_SCHEMAS.VideoAnalyze;

    it('should require version and core analysis fields', () => {
      expect(schema.required).toContain('version');
      expect(schema.required).toContain('detectSilence');
      expect(schema.required).toContain('detectShots');
      expect(schema.required).toContain('transcription');
      expect(schema.required).toContain('analyzeVisuals');
      expect(schema.required).toContain('analyzeAudioLevels');
    });

    it('should have properties for video analysis configuration', () => {
      const props = schema.properties as Record<string, unknown>;
      const expectedFields = [
        'version',
        'source',
        'sources',
        'detectSilence',
        'silenceThresholdDb',
        'minSilenceMs',
        'detectShots',
        'sceneThreshold',
        'transcription',
        'maxDurationSeconds',
        'maxInputBytes',
        'maxOutputChars',
        'analyzeVisuals',
        'detectSharpness',
        'analyzeAudioLevels',
        'detectNearDuplicates',
      ];
      for (const field of expectedFields) {
        expect(props).toHaveProperty(field);
      }
    });

    it('should have definitions for VideoSourceRef and VideoAnalyzeExpectation', () => {
      const defs = schema.definitions as Record<string, unknown>;
      expect(defs).toHaveProperty('VideoSourceRef');
      expect(defs).toHaveProperty('VideoAnalyzeExpectation');
    });
  });

  describe('VideoCompile schema', () => {
    const schema = STEP_CONFIG_SCHEMAS.VideoCompile;

    it('should require version and core compile fields', () => {
      expect(schema.required).toContain('version');
      expect(schema.required).toContain('decision');
      expect(schema.required).toContain('analysisStepOrder');
      expect(schema.required).toContain('mode');
      expect(schema.required).toContain('outputFileName');
      expect(schema.required).toContain('videoCodec');
      expect(schema.required).toContain('audioCodec');
    });

    it('should have properties for video compile configuration', () => {
      const props = schema.properties as Record<string, unknown>;
      const expectedFields = [
        'version',
        'decision',
        'analysisStepOrder',
        'mode',
        'prePaddingMs',
        'postPaddingMs',
        'maxSegments',
        'allowKeyframeSnapping',
        'outputFileName',
        'videoCodec',
        'audioCodec',
        'crf',
        'preset',
        'enableGraphics',
        'enableMusic',
        'enableColorGrade',
      ];
      for (const field of expectedFields) {
        expect(props).toHaveProperty(field);
      }
    });

    it('should have definitions for ExtractInputRef and VideoCompileExpectation', () => {
      const defs = schema.definitions as Record<string, unknown>;
      expect(defs).toHaveProperty('ExtractInputRef');
      expect(defs).toHaveProperty('VideoCompileExpectation');
    });

    it('mirrors the generated-clips fields appended after maxSfxCueSeconds', () => {
      const props = schema.properties as Record<string, Record<string, unknown>>;

      expect(props).toHaveProperty('generatedClips');
      expect(props).toHaveProperty('enableGeneratedClips');
      expect(props).toHaveProperty('maxGeneratedClips');

      // Same shapes as their siblings: a step ref, a tri-state switch, a positive-integer cap.
      expect(props.generatedClips).toEqual({ $ref: '#/definitions/ExtractInputRef' });
      expect(props.enableGeneratedClips.type).toEqual(['boolean', 'null']);
      expect(props.maxGeneratedClips.type).toEqual(['integer', 'null']);
      expect(props.maxGeneratedClips.minimum).toBe(1);

      // Not required — an existing config without them must still validate.
      expect(schema.required).not.toContain('generatedClips');
      expect(schema.required).not.toContain('enableGeneratedClips');
      expect(schema.required).not.toContain('maxGeneratedClips');

      // C# appends them AFTER maxSfxCueSeconds and BEFORE expect; the mirror keeps that order.
      const keys = Object.keys(props);
      expect(keys.indexOf('maxSfxCueSeconds')).toBeLessThan(keys.indexOf('generatedClips'));
      expect(keys.indexOf('generatedClips')).toBeLessThan(keys.indexOf('enableGeneratedClips'));
      expect(keys.indexOf('enableGeneratedClips')).toBeLessThan(keys.indexOf('maxGeneratedClips'));
      expect(keys.indexOf('maxGeneratedClips')).toBeLessThan(keys.indexOf('expect'));
    });

    it('mirrors takeSelection (the dailies room\'s answer) as an optional ExtractInputRef, appended last', () => {
      const props = schema.properties as Record<string, Record<string, unknown>>;
      expect(props).toHaveProperty('takeSelection');
      expect(props.takeSelection).toEqual({ $ref: '#/definitions/ExtractInputRef' });
      // Not required — a config written before the dailies room existed must still validate.
      expect(schema.required).not.toContain('takeSelection');
      // The C# record appends it after LoudnessTarget, the last member master had; so does the mirror.
      const keys = Object.keys(props);
      expect(keys.indexOf('loudnessTarget')).toBeLessThan(keys.indexOf('takeSelection'));
    });
  });

  describe('VideoCompile voiceover / caption / pickup mirror', () => {
    const schema = STEP_CONFIG_SCHEMAS.VideoCompile;

    it('mirrors every voiceover, caption, dialogue-mode and pickup member the C# record appends', () => {
      const props = schema.properties as Record<string, Record<string, unknown>>;
      const fields = [
        'enableVoiceover', 'voiceoverStepOrder',
        'enableCaptions', 'captionStyle', 'captionFontSizePct', 'captionFontColor', 'captionBoxColor',
        'captionHighlightColor', 'captionMaxCharsPerCue', 'maxCaptionCues',
        'voiceoverMode', 'voiceoverDuckDb', 'voiceoverDuckRampMs',
        'enablePickups', 'pickupVoiceoverStepOrder',
        'captionFont', 'captionPosition', 'muteSourceAudio', 'loudnessTarget',
      ];
      for (const field of fields) {
        expect(props).toHaveProperty(field);
        // Not required — a config saved before these existed must still validate.
        expect(schema.required).not.toContain(field);
      }
      expect(props.captionStyle.enum).toEqual(['Subtitle', 'Karaoke', 'Punch', 'Bold', null]);
      expect(props.captionFont.enum).toEqual(['Auto', 'Inter', 'Montserrat', 'Poppins', 'BebasNeue', 'Anton', 'DejaVuSans', null]);
      expect(props.captionPosition.enum).toEqual(['Auto', 'Bottom', 'Center', 'Top', null]);
      // 0 = the automatic size, so the floor is 0, not the explicit-size minimum of 2.
      expect(props.captionFontSizePct.minimum).toBe(0);
      expect(props.voiceoverMode.enum).toEqual(['OverMusic', 'DuckDialogue', 'ReplaceDialogue', null]);
      expect(props.enableCaptions.type).toEqual(['boolean', 'null']);
      expect(props.voiceoverStepOrder.minimum).toBe(1);
    });
  });

  describe('VideoCompile reel members (story order, beat sync, insert contents and dips)', () => {
    const schema = STEP_CONFIG_SCHEMAS.VideoCompile;
    const props = schema.properties as Record<string, Record<string, unknown>>;

    it('mirrors keepOrder as an optional closed enum', () => {
      expect(props.keepOrder.enum).toEqual(['SourceOrder', 'AsListed', null]);
      expect(schema.required).not.toContain('keepOrder');
    });

    it('mirrors insertContents and insertContentDips', () => {
      const items = (props.insertContents.items as { required: string[]; properties: Record<string, unknown> });
      expect(items.required).toEqual(['projectFileId']);
      expect(Object.keys(items.properties)).toEqual(['projectFileId', 'sourceIndex', 'regionId', 'fit', 'insertContentRef']);
      expect(props.insertContentDips.enum).toEqual(['Freeze', 'Cut', 'Keep', null]);
      expect(schema.required).not.toContain('insertContents');
      expect(schema.required).not.toContain('insertContentDips');
    });
  });

  describe('Room schemas (EditRoom, GraphicsRoom, ColorGradeRoom)', () => {
    it('EditRoom schema should require version and have room fields', () => {
      const schema = STEP_CONFIG_SCHEMAS.EditRoom;
      expect(schema.required).toContain('version');
      const props = schema.properties as Record<string, unknown>;
      expect(props).toHaveProperty('view');
      expect(props).toHaveProperty('rounds');
      expect(props).toHaveProperty('maxTurns');
      expect(props).toHaveProperty('persistTranscript');
      expect(props).toHaveProperty('fallbackToSoloEditor');
    });

    it('GraphicsRoom schema should require version and have fallbackToSoloPlanner', () => {
      const schema = STEP_CONFIG_SCHEMAS.GraphicsRoom;
      expect(schema.required).toContain('version');
      const props = schema.properties as Record<string, unknown>;
      expect(props).toHaveProperty('fallbackToSoloPlanner');
    });

    it('ColorGradeRoom schema should require version and have fallbackToSoloColorist', () => {
      const schema = STEP_CONFIG_SCHEMAS.ColorGradeRoom;
      expect(schema.required).toContain('version');
      const props = schema.properties as Record<string, unknown>;
      expect(props).toHaveProperty('fallbackToSoloColorist');
    });

    it('DailiesRoom schema is the shared room schema and has fallbackToSoloSelector', () => {
      const schema = STEP_CONFIG_SCHEMAS.DailiesRoom;
      expect(schema).toBe(STEP_CONFIG_SCHEMAS.EditRoom);
      expect(schema.required).toContain('version');
      const props = schema.properties as Record<string, unknown>;
      expect(props).toHaveProperty('fallbackToSoloSelector');
    });

    it('Room schemas should allow additionalProperties (for unknown fields)', () => {
      const editRoomSchema = STEP_CONFIG_SCHEMAS.EditRoom;
      expect(editRoomSchema.additionalProperties).toBe(true);

      const graphicsRoomSchema = STEP_CONFIG_SCHEMAS.GraphicsRoom;
      expect(graphicsRoomSchema.additionalProperties).toBe(true);

      const colorGradeRoomSchema = STEP_CONFIG_SCHEMAS.ColorGradeRoom;
      expect(colorGradeRoomSchema.additionalProperties).toBe(true);
    });

    /**
     * The convening gate (decision-engine phase 2). Asserted by PRESENCE and DEFAULT, never by exact
     * key-set equality: `ROOM_SCHEMA` is a deliberately PARTIAL mirror — it declares 8 of the room
     * records' many members and carries `additionalProperties: true` — so a transcribed-C#-list
     * equality assertion would be wrong here (unlike `VIDEO_GENERATE_SCHEMA`, which is a complete
     * mirror, see the VideoGenerate block below).
     *
     * Every default is checked twice: against the C# record's literal default, and against the
     * matching `createDefault*` factory — the house cross-check pattern that keeps the schema and the
     * TypeScript record from drifting apart.
     */
    const CONVENE_DEFAULTS = createDefaultRoomConveneConfig();

    for (const roomStepType of ['EditRoom', 'GraphicsRoom', 'ColorGradeRoom', 'DailiesRoom'] as const) {
      it(`${roomStepType} schema declares the convening-gate fields with the room records' C# defaults`, () => {
        const props = STEP_CONFIG_SCHEMAS[roomStepType].properties as Record<string, Record<string, unknown>>;

        // conveneGate — ConveneGateMode { Off, Shadow, Gate }, default Off
        expect(props).toHaveProperty('conveneGate');
        expect(props.conveneGate.type).toBe('string');
        expect(props.conveneGate.enum).toEqual(['Off', 'Shadow', 'Gate']);
        expect(props.conveneGate.default).toBe('Off');
        expect(props.conveneGate.default).toBe(CONVENE_DEFAULTS.conveneGate);

        // conveneAcceptAt — double, default 0.85
        expect(props).toHaveProperty('conveneAcceptAt');
        expect(props.conveneAcceptAt.type).toBe('number');
        expect(props.conveneAcceptAt.default).toBe(0.85);
        expect(props.conveneAcceptAt.default).toBe(CONVENE_DEFAULTS.conveneAcceptAt);

        // conveneMinMargin — double, default 0.25
        expect(props).toHaveProperty('conveneMinMargin');
        expect(props.conveneMinMargin.type).toBe('number');
        expect(props.conveneMinMargin.default).toBe(0.25);
        expect(props.conveneMinMargin.default).toBe(CONVENE_DEFAULTS.conveneMinMargin);

        // conveneProviderId — nullable Guid, default null
        expect(props).toHaveProperty('conveneProviderId');
        expect(props.conveneProviderId.type).toEqual(expect.arrayContaining(['string', 'null']));
        expect(props.conveneProviderId.default).toBeNull();
        expect(props.conveneProviderId.default).toBe(CONVENE_DEFAULTS.conveneProviderId);
      });
    }

    it('declares the convening-gate fields exactly once, in the one ROOM_SCHEMA all three rooms share', () => {
      // One shared schema object, not three copies — so the convene fields cannot be declared twice
      // and cannot drift between the rooms.
      expect(STEP_CONFIG_SCHEMAS.GraphicsRoom).toBe(STEP_CONFIG_SCHEMAS.EditRoom);
      expect(STEP_CONFIG_SCHEMAS.ColorGradeRoom).toBe(STEP_CONFIG_SCHEMAS.EditRoom);
    });
  });

  describe('ReviewLoop schema', () => {
    const schema = STEP_CONFIG_SCHEMAS.ReviewLoop;

    it('should have properties for minScore, maxIterations, and loopTargetStepOrder', () => {
      const props = schema.properties as Record<string, unknown>;
      expect(props).toHaveProperty('minScore');
      expect(props).toHaveProperty('maxIterations');
      expect(props).toHaveProperty('loopTargetStepOrder');
    });

    it('should allow additionalProperties for unknown fields', () => {
      expect(schema.additionalProperties).toBe(true);
    });

    it('should have correct property schemas', () => {
      const props = schema.properties as Record<string, Record<string, unknown>>;
      // minScore should be integer or null, 1-10
      expect(props.minScore.type).toEqual(expect.arrayContaining(['integer', 'null']));
      expect(props.minScore.minimum).toBe(1);
      expect(props.minScore.maximum).toBe(10);
      // maxIterations should be integer, 1-10
      expect(props.maxIterations.type).toBe('integer');
      expect(props.maxIterations.minimum).toBe(1);
      expect(props.maxIterations.maximum).toBe(10);
      // loopTargetStepOrder should be integer or null, minimum 1
      expect(props.loopTargetStepOrder.type).toEqual(expect.arrayContaining(['integer', 'null']));
      expect(props.loopTargetStepOrder.minimum).toBe(1);
    });

    /**
     * The calibrated early stop (decision-engine phase 2). Presence and defaults only — see the note
     * above the room block: `REVIEW_LOOP_SCHEMA` mixes `WorkflowStep` COLUMNS (`minScore`,
     * `maxIterations`, `loopTargetStepOrder`) with `ReviewLoopStepConfig` members, so an exact key-set
     * assertion against the record would be wrong.
     */
    it("declares the calibrated early-stop fields with ReviewLoopStepConfig.cs's C# defaults", () => {
      const props = schema.properties as Record<string, Record<string, unknown>>;
      const defaults = createDefaultReviewLoopStepConfig();

      // enabled — bool, default false
      expect(props).toHaveProperty('enabled');
      expect(props.enabled.type).toBe('boolean');
      expect(props.enabled.default).toBe(false);
      expect(props.enabled.default).toBe(defaults.enabled);

      // acceptAt — double, default 0.85
      expect(props).toHaveProperty('acceptAt');
      expect(props.acceptAt.type).toBe('number');
      expect(props.acceptAt.default).toBe(0.85);
      expect(props.acceptAt.default).toBe(defaults.acceptAt);

      // bimodalMargin — double, default 0.15
      expect(props).toHaveProperty('bimodalMargin');
      expect(props.bimodalMargin.type).toBe('number');
      expect(props.bimodalMargin.default).toBe(0.15);
      expect(props.bimodalMargin.default).toBe(defaults.bimodalMargin);

      // providerId — nullable Guid, default null
      expect(props).toHaveProperty('providerId');
      expect(props.providerId.type).toEqual(expect.arrayContaining(['string', 'null']));
      expect(props.providerId.default).toBeNull();
      expect(props.providerId.default).toBe(defaults.providerId);

      // timeoutSeconds — int, default 5
      expect(props).toHaveProperty('timeoutSeconds');
      expect(props.timeoutSeconds.type).toBe('integer');
      expect(props.timeoutSeconds.default).toBe(5);
      expect(props.timeoutSeconds.default).toBe(defaults.timeoutSeconds);
    });
  });

  describe('VideoGenerate schema', () => {
    const schema = STEP_CONFIG_SCHEMAS.VideoGenerate;
    const props = schema.properties as Record<string, Record<string, unknown>>;

    // camelCase mirror of inference/src/ReelBolt.Shared/Workflows/VideoGenerateStepConfig.cs
    const CS_FIELDS = [
      'planSource',
      'inlinePrompt',
      'plan',
      'planProjectFileId',
      'analysis',
      'model',
      'resolution',
      'durationSeconds',
      'ratio',
      'providerId',
      'takes',
      'maxClips',
      'maxSpendCredits',
      'allowSourceMediaEgress',
      'pollIntervalSeconds',
      'timeoutSeconds',
      'loopBackBehavior',
      'lookMatching',
      'cameraContinuity',
      'maxGpuMinutes',
      'conformFps',
      'conformHeight',
    ];

    it('declares exactly the 22 properties of VideoGenerateStepConfig.cs', () => {
      expect(Object.keys(props).sort()).toEqual([...CS_FIELDS].sort());
      expect(Object.keys(props)).toHaveLength(22);
    });

    it('bounds the phase-5 knobs the way the C# Validate() does', () => {
      expect(props.maxGpuMinutes.type).toBe('number');
      expect(props.maxGpuMinutes.minimum).toBe(0);
      expect(props.conformFps.type).toBe('integer');
      expect(props.conformFps.minimum).toBe(0);
      expect(props.conformFps.maximum).toBe(120);
      expect(props.conformHeight.type).toBe('integer');
      expect(props.conformHeight.minimum).toBe(0);
      expect(props.conformHeight.maximum).toBe(4320);
      expect(props.lookMatching.type).toBe('boolean');
      expect(props.cameraContinuity.type).toBe('boolean');
    });

    it('declares plan and analysis as ExtractInputRef references, in C# order', () => {
      expect(props.plan.$ref).toBe('#/definitions/ExtractInputRef');
      expect(props.analysis.$ref).toBe('#/definitions/ExtractInputRef');
      expect(Object.keys(props)).toEqual(CS_FIELDS);
    });

    it('bounds takes to an integer between 1 and 64', () => {
      expect(props.takes.type).toBe('integer');
      expect(props.takes.minimum).toBe(1);
      expect(props.takes.maximum).toBe(64);
    });

    it('bounds maxClips to an integer between 1 and 64 defaulting to 1', () => {
      expect(props.maxClips.type).toBe('integer');
      expect(props.maxClips.minimum).toBe(1);
      expect(props.maxClips.maximum).toBe(64);
      expect(props.maxClips.default).toBe(1);
      expect(props.maxClips.default).toBe(createDefaultVideoGenerateStepConfig().maxClips);
    });

    it('documents that takes must not exceed maxClips', () => {
      expect(props.maxClips.description).toEqual(expect.stringContaining('takes must be <= maxClips'));
    });

    it('requires maxSpendCredits as a strictly positive number', () => {
      expect(props.maxSpendCredits.type).toBe('number');
      expect(props.maxSpendCredits.exclusiveMinimum).toBe(0);
      expect(schema.required).toContain('maxSpendCredits');
    });

    it('restricts planSource to Inline, StepRef and ProjectFile', () => {
      expect(props.planSource.enum).toEqual(['Inline', 'StepRef', 'ProjectFile']);
    });

    it('caps inlinePrompt at 7000 characters', () => {
      expect(props.inlinePrompt.maxLength).toBe(7000);
    });

    it('allows providerId to be null', () => {
      expect(props.providerId.type).toEqual(expect.arrayContaining(['string', 'null']));
    });

    it('matches the keys of createDefaultVideoGenerateStepConfig()', () => {
      expect(Object.keys(props).sort()).toEqual(
        Object.keys(createDefaultVideoGenerateStepConfig()).sort(),
      );
    });

    it('bounds pollIntervalSeconds to an integer between 1 and 300', () => {
      expect(props.pollIntervalSeconds.type).toBe('integer');
      expect(props.pollIntervalSeconds.minimum).toBe(1);
      expect(props.pollIntervalSeconds.maximum).toBe(300);
    });

    it('bounds timeoutSeconds to an integer between 60 and 7200 and documents timeout >= poll', () => {
      expect(props.timeoutSeconds.type).toBe('integer');
      expect(props.timeoutSeconds.minimum).toBe(60);
      expect(props.timeoutSeconds.maximum).toBe(7200);
      expect(props.timeoutSeconds.description).toEqual(expect.stringContaining('>= pollIntervalSeconds'));
    });
  });

  describe('Generic schemas (Agent, Conditional, ForEach, Parallel)', () => {
    const genericStepTypes: StepType[] = ['Agent', 'Conditional', 'ForEach', 'Parallel'];

    for (const stepType of genericStepTypes) {
      it(`${stepType} schema should allow any properties`, () => {
        const schema = STEP_CONFIG_SCHEMAS[stepType];
        expect(schema.type).toBe('object');
        expect(schema.additionalProperties).toBe(true);
      });
    }

    /**
     * Conditional's decision arm lives here rather than in a describe of its own, because Conditional
     * has no describe of its own — it is one of the four "generic" (additionalProperties: true)
     * schemas. Presence and defaults only: `CONDITIONAL_SCHEMA` mixes the `WorkflowStep` COLUMN
     * `conditionExpression` with `ConditionalStepConfig` members, so an exact key-set assertion
     * against the C# record would be wrong.
     */
    it("Conditional schema declares the phase-2 decision fields with ConditionalStepConfig.cs's C# defaults", () => {
      const props = STEP_CONFIG_SCHEMAS.Conditional.properties as Record<string, Record<string, unknown>>;
      const defaults = createDefaultConditionalStepConfig();

      // the pre-existing step column is untouched by the decision arm
      expect(props).toHaveProperty('conditionExpression');

      // mode — ConditionMode { Expression, Decision }, default Expression
      expect(props).toHaveProperty('mode');
      expect(props.mode.type).toBe('string');
      expect(props.mode.enum).toEqual(['Expression', 'Decision']);
      expect(props.mode.default).toBe('Expression');
      expect(props.mode.default).toBe(defaults.mode);

      // decisionQuestion — nullable string, default null
      expect(props).toHaveProperty('decisionQuestion');
      expect(props.decisionQuestion.type).toEqual(expect.arrayContaining(['string', 'null']));
      expect(props.decisionQuestion.default).toBeNull();
      expect(props.decisionQuestion.default).toBe(defaults.decisionQuestion);

      // threshold — double, default 0.5
      expect(props).toHaveProperty('threshold');
      expect(props.threshold.type).toBe('number');
      expect(props.threshold.default).toBe(0.5);
      expect(props.threshold.default).toBe(defaults.threshold);

      // providerId — nullable Guid, default null
      expect(props).toHaveProperty('providerId');
      expect(props.providerId.type).toEqual(expect.arrayContaining(['string', 'null']));
      expect(props.providerId.default).toBeNull();
      expect(props.providerId.default).toBe(defaults.providerId);

      // timeoutSeconds — int, default 5
      expect(props).toHaveProperty('timeoutSeconds');
      expect(props.timeoutSeconds.type).toBe('integer');
      expect(props.timeoutSeconds.default).toBe(5);
      expect(props.timeoutSeconds.default).toBe(defaults.timeoutSeconds);
    });
  });

  describe('Voiceover schema', () => {
    const schema = STEP_CONFIG_SCHEMAS.Voiceover;

    it('should describe an object that preserves unknown fields', () => {
      expect(schema.type).toBe('object');
      expect(schema.additionalProperties).toBe(true);
    });

    it('should require source and have properties for all Voiceover config fields', () => {
      expect(schema.required).toContain('source');
      const props = schema.properties as Record<string, unknown>;
      const expectedFields = [
        'source',
        'providerId',
        'model',
        'defaultVoiceId',
        'maxBytes',
      ];
      for (const field of expectedFields) {
        expect(props).toHaveProperty(field);
      }
    });

    it('should have definitions for VoiceoverSource and VoiceoverLine', () => {
      const defs = schema.definitions as Record<string, unknown>;
      expect(defs).toHaveProperty('VoiceoverSource');
      expect(defs).toHaveProperty('VoiceoverLine');
    });

    it('should have correct top-level property schemas', () => {
      const props = schema.properties as Record<string, Record<string, unknown>>;
      // source is a reference to the shared VoiceoverSource definition
      expect(props.source.$ref).toBe('#/definitions/VoiceoverSource');
      // providerId / model / defaultVoiceId are all nullable strings
      for (const field of ['providerId', 'model', 'defaultVoiceId']) {
        expect(props[field].type).toEqual(expect.arrayContaining(['string', 'null']));
      }
      // maxBytes is a positive byte count
      expect(props.maxBytes.type).toBe('integer');
      expect(props.maxBytes.minimum).toBe(1);
    });

    it('should describe the VoiceoverSource shape (kind, lines, scriptwriterStepOrder)', () => {
      const defs = schema.definitions as Record<string, Record<string, unknown>>;
      const sourceDef = defs.VoiceoverSource;
      expect(sourceDef.type).toBe('object');
      expect(sourceDef.required).toContain('kind');

      const sourceProps = sourceDef.properties as Record<string, Record<string, unknown>>;
      // kind is the closed VoiceoverSourceKind enum
      expect(sourceProps.kind.type).toBe('string');
      expect(sourceProps.kind.enum).toEqual(expect.arrayContaining(['Inline', 'ScriptScenes']));
      // lines is a nullable array of VoiceoverLine
      expect(sourceProps.lines.type).toEqual(expect.arrayContaining(['array', 'null']));
      const lineItems = sourceProps.lines.items as Record<string, unknown>;
      expect(lineItems.$ref).toBe('#/definitions/VoiceoverLine');
      // scriptwriterStepOrder is a nullable, 1-based step order
      expect(sourceProps.scriptwriterStepOrder.type).toEqual(expect.arrayContaining(['integer', 'null']));
      expect(sourceProps.scriptwriterStepOrder.minimum).toBe(1);
    });

    it('should describe the VoiceoverLine shape (text, startSec, voiceId)', () => {
      const defs = schema.definitions as Record<string, Record<string, unknown>>;
      const lineDef = defs.VoiceoverLine;
      expect(lineDef.type).toBe('object');
      expect(lineDef.required).toEqual(expect.arrayContaining(['text', 'startSec']));

      const lineProps = lineDef.properties as Record<string, Record<string, unknown>>;
      expect(lineProps.text.type).toBe('string');
      expect(lineProps.startSec.type).toBe('number');
      expect(lineProps.startSec.minimum).toBe(0);
      expect(lineProps.voiceId.type).toEqual(expect.arrayContaining(['string', 'null']));
    });

    it('should accept the default Voiceover config and reject an unknown source kind', () => {
      const validate = new Ajv({ allErrors: true, coerceTypes: false }).compile(schema);

      // Mirrors createDefaultVoiceoverStepConfig() in components/workflows/VoiceoverStepConfig.tsx
      const defaultConfig = {
        source: { kind: 'Inline', lines: [] },
        maxBytes: 2_000_000,
      };
      expect(validate(defaultConfig)).toBe(true);

      // The fully-populated shape must also validate (ScriptScenes + per-line overrides)
      const fullConfig = {
        source: {
          kind: 'ScriptScenes',
          scriptwriterStepOrder: 2,
          lines: [{ text: 'Hello', startSec: 1.5, voiceId: 'voice-1' }],
        },
        providerId: '11111111-1111-1111-1111-111111111111',
        model: 'fish-speech-1.5',
        defaultVoiceId: 'voice-2',
        maxBytes: 4_000_000,
      };
      expect(validate(fullConfig)).toBe(true);

      // An out-of-enum kind is rejected
      expect(validate({ source: { kind: 'Unknown' } })).toBe(false);
    });

    it('pins the VoiceoverSource kind enum to exactly the four kinds, in order', () => {
      const defs = schema.definitions as Record<string, Record<string, unknown>>;
      const sourceProps = defs.VoiceoverSource.properties as Record<string, Record<string, unknown>>;

      // A CLOSED enum, unlike the decision-engine enums above: the kind is a client-side
      // discriminant the editor switches on exhaustively, so a member the UI cannot render must
      // never be admitted. 'NarrationPlan' was appended LAST for wave 1 phase 2 — the two existing
      // members keep their positions, so an already-persisted config keeps validating unchanged.
      // 'PickupPlan' appended LAST for phase 4 (docs/voiceover.md "Pickups"); the three before it keep their positions.
      expect(sourceProps.kind.enum).toEqual(['Inline', 'ScriptScenes', 'NarrationPlan', 'PickupPlan']);
    });

    it('describes narrationWriterStepOrder as a nullable 1-based step order', () => {
      const defs = schema.definitions as Record<string, Record<string, unknown>>;
      const sourceProps = defs.VoiceoverSource.properties as Record<string, Record<string, unknown>>;

      expect(sourceProps).toHaveProperty('narrationWriterStepOrder');
      expect(sourceProps.narrationWriterStepOrder.type).toEqual(
        expect.arrayContaining(['integer', 'null']),
      );
      expect(sourceProps.narrationWriterStepOrder.minimum).toBe(1);
      // It mirrors scriptwriterStepOrder exactly: one shape behind the two step pickers.
      expect(sourceProps.narrationWriterStepOrder.type).toEqual(sourceProps.scriptwriterStepOrder.type);
      expect(sourceProps.narrationWriterStepOrder.minimum).toBe(
        sourceProps.scriptwriterStepOrder.minimum,
      );
    });

    it('accepts a NarrationPlan config that round-trips through validateStepConfig', () => {
      // The seam this wave closes: the config the editor emits once a NarrationWriter step is
      // chosen must survive the validator, or a step the user configured can never be saved.
      expect(
        validateStepConfig('Voiceover', {
          source: { kind: 'NarrationPlan', narrationWriterStepOrder: 3 },
          maxBytes: 2_000_000,
        }),
      ).toEqual({ valid: true, errors: [] });

      // The shape the editor holds the moment NarrationPlan is selected — a null order — validates
      // too: "unconfigured" is getVoiceoverSourceError's verdict, never the schema's, so switching
      // into this mode can never make a save impossible.
      expect(
        validateStepConfig('Voiceover', {
          source: { kind: 'NarrationPlan', narrationWriterStepOrder: null },
        }),
      ).toEqual({ valid: true, errors: [] });
    });

    it('rejects narrationWriterStepOrder 0, agreeing with getVoiceoverSourceError', () => {
      // Step orders are 1-based in both layers. The node summary's T-004 defect was a display
      // fallback that read order 0 as configured; the schema must not be the layer that admits a
      // never-valid order.
      expect(
        validateStepConfig('Voiceover', {
          source: { kind: 'NarrationPlan', narrationWriterStepOrder: 0 },
        }).valid,
      ).toBe(false);
    });
  });
});

/**
 * ThumbnailGenerate — deterministic frame extraction (no model call).
 *
 * Unlike the room/conditional/voiceover schemas, this one is a COMPLETE 1:1 mirror of the C#
 * `ThumbnailGenerateStepConfig` record, so it IS asserted by exact key-set equality. The key list is
 * checked against the `createDefault*` factory's own key order as well as the record's declared
 * order, because the C# record is POSITIONAL: a member inserted in the middle would silently
 * reinterpret every config already persisted under the old positions.
 */
describe('ThumbnailGenerate schema', () => {
  const schema = STEP_CONFIG_SCHEMAS.ThumbnailGenerate;
  const props = schema.properties as Record<string, Record<string, unknown>>;

  /** The C# record's declared parameter order — the order persisted JSON is interpreted in. */
  const RECORD_ORDER = [
    'version',
    'analysisStepOrder',
    'analysisStepResultId',
    'selection',
    'atTimeSec',
    'sourceIndex',
    'width',
    'height',
    'fit',
    'jpegQuality',
    'outputFileName',
    'registerProjectFile',
    'maxCandidates',
    'minShotSeconds',
    'preferBestTake',
    'overlayText',
    'overlayPosition',
    'overlayFontSizePct',
    'overlayFontColor',
    'overlayBoxColor',
    'maxOverlayChars',
  ];

  it('mirrors the record 1:1, in the record\'s own order', () => {
    expect(Object.keys(props)).toEqual(RECORD_ORDER);
    // The factory is the third description of the same shape; a key added to only one of the two
    // fails here rather than silently diverging.
    expect(Object.keys(createDefaultThumbnailGenerateStepConfig())).toEqual(RECORD_ORDER);
  });

  it('requires exactly version and analysisStepOrder', () => {
    expect(schema.required).toEqual(['version', 'analysisStepOrder']);
  });

  it('declares the record\'s three closed enums', () => {
    expect(props.selection.enum).toEqual(['Auto', 'AtTime']);
    expect(props.fit.enum).toEqual(['Cover', 'Contain']);
    expect(props.overlayPosition.enum).toEqual(['None', 'LowerThird', 'UpperThird', 'Center']);
    // Every enum member the backend serializes by name is admissible, and nothing else is — these
    // reach ffmpeg's argv through first-party tables, so an unknown member must not validate.
    for (const field of ['selection', 'fit', 'overlayPosition'] as const) {
      expect(props[field].type).toBe('string');
    }
  });

  it('bounds every field the record\'s Validate() or the executor clamps', () => {
    expect(props.width).toMatchObject({ type: 'integer', minimum: 16, maximum: 4096 });
    expect(props.height).toMatchObject({ type: 'integer', minimum: 16, maximum: 4096 });
    expect(props.jpegQuality).toMatchObject({ type: 'integer', minimum: 2, maximum: 31 });
    expect(props.maxCandidates).toMatchObject({ type: 'integer', minimum: 1, maximum: 500 });
    expect(props.overlayFontSizePct).toMatchObject({ type: 'integer', minimum: 2, maximum: 25 });
    expect(props.maxOverlayChars).toMatchObject({ type: 'integer', minimum: 1, maximum: 400 });
    expect(props.atTimeSec.minimum).toBe(0);
    expect(props.sourceIndex.minimum).toBe(0);
    expect(props.minShotSeconds.minimum).toBe(0);
  });

  it('declares the record\'s own defaults, and none for its two required parameters', () => {
    const defaults = createDefaultThumbnailGenerateStepConfig() as unknown as Record<string, unknown>;
    const optional = RECORD_ORDER.filter((field) => !(schema.required as string[]).includes(field));

    // Compared as one object, so a field whose default drifted is named in the diff rather than
    // being reported one field at a time.
    expect(Object.fromEntries(optional.map((name) => [name, props[name].default])))
      .toEqual(Object.fromEntries(optional.map((name) => [name, defaults[name]])));

    // The C# record is POSITIONAL: `Version` and `AnalysisStepOrder` are required parameters and
    // carry no default value, so the schema must not invent one — a default there would tell a
    // form-driven consumer it may omit a field the executor refuses to run without.
    expect(props.version.default).toBeUndefined();
    expect(props.analysisStepOrder.default).toBeUndefined();
  });

  it('accepts the default config and rejects every out-of-range or out-of-enum value', () => {
    const defaults = createDefaultThumbnailGenerateStepConfig();
    expect(validateStepConfig('ThumbnailGenerate', { ...defaults })).toEqual({ valid: true, errors: [] });

    for (const [field, value] of [
      ['width', 15], ['width', 4097],
      ['height', 15], ['height', 4097],
      ['jpegQuality', 1], ['jpegQuality', 32],
      ['maxCandidates', 0], ['maxCandidates', 501],
      ['overlayFontSizePct', 1], ['overlayFontSizePct', 26],
      ['maxOverlayChars', 0], ['maxOverlayChars', 401],
      ['atTimeSec', -1],
      ['sourceIndex', -1],
      ['minShotSeconds', -0.1],
      ['selection', 'Manual'],
      ['fit', 'Stretch'],
      ['overlayPosition', 'TopLeft'],
    ] as const) {
      expect(validateStepConfig('ThumbnailGenerate', { ...defaults, [field]: value }).valid).toBe(false);
    }

    // The inclusive endpoints are accepted, so a config at the clamp boundary is never unsaveable.
    expect(validateStepConfig('ThumbnailGenerate', { ...defaults, width: 16, height: 4096 }).valid).toBe(true);
  });

  it('describes the Auto/AtTime choice as what it does for the customer', () => {
    // The picker copy is machine-readable and reaches the assistant's ListStepSchemas payload, so the
    // one thing an author must know — Auto needs a Watch footage step — is stated in the schema text.
    expect(props.selection.description).toEqual(expect.stringContaining('Watch footage'));
    expect(props.analysisStepOrder.description).toEqual(expect.stringContaining('May be 0 only when'));
  });
});

/**
 * decision-engine-p3 (T-301) — the new decision-engine step-config fields.
 * Mirrors the backend `VideoAnalyzeStepConfig.cs` derush/screening fields and the shared
 * `IRoomStepConfig.DecisionScheduling` field on all three room configs.
 */
describe('decision-engine-p3 step-config fields', () => {
  /** The four new VideoAnalyze keys, in the order they were appended to the C# record. */
  const NEW_ANALYZE_FIELDS = [
    'derushPrePass',
    'derushAcceptAt',
    'derushMinMargin',
    'screenInstructionLikeSpans',
  ] as const;

  const ROOM_STEP_TYPES: StepType[] = ['EditRoom', 'GraphicsRoom', 'ColorGradeRoom', 'DailiesRoom'];

  const analyzeProps = STEP_CONFIG_SCHEMAS.VideoAnalyze.properties as Record<
    string,
    Record<string, unknown>
  >;
  const roomProps = STEP_CONFIG_SCHEMAS.EditRoom.properties as Record<
    string,
    Record<string, unknown>
  >;

  /**
   * A valid VideoAnalyze config as it would have been persisted BEFORE this ticket existed — i.e.
   * carrying none of the four new keys.
   *
   * `expect: null` is dropped on purpose: `createDefaultVideoAnalyzeStepConfig()` emits it, but the
   * pre-existing `expect` `$ref` (`VideoAnalyzeExpectation`, `type: 'object'`) does not allow null,
   * so that one key already fails validation on `origin/master`. That mismatch is NOT this ticket's
   * to fix — the test directly below pins it as unchanged, so this ticket cannot be accused of
   * silently altering existing validation in either direction.
   */
  const legacyAnalyzeConfig = (): Record<string, unknown> => {
    const config: Record<string, unknown> = {
      ...(createDefaultVideoAnalyzeStepConfig() as unknown as Record<string, unknown>),
    };
    delete config.expect;
    return config;
  };

  describe('registry exposure', () => {
    it('exposes all four new VideoAnalyze properties', () => {
      for (const field of NEW_ANALYZE_FIELDS) {
        expect(analyzeProps).toHaveProperty(field);
      }
    });

    it('exposes decisionScheduling on all three room step types', () => {
      for (const stepType of ROOM_STEP_TYPES) {
        const props = STEP_CONFIG_SCHEMAS[stepType].properties as Record<string, unknown>;
        expect(props).toHaveProperty('decisionScheduling');
      }
      // All three rooms are bindings of ONE shared backend config surface, so the three must be
      // the identical schema object — a divergence here would mean one room drifted.
      expect(STEP_CONFIG_SCHEMAS.GraphicsRoom).toBe(STEP_CONFIG_SCHEMAS.EditRoom);
      expect(STEP_CONFIG_SCHEMAS.ColorGradeRoom).toBe(STEP_CONFIG_SCHEMAS.EditRoom);
    });

    it('declares the member lists once, and includes the wave-2 members', () => {
      expect(VIDEO_DERUSH_PRE_PASS_MODES).toEqual(['Off', 'Prior', 'Fast']);
      expect(ROOM_DECISION_MODES).toEqual(['Off', 'Questions', 'Adaptive']);
      // The schema advertises the shared list rather than a re-typed copy.
      expect(analyzeProps.derushPrePass.examples).toEqual([...VIDEO_DERUSH_PRE_PASS_MODES]);
      expect(roomProps.decisionScheduling.examples).toEqual([...ROOM_DECISION_MODES]);
    });

    /**
     * QA-2 (T-302). `Fast` and `Adaptive` are declared by wave 1 but behave EXACTLY like `Prior`
     * and `Questions` respectively — wave 2 implements them. The caveat must live in the schema
     * TEXT, not only in a source comment: the description is machine-readable and survives into the
     * serialized schema, while a comment is visible only to a human reading the source file.
     *
     * Asserted on the serialized property rather than on a bare in-memory getter, so the test fails
     * if the caveat is present in the object but dropped on the way out.
     */
    it('carries the not-yet-implemented caveat in the emitted schema JSON for both enums', () => {
      const analyzeCaveat = analyzeProps.derushPrePass.description as string;
      const roomCaveat = roomProps.decisionScheduling.description as string;

      // `Fast` does not skip the editor — it is `Prior`.
      expect(analyzeCaveat).toEqual(expect.stringContaining('`Fast`'));
      expect(analyzeCaveat).toEqual(expect.stringContaining('behaves exactly like `Prior`'));
      expect(analyzeCaveat).toEqual(expect.stringContaining('does not skip the editor'));
      expect(analyzeCaveat).toEqual(expect.stringContaining('is not available yet'));

      // `Adaptive` does not act on the answers — it is `Questions`.
      expect(roomCaveat).toEqual(expect.stringContaining('`Adaptive`'));
      expect(roomCaveat).toEqual(expect.stringContaining('behaves exactly like `Questions`'));
      expect(roomCaveat).toEqual(expect.stringContaining('nothing acts on the answers'));
      expect(roomCaveat).toEqual(expect.stringContaining('is not available yet'));

      // Both, and only these two, are described as unavailable rather than as working settings.
      for (const caveat of [analyzeCaveat, roomCaveat]) {
        expect(caveat).toEqual(expect.stringContaining('is not available yet'));
      }

      // The caveat survives serialization — this is the payload shape an assistant receives.
      const emittedAnalyze = JSON.stringify(analyzeProps.derushPrePass);
      const emittedRoom = JSON.stringify(roomProps.decisionScheduling);
      expect(emittedAnalyze).toContain('behaves exactly like `Prior`');
      expect(emittedRoom).toContain('behaves exactly like `Questions`');

      // The caveat is an ANNOTATION, not a gate: `enum` stays absent, so every declared member —
      // including the two placeholders — still validates. The `forward compatibility` block below
      // pins the tolerance behaviour; this pins that adding the caveat did not quietly turn the
      // property into a closed enum.
      expect(analyzeProps.derushPrePass.enum).toBeUndefined();
      expect(roomProps.decisionScheduling.enum).toBeUndefined();
    });
  });

  describe('defaults', () => {
    it('declares the backend defaults', () => {
      expect(analyzeProps.derushPrePass.default).toBe('Off');
      expect(analyzeProps.derushAcceptAt.default).toBe(0.85);
      expect(analyzeProps.derushMinMargin.default).toBe(0.25);
      expect(analyzeProps.screenInstructionLikeSpans.default).toBe(false);
      expect(roomProps.decisionScheduling.default).toBe('Off');
    });

    it('declares the derush thresholds as DecisionGateConfig-mirroring numbers', () => {
      // Same 0.85/0.25 the plan pins to DecisionGateConfig — one definition of "confident".
      expect(analyzeProps.derushAcceptAt.default).toBe(0.85);
      expect(analyzeProps.derushMinMargin.default).toBe(0.25);
      expect(analyzeProps.derushAcceptAt.type).toBe('number');
      expect(analyzeProps.derushMinMargin.type).toBe('number');
    });

    it('types screenInstructionLikeSpans and the enum as a boolean and a string', () => {
      expect(analyzeProps.screenInstructionLikeSpans.type).toBe('boolean');
      expect(analyzeProps.derushPrePass.type).toBe('string');
      expect(roomProps.decisionScheduling.type).toBe('string');
    });
  });

  describe('bounds', () => {
    it('bounds derushAcceptAt and derushMinMargin to [0, 1]', () => {
      for (const field of ['derushAcceptAt', 'derushMinMargin'] as const) {
        expect(analyzeProps[field].minimum).toBe(0);
        expect(analyzeProps[field].maximum).toBe(1);
      }
    });

    it('actually rejects out-of-range thresholds through validateStepConfig', () => {
      const base = legacyAnalyzeConfig();
      expect(validateStepConfig('VideoAnalyze', { ...base, derushAcceptAt: 1.5 }).valid).toBe(false);
      expect(validateStepConfig('VideoAnalyze', { ...base, derushAcceptAt: -0.01 }).valid).toBe(false);
      expect(validateStepConfig('VideoAnalyze', { ...base, derushMinMargin: 2 }).valid).toBe(false);
      expect(validateStepConfig('VideoAnalyze', { ...base, derushMinMargin: -0.5 }).valid).toBe(false);
      // ...and accepts the inclusive endpoints.
      expect(validateStepConfig('VideoAnalyze', { ...base, derushAcceptAt: 1 }).valid).toBe(true);
      expect(validateStepConfig('VideoAnalyze', { ...base, derushMinMargin: 0 }).valid).toBe(true);
    });
  });

  describe('backward compatibility', () => {
    it('requires none of the new keys, on VideoAnalyze or on the rooms', () => {
      const analyzeRequired = STEP_CONFIG_SCHEMAS.VideoAnalyze.required as string[];
      for (const field of NEW_ANALYZE_FIELDS) {
        expect(analyzeRequired).not.toContain(field);
      }
      expect(STEP_CONFIG_SCHEMAS.EditRoom.required as string[]).not.toContain('decisionScheduling');
    });

    it('still validates an existing config carrying none of the new keys, unchanged', () => {
      const legacy = legacyAnalyzeConfig();

      // Guard the test's own premise: this really is a pre-change config object.
      for (const field of NEW_ANALYZE_FIELDS) {
        expect(legacy).not.toHaveProperty(field);
      }

      expect(validateStepConfig('VideoAnalyze', legacy)).toEqual({ valid: true, errors: [] });
      // Same object, validated twice: no validation-time mutation (AJV must not inject defaults).
      expect(validateStepConfig('VideoAnalyze', legacy)).toEqual({ valid: true, errors: [] });
      for (const field of NEW_ANALYZE_FIELDS) {
        expect(legacy).not.toHaveProperty(field);
      }
    });

    it('does not alter the validation of any pre-existing VideoAnalyze field', () => {
      // The ONLY pre-existing key the default config emits that the schema rejects is `expect: null`
      // (the `$ref` is a non-nullable object). Pinning the exact error set proves the new fields
      // neither fixed nor broke existing behaviour as a side effect.
      const withNullExpect = {
        ...legacyAnalyzeConfig(),
        expect: null,
      };
      expect(validateStepConfig('VideoAnalyze', withNullExpect)).toEqual({
        valid: false,
        errors: [{ path: '/expect', message: 'must be object' }],
      });
    });

    it('still validates a room config carrying none of the new keys', () => {
      const legacyRoom = { version: 1, rounds: 2, maxTurns: 8, persistTranscript: true };
      expect(legacyRoom).not.toHaveProperty('decisionScheduling');
      expect(validateStepConfig('EditRoom', legacyRoom)).toEqual({ valid: true, errors: [] });
      expect(validateStepConfig('GraphicsRoom', legacyRoom)).toEqual({ valid: true, errors: [] });
      expect(validateStepConfig('ColorGradeRoom', legacyRoom)).toEqual({ valid: true, errors: [] });
    });
  });

  describe('forward compatibility (the wave-2 enum members)', () => {
    it('accepts the wave-2 members Fast and Adaptive', () => {
      const base = legacyAnalyzeConfig();
      expect(validateStepConfig('VideoAnalyze', { ...base, derushPrePass: 'Fast' }).valid).toBe(true);
      expect(validateStepConfig('VideoAnalyze', { ...base, derushPrePass: 'Prior' }).valid).toBe(true);
      expect(validateStepConfig('EditRoom', { version: 1, decisionScheduling: 'Adaptive' }).valid).toBe(true);
      expect(validateStepConfig('EditRoom', { version: 1, decisionScheduling: 'Questions' }).valid).toBe(true);
    });

    it('accepts an unknown/extra member, so a newer backend never dead-ends the builder', () => {
      const base = legacyAnalyzeConfig();
      for (const member of ['Turbo', 'wave3-mode', 'OFF', '']) {
        expect(validateStepConfig('VideoAnalyze', { ...base, derushPrePass: member })).toEqual({
          valid: true,
          errors: [],
        });
        expect(validateStepConfig('EditRoom', { version: 1, decisionScheduling: member })).toEqual({
          valid: true,
          errors: [],
        });
      }
    });

    it('still rejects a non-string member even though the member list is open', () => {
      const base = legacyAnalyzeConfig();
      expect(validateStepConfig('VideoAnalyze', { ...base, derushPrePass: 3 }).valid).toBe(false);
      expect(validateStepConfig('EditRoom', { version: 1, decisionScheduling: true }).valid).toBe(false);
    });
  });
});

/**
 * video-generation-complete (issue #93 phase 4) — the compile step's `takeSelection` reference,
 * validated through `validateStepConfig` rather than only asserted on the schema object.
 */
describe('VideoCompile take-selection field (issue #93 phase 4)', () => {
  /**
   * A config written BEFORE the dailies room existed. Built from the factory and then stripped of
   * the keys whose `$ref`s are non-nullable objects the factory emits as `null` (`expect`,
   * `graphicsPlan`, `generatedClips`, `takeSelection`), plus the two music fades the schema lists as
   * required and the factory omits — all pre-existing factory/schema mismatches, recorded so the
   * test measures what it claims to.
   */
  const legacyCompileConfig = (): Record<string, unknown> => {
    const config = {
      ...createDefaultVideoCompileStepConfig(),
      musicFadeInMs: 1500,
      musicFadeOutMs: 2500,
    } as unknown as Record<string, unknown>;
    delete config.expect;
    delete config.graphicsPlan;
    delete config.generatedClips;
    delete config.takeSelection;
    return config;
  };

  it('still validates a config carrying no takeSelection, unchanged', () => {
    const legacy = legacyCompileConfig();
    expect(legacy).not.toHaveProperty('takeSelection');
    expect(legacy.transitionPolicy).toBe('Off');

    expect(validateStepConfig('VideoCompile', legacy)).toEqual({ valid: true, errors: [] });
    // Same object, validated twice: no validation-time mutation.
    expect(validateStepConfig('VideoCompile', legacy)).toEqual({ valid: true, errors: [] });
  });

  it('accepts a takeSelection step reference and rejects a malformed one', () => {
    const base = legacyCompileConfig();
    expect(
      validateStepConfig('VideoCompile', { ...base, takeSelection: { from: 'Step', stepOrder: 6 } }),
    ).toEqual({ valid: true, errors: [] });
    expect(validateStepConfig('VideoCompile', { ...base, takeSelection: { from: 'Sideways' } }).valid).toBe(false);
    // The transition policy stays a CLOSED enum — that is what makes the Select a fixed allowlist.
    expect(validateStepConfig('VideoCompile', { ...base, transitionPolicy: 'Sideways' }).valid).toBe(false);
  });
});

describe('VideoCompile insert-sound fields (the sound of an inserted clip)', () => {
  /**
   * A config written BEFORE the inserted clip's sound existed. Built exactly like the take-selection
   * suite's legacy config: stripped of the keys whose `$ref`s are non-nullable objects the factory
   * emits as `null`, plus the two music fades the schema lists as required and the factory omits.
   */
  const preInsertSoundConfig = (): Record<string, unknown> => {
    const config = {
      ...createDefaultVideoCompileStepConfig(),
      musicFadeInMs: 1500,
      musicFadeOutMs: 2500,
    } as unknown as Record<string, unknown>;
    delete config.expect;
    delete config.graphicsPlan;
    delete config.generatedClips;
    delete config.takeSelection;
    return config;
  };

  it('mirrors the pair as optional, appended after takeSelection in the C# record order', () => {
    const schema = STEP_CONFIG_SCHEMAS.VideoCompile;
    const props = schema.properties as Record<string, Record<string, unknown>>;

    // Same shapes as their siblings: a tri-state switch and an integer knob.
    expect(props.mixInsertAudio.type).toEqual(['boolean', 'null']);
    expect(props.insertAudioGainDb.type).toEqual(['integer', 'null']);
    // The engine's own range — outside it the step fails INSERT_AUDIO_GAIN_OUT_OF_RANGE.
    expect(props.insertAudioGainDb.minimum).toBe(-24);
    expect(props.insertAudioGainDb.maximum).toBe(12);

    // Not required — a config saved before the feature existed must still validate.
    expect(schema.required).not.toContain('mixInsertAudio');
    expect(schema.required).not.toContain('insertAudioGainDb');

    // The C# record appends them after TakeSelection, the last member master had; so does the mirror.
    const keys = Object.keys(props);
    expect(keys.indexOf('takeSelection')).toBeLessThan(keys.indexOf('mixInsertAudio'));
    expect(keys.indexOf('mixInsertAudio')).toBeLessThan(keys.indexOf('insertAudioGainDb'));
  });

  it('still validates a config carrying neither field, unchanged', () => {
    const config = preInsertSoundConfig();
    expect(config).not.toHaveProperty('mixInsertAudio');
    expect(config).not.toHaveProperty('insertAudioGainDb');

    expect(validateStepConfig('VideoCompile', config)).toEqual({ valid: true, errors: [] });
    // Same object, validated twice: no validation-time mutation.
    expect(validateStepConfig('VideoCompile', config)).toEqual({ valid: true, errors: [] });
  });

  it('accepts the pair and rejects a gain outside -24..12', () => {
    const config = preInsertSoundConfig();
    expect(validateStepConfig('VideoCompile', { ...config, mixInsertAudio: true, insertAudioGainDb: -6 }))
      .toEqual({ valid: true, errors: [] });
    expect(validateStepConfig('VideoCompile', { ...config, insertAudioGainDb: -24 }).valid).toBe(true);
    expect(validateStepConfig('VideoCompile', { ...config, insertAudioGainDb: 12 }).valid).toBe(true);
    expect(validateStepConfig('VideoCompile', { ...config, insertAudioGainDb: -25 }).valid).toBe(false);
    expect(validateStepConfig('VideoCompile', { ...config, insertAudioGainDb: 13 }).valid).toBe(false);
  });
});


describe('ExtractFrame schema (ExtractFrameStepConfig.cs)', () => {
  const schema = STEP_CONFIG_SCHEMAS.ExtractFrame;
  const props = schema.properties as Record<string, Record<string, unknown>>;

  it('mirrors the record 1:1 and requires only the source', () => {
    expect(Object.keys(props).sort()).toEqual(
      ['source', 'at', 'atSec', 'format', 'maxLongSide', 'crop', 'registerProjectFile'].sort(),
    );
    // The factory carries every key but the optional crop.
    for (const key of Object.keys(createDefaultExtractFrameStepConfig())) expect(props).toHaveProperty(key);
    expect(schema.required).toEqual(['source']);
    expect(schema.additionalProperties).toBe(false);
  });

  it('pins the closed enums and the server\'s bounds', () => {
    expect(props.at.enum).toEqual(['Time', 'Last', 'First']);
    expect(props.format.enum).toEqual(['png', 'jpeg']);
    expect(props.maxLongSide).toMatchObject({ minimum: 16, maximum: 8192 });
  });

  it('accepts the factory default and every source kind, and rejects an unknown frame, format or key', () => {
    expect(validateStepConfig('ExtractFrame', createDefaultExtractFrameStepConfig()).valid).toBe(true);
    for (const kind of ['ProjectFile', 'StepOutput', 'PreviousStepOutput', 'StepResult']) {
      expect(validateStepConfig('ExtractFrame', { ...createDefaultExtractFrameStepConfig(), source: { kind } }).valid).toBe(true);
    }
    expect(validateStepConfig('ExtractFrame', { ...createDefaultExtractFrameStepConfig(), at: 'Middle' }).valid).toBe(false);
    expect(validateStepConfig('ExtractFrame', { ...createDefaultExtractFrameStepConfig(), format: 'gif' }).valid).toBe(false);
    expect(validateStepConfig('ExtractFrame', { ...createDefaultExtractFrameStepConfig(), maxLongSide: 9000 }).valid).toBe(false);
    expect(validateStepConfig('ExtractFrame', { ...createDefaultExtractFrameStepConfig(), bogus: 1 }).valid).toBe(false);
    expect(validateStepConfig('ExtractFrame', { at: 'Last' }).valid).toBe(false);
  });
});

describe('PinInsert schema (PinInsertStepConfig.cs)', () => {
  const schema = STEP_CONFIG_SCHEMAS.PinInsert;
  const props = schema.properties as Record<string, Record<string, unknown>>;
  const filled = () => ({
    ...createDefaultPinInsertStepConfig(),
    pins: [{ regionId: 'r0', content: { from: 'Step', stepOrder: 2 } }],
  });

  it('mirrors the record 1:1 and requires the analysis and the pins', () => {
    expect(Object.keys(props).sort()).toEqual(['analysis', 'pins', 'requireApplied'].sort());
    for (const key of Object.keys(createDefaultPinInsertStepConfig())) expect(props).toHaveProperty(key);
    expect(schema.required).toEqual(['analysis', 'pins']);
    expect(schema.additionalProperties).toBe(false);
    expect(props.pins).toMatchObject({ minItems: 1, maxItems: 16 });
  });

  it('accepts a filled config and every content kind, and rejects a bad region, no pins or an unknown key', () => {
    expect(validateStepConfig('PinInsert', filled()).valid).toBe(true);
    for (const content of [{ from: 'ProjectFile', projectFileId: 'f' }, { from: 'StepResult', stepResultId: 'r' }]) {
      expect(validateStepConfig('PinInsert', { ...filled(), pins: [{ regionId: null, content }] }).valid).toBe(true);
    }
    expect(validateStepConfig('PinInsert', { ...filled(), pins: [{ regionId: 'screen', content: { from: 'Step', stepOrder: 2 } }] }).valid).toBe(false);
    expect(validateStepConfig('PinInsert', { ...filled(), pins: [] }).valid).toBe(false);
    expect(validateStepConfig('PinInsert', { ...filled(), bogus: 1 }).valid).toBe(false);
  });
});

describe('AudioGenerate schema (AudioGenerateStepConfig.cs)', () => {
  const schema = STEP_CONFIG_SCHEMAS.AudioGenerate;
  const props = schema.properties as Record<string, Record<string, unknown>>;

  it('mirrors the record 1:1, in the factory\'s own key set, requiring kind, prompt and durationSec', () => {
    expect(Object.keys(props).sort()).toEqual(Object.keys(createDefaultAudioGenerateStepConfig()).sort());
    expect(schema.required).toEqual(['kind', 'prompt', 'durationSec']);
    expect(schema.additionalProperties).toBe(false);
  });

  it('accepts the factory default and a full sound-effect step, and rejects bounds the server refuses', () => {
    const base = createDefaultAudioGenerateStepConfig();
    expect(validateStepConfig('AudioGenerate', base).valid).toBe(true);
    expect(validateStepConfig('AudioGenerate', { ...base, kind: 'SoundEffect', durationSec: 5, loop: true, promptInfluence: 0.3, takes: 4, maxSpendCredits: 2 }).valid).toBe(true);
    expect(validateStepConfig('AudioGenerate', { ...base, kind: 'Speech' }).valid).toBe(false);
    expect(validateStepConfig('AudioGenerate', { ...base, takes: 5 }).valid).toBe(false);
    expect(validateStepConfig('AudioGenerate', { ...base, promptInfluence: 1.5 }).valid).toBe(false);
    expect(validateStepConfig('AudioGenerate', { ...base, maxSpendCredits: 0 }).valid).toBe(false);
    expect(validateStepConfig('AudioGenerate', { ...base, fileNamePrefix: 'bad name!' }).valid).toBe(false);
  });
});

describe('ImageGenerate schema (ImageGenerateStepConfig.cs)', () => {
  const schema = STEP_CONFIG_SCHEMAS.ImageGenerate;
  const props = schema.properties as Record<string, Record<string, unknown>>;

  it('mirrors the record 1:1, in the factory\'s own key set, requiring only mode', () => {
    expect(Object.keys(props).sort()).toEqual(Object.keys(createDefaultImageGenerateStepConfig()).sort());
    expect(schema.required).toEqual(['mode']);
    expect(schema.additionalProperties).toBe(false);
  });

  it('accepts the factory default and an Edit step, and rejects bounds the server refuses', () => {
    const base = createDefaultImageGenerateStepConfig();
    expect(validateStepConfig('ImageGenerate', base).valid).toBe(true);
    expect(validateStepConfig('ImageGenerate', { ...base, mode: 'Edit', instruction: 'Make it night', source: { kind: 'ProjectFile', projectFileId: 'f1' }, allowSourceMediaEgress: true, maxSpendCredits: 2 }).valid).toBe(true);
    expect(validateStepConfig('ImageGenerate', { ...base, mode: 'Remix' }).valid).toBe(false);
    expect(validateStepConfig('ImageGenerate', { ...base, variations: 5 }).valid).toBe(false);
    expect(validateStepConfig('ImageGenerate', { ...base, maxSpendCredits: 0 }).valid).toBe(false);
    expect(validateStepConfig('ImageGenerate', { ...base, fileNamePrefix: 'bad name!' }).valid).toBe(false);
  });
});

describe('new optional fields on existing step schemas (T-102, T-104, T-106)', () => {
  it('ReviewLoop accepts the C# visualCheck defaults and rejects an out-of-range frame count', () => {
    const base = createDefaultReviewLoopStepConfig();
    expect(validateStepConfig('ReviewLoop', { ...base, visualCheck: createDefaultVisualCheckConfig() }).valid).toBe(true);
    expect(validateStepConfig('ReviewLoop', { ...base, visualCheck: { ...createDefaultVisualCheckConfig(), frames: 17 } }).valid).toBe(false);
    expect(validateStepConfig('ReviewLoop', { ...base, visualCheck: { ...createDefaultVisualCheckConfig(), safeMarginPercent: 21 } }).valid).toBe(false);
    // The field is absent from the factory: an untouched loop saves byte-identically.
    expect(base).not.toHaveProperty('visualCheck');
  });

  // The compile and analyze factories carry nulls their schemas already refuse (`expect`, `graphicsPlan`...), so
  // these assertions look only at the errors under the paths this change added.
  const errorsUnder = (stepType: StepType, config: unknown, ...prefixes: string[]) =>
    validateStepConfig(stepType, config).errors.filter((e) => prefixes.some((p) => e.path.startsWith(p)));
  const NEW_COMPILE_PATHS = ['/reframes', '/sourceFits', '/subjectSafeArea', '/insertContentRef'];

  it('VideoCompile accepts reframes, per-source fits, a safe area and an insert reference, and none are in the factory', () => {
    const base = createDefaultVideoCompileStepConfig();
    for (const key of ['reframes', 'sourceFits', 'subjectSafeArea', 'insertContentRef']) expect(base).not.toHaveProperty(key);
    const full = {
      ...base,
      mode: 'Reencode',
      reframes: [{ sourceIndex: 0, fromSec: 0, toSec: 6, keyframes: [{ atSec: 0, zoom: 1 }, { atSec: 6, zoom: 2, centerX: 0.4, easing: 'EaseInOut' }] }],
      sourceFits: { '0': 'Pad', '2': 'Subject' },
      subjectSafeArea: 0.1,
      insertContentRef: { from: 'Step', stepOrder: 2 },
    };
    expect(errorsUnder('VideoCompile', full, ...NEW_COMPILE_PATHS)).toEqual([]);
    expect(errorsUnder('VideoCompile', { ...full, subjectSafeArea: 0.5 }, '/subjectSafeArea')).not.toEqual([]);
    expect(errorsUnder('VideoCompile', { ...full, sourceFits: { '0': 'Stretch' } }, '/sourceFits')).not.toEqual([]);
    expect(errorsUnder('VideoCompile', { ...full, insertContentRef: { from: 'Url' } }, '/insertContentRef')).not.toEqual([]);
    expect(errorsUnder('VideoCompile', { ...full, reframes: [{ sourceIndex: 0, keyframes: [{ atSec: 0, zoom: 9 }] }] }, '/reframes')).not.toEqual([]);
    // null is the "not set" spelling for each, as for every other optional member.
    expect(errorsUnder('VideoCompile', { ...base, reframes: null, sourceFits: null, subjectSafeArea: null, insertContentRef: null }, ...NEW_COMPILE_PATHS)).toEqual([]);
  });

  it('a per-entry insert reference is admissible in insertContents', () => {
    const entry = { projectFileId: '', insertContentRef: { from: 'StepResult', stepResultId: 'abc' } };
    expect(errorsUnder('VideoCompile', { ...createDefaultVideoCompileStepConfig(), insertContents: [entry] }, '/insertContents')).toEqual([]);
  });

  it('a VideoSourceRef may now name a step result of an earlier run', () => {
    const analyze = { ...createDefaultVideoAnalyzeStepConfig(), source: { kind: 'StepResult', stepResultId: 'abc' } };
    expect(errorsUnder('VideoAnalyze', analyze, '/source')).toEqual([]);
    expect(errorsUnder('VideoAnalyze', { ...analyze, source: { kind: 'Bogus' } }, '/source')).not.toEqual([]);
  });
});
