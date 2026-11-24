/**
 * JSON Schema (draft-07) registry for workflow step configurations, keyed by StepType.
 * Each schema describes the config object passed to / returned from step editors and validators.
 * Schemas are automatically derived from the TypeScript interfaces in lib/types/workflow.ts.
 */

import type { StepType } from '@/lib/types/workflow';
import {
  OBJECT_MATCH_SCOPES,
  OBJECT_TRACK_METHODS,
  REGION_EFFECTS,
  ROOM_DECISION_MODES,
  VIDEO_DERUSH_PRE_PASS_MODES,
} from '@/lib/types/workflow';
import {
  MAX_POLL_INTERVAL_SECONDS,
  MAX_TIMEOUT_SECONDS,
  MIN_POLL_INTERVAL_SECONDS,
  MIN_TIMEOUT_SECONDS,
} from '@/lib/utils/video-generate-validation';

/** JSON Schema type (draft-07) for step configs. */
type JSONSchema7 = Record<string, unknown>;

/**
 * Builds the schema for a decision-engine enum that gains members in a later wave.
 *
 * The members this build knows are advertised as `examples` — a standard draft-07 annotation AJV
 * understands and does NOT validate against — rather than as a closed `enum`. That is deliberate
 * and load-bearing: `enum` would make `validateStepConfig` reject a config written by a NEWER
 * backend (wave 2's `DerushPrePass=Fast` / `RoomDecisionMode=Adaptive`, or anything after them),
 * which in the builder means a saved step whose "Apply" can never succeed — a dead end caused
 * entirely by the frontend lagging the server.
 *
 * Declaring the wave-2 members in a closed `enum` instead only tolerates the members anticipated
 * today, so it reproduces exactly the same failure one wave later.
 *
 * @param members - the shared member list (single source of truth, from `lib/types/workflow.ts`).
 *   `members[0]` is the schema `default` by convention: both lists lead with the member the backend
 *   record defaults to (`Off`).
 * @param description - what the field does; provenance and the member list are appended
 * @param caveat - appended to the description for any member that is DECLARED AHEAD of its
 *   behaviour. The description and `examples` are machine-readable text a consumer can act on,
 *   whereas a source comment is visible only to a human reading this file — so a member that is
 *   currently a placeholder has to be described as one HERE. State the member, what it currently
 *   does instead, and that it is not yet implemented.
 */
function openEnumSchema(
  members: readonly string[],
  description: string,
  caveat?: string,
): JSONSchema7 {
  return {
    type: 'string',
    default: members[0],
    description: `${description} Members: ${members.join(' | ')}. A member this build does not know is still accepted.${caveat ? ` ${caveat}` : ''}`,
    examples: [...members],
  };
}

/**
 * Shared schema definitions (components) referenced by multiple step config schemas.
 */
const DEFINITIONS: Record<string, JSONSchema7> = {
  ExtractInputRef: {
    type: 'object',
    properties: {
      from: {
        type: 'string',
        enum: ['Previous', 'Step', 'Accumulated', 'ProjectFiles'],
      },
      stepOrder: {
        type: ['integer', 'null'],
        minimum: 1,
      },
    },
    required: ['from'],
  },

  VideoSourceRef: {
    type: 'object',
    properties: {
      kind: {
        type: 'string',
        enum: ['ProjectFile', 'StepOutput', 'PreviousStepOutput', 'GeneratedClips', 'StepResult', 'BriefFiles'],
      },
      projectFileId: {
        type: ['string', 'null'],
      },
      stepResultId: {
        type: ['string', 'null'],
        description: 'Kind StepResult only: a step result of an earlier run of this project.',
      },
      stepOrder: {
        type: ['integer', 'null'],
        minimum: 1,
      },
      inSec: { type: ['number', 'null'], minimum: 0 },
      outSec: { type: ['number', 'null'], minimum: 0 },
    },
    required: ['kind'],
  },

  InsertContentRef: {
    type: ['object', 'null'],
    description: 'Insert content by reference: a project file, an earlier step of this run, or a step result of an earlier run. Exactly the one id field of its kind.',
    properties: {
      from: { type: 'string', enum: ['ProjectFile', 'Step', 'StepResult'] },
      projectFileId: { type: ['string', 'null'] },
      stepOrder: { type: ['integer', 'null'], minimum: 1 },
      stepResultId: { type: ['string', 'null'] },
    },
    required: ['from'],
  },

  ExtractExpectation: {
    type: 'object',
    properties: {
      requiredPaths: {
        type: ['array', 'null'],
        items: { type: 'string' },
      },
      minItems: {
        type: ['integer', 'null'],
        minimum: 0,
      },
      maxItems: {
        type: ['integer', 'null'],
        minimum: 0,
      },
      nonEmptyStringPaths: {
        type: ['array', 'null'],
        items: { type: 'string' },
      },
    },
  },

  VideoAnalyzeExpectation: {
    type: 'object',
    properties: {
      minShots: {
        type: ['integer', 'null'],
        minimum: 1,
      },
      minTranscriptSegments: {
        type: ['integer', 'null'],
        minimum: 1,
      },
      maxSilenceRatio: {
        type: ['number', 'null'],
        minimum: 0,
        maximum: 1,
      },
      minShotsWithVisuals: {
        type: ['integer', 'null'],
        minimum: 1,
      },
    },
  },

  VideoCompileExpectation: {
    type: 'object',
    properties: {
      minOutputSeconds: {
        type: ['number', 'null'],
        minimum: 0,
      },
      maxOutputSeconds: {
        type: ['number', 'null'],
        minimum: 0,
      },
      minRetainedRatio: {
        type: ['number', 'null'],
        minimum: 0,
        maximum: 1,
      },
      maxRetainedRatio: {
        type: ['number', 'null'],
        minimum: 0,
        maximum: 1,
      },
    },
  },

  VoiceoverLine: {
    type: 'object',
    properties: {
      text: { type: 'string' },
      startSec: { type: 'number', minimum: 0 },
      voiceId: { type: ['string', 'null'] },
    },
    required: ['text', 'startSec'],
  },

  VoiceoverSource: {
    type: 'object',
    properties: {
      kind: {
        type: 'string',
        // 'NarrationPlan' appended LAST for Wave 1 phase 2: it reads its lines from a
        // NarrationWriter step's output. The existing two values keep their positions.
        enum: ['Inline', 'ScriptScenes', 'NarrationPlan', 'PickupPlan'],
      },
      lines: {
        type: ['array', 'null'],
        items: { $ref: '#/definitions/VoiceoverLine' },
      },
      scriptwriterStepOrder: {
        type: ['integer', 'null'],
        minimum: 1,
      },
      // Appended for Wave 1 phase 2: the 1-based step order of the NarrationWriter step a
      // NarrationPlan source reads its lines from. Mirrors scriptwriterStepOrder.
      narrationWriterStepOrder: {
        type: ['integer', 'null'],
        minimum: 1,
      },
      // Appended for phase 4: the PickupPlanner step a PickupPlan source reads its pickups from.
      pickupPlannerStepOrder: {
        type: ['integer', 'null'],
        minimum: 1,
      },
    },
    required: ['kind'],
  },
};

/**
 * Extract step config schema.
 */
const EXTRACT_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    version: { type: 'integer', minimum: 1 },
    operation: {
      type: 'string',
      enum: ['Project', 'Resolve', 'Files'],
    },
    inputs: {
      type: 'object',
      additionalProperties: { $ref: '#/definitions/ExtractInputRef' },
    },
    path: { type: ['string', 'null'] },
    fields: {
      type: ['array', 'null'],
      items: { type: 'string' },
    },
    idField: { type: ['string', 'null'] },
    idPrefix: { type: 'string' },
    sortBy: { type: ['string', 'null'] },
    take: { type: ['integer', 'null'], minimum: 0 },
    skip: { type: 'integer', minimum: 0 },
    idsPath: { type: ['string', 'null'] },
    recordsPath: { type: ['string', 'null'] },
    onUnknownId: {
      type: 'string',
      enum: ['Fail', 'Skip'],
    },
    categories: {
      type: ['array', 'null'],
      items: { type: 'string' },
    },
    includeExtensions: {
      type: ['array', 'null'],
      items: { type: 'string' },
    },
    excludePathContains: {
      type: ['array', 'null'],
      items: { type: 'string' },
    },
    includeSummaries: { type: 'boolean' },
    includeContent: { type: 'boolean' },
    maxCharsPerFile: { type: 'integer', minimum: 0 },
    maxOutputChars: { type: 'integer', minimum: 0 },
    expect: { $ref: '#/definitions/ExtractExpectation' },
  },
  required: ['version', 'operation', 'inputs', 'idPrefix', 'skip', 'onUnknownId', 'includeSummaries', 'includeContent', 'maxCharsPerFile', 'maxOutputChars'],
  definitions: DEFINITIONS,
};

/**
 * VideoAnalyze step config schema.
 */
const VIDEO_ANALYZE_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    version: { type: 'integer', minimum: 1 },
    source: { $ref: '#/definitions/VideoSourceRef' },
    sources: {
      type: ['array', 'null'],
      items: { $ref: '#/definitions/VideoSourceRef' },
    },
    detectSilence: { type: 'boolean' },
    silenceThresholdDb: { type: 'number' },
    minSilenceMs: { type: 'integer', minimum: 0 },
    detectShots: { type: 'boolean' },
    sceneThreshold: { type: 'number', minimum: 0, maximum: 1 },
    transcription: {
      type: 'string',
      enum: ['Off', 'Optional', 'Required'],
    },
    transcriptionProviderId: { type: ['string', 'null'] },
    language: { type: ['string', 'null'] },
    wordTimestamps: { type: 'boolean' },
    maxAsrChunkBytes: { type: 'integer', minimum: 1 },
    // 0 = no limit of its own: the platform's limit applies.
    maxDurationSeconds: { type: 'integer', minimum: 0 },
    maxInputBytes: { type: 'integer', minimum: 0 },
    maxOutputChars: { type: 'integer', minimum: 0 },
    maxViewSegments: { type: 'integer', minimum: 1 },
    maxSegmentTextChars: { type: 'integer', minimum: 0 },
    analyzeVisuals: { type: 'boolean' },
    visualSampleFps: { type: 'number', minimum: 0.1 },
    visualGridWidth: { type: 'integer', minimum: 1 },
    visualGridHeight: { type: 'integer', minimum: 1 },
    maxVisualSampleFrames: { type: 'integer', minimum: 1 },
    stillMotionThreshold: { type: 'number', minimum: 0 },
    minStillWindowMs: { type: 'integer', minimum: 0 },
    maxStillWindowsPerShot: { type: 'integer', minimum: 1 },
    detectLetterbox: { type: 'boolean' },
    detectSharpness: { type: 'boolean' },
    analyzeAudioLevels: { type: 'boolean' },
    detectNearDuplicates: { type: 'boolean' },
    duplicateSimilarityThreshold: { type: 'number', minimum: 0, maximum: 1 },
    duplicateWindowShots: { type: 'integer', minimum: 1 },
    visualDetail: {
      type: 'string',
      enum: ['None', 'Compact', 'Full'],
    },
    maxViewDuplicateGroups: { type: 'integer', minimum: 1 },
    analyzeColorGrading: { type: 'boolean' },
    detectLookGroups: { type: 'boolean' },
    lookSimilarityThreshold: { type: 'number', minimum: 0, maximum: 1 },
    maxViewLookGroups: { type: 'integer', minimum: 1 },
    keyframesPerShot: { type: 'integer', minimum: 1, maximum: 3 },
    maxSharpnessShots: { type: 'integer', minimum: 1 },
    expect: { $ref: '#/definitions/VideoAnalyzeExpectation' },
    vision: {
      type: ['string', 'null'],
      enum: ['Off', 'Optional', 'Required'],
    },
    visionProviderId: { type: ['string', 'null'] },
    captionSelection: {
      type: ['string', 'null'],
      enum: ['PerDuplicateGroup', 'LongestShots', 'EvenlySpaced'],
    },
    maxCaptionedShots: { type: ['integer', 'null'], minimum: 1 },
    minCaptionShotSeconds: { type: ['number', 'null'], minimum: 0 },
    keyframeMaxWidth: { type: ['integer', 'null'], minimum: 1 },
    visionTimeoutSeconds: { type: ['integer', 'null'], minimum: 1 },
    maxCaptionChars: { type: ['integer', 'null'], minimum: 1 },
    persistKeyframes: { type: ['boolean', 'null'] },
    emitOverlayPlacements: { type: ['boolean', 'null'] },
    maxPlacementsPerShot: { type: ['integer', 'null'], minimum: 1 },
    maxPlacements: { type: ['integer', 'null'], minimum: 1 },
    maxTimeSlicesPerRegion: { type: ['integer', 'null'], minimum: 1 },
    offerMusicTracks: { type: ['boolean', 'null'] },
    maxMusicTracks: { type: ['integer', 'null'], minimum: 1 },
    offerSfxClips: { type: ['boolean', 'null'] },
    maxSfxClips: { type: ['integer', 'null'], minimum: 1 },
    detectInsertRegions: { type: ['boolean', 'null'] },
    insertRegionColor: { type: ['string', 'null'] },
    minInsertRegionSeconds: { type: ['number', 'null'], minimum: 0 },
    maxInsertRegions: { type: ['integer', 'null'], minimum: 1 },
    // -- Decision-engine phase 3 (decision-engine-p3) — per-item derush pre-pass + input
    //    screening. Appended after the existing properties, all defaulted and NOT in `required`,
    //    so a config persisted before these existed keeps validating unchanged. --
    derushPrePass: openEnumSchema(
      VIDEO_DERUSH_PRE_PASS_MODES,
      'How much of the per-shot review runs before the editor sees the footage.',
      '`Fast` is not available yet: it behaves exactly like `Prior` and does not skip the editor.',
    ),
    derushAcceptAt: {
      type: 'number',
      minimum: 0,
      maximum: 1,
      default: 0.85,
      description:
        'Confidence at or above which a single keep-or-drop answer counts as decided.',
    },
    derushMinMargin: {
      type: 'number',
      minimum: 0,
      maximum: 1,
      default: 0.25,
      description:
        'Minimum margin between the top two probabilities for a keep-or-drop answer to count as decided.',
    },
    screenInstructionLikeSpans: {
      type: 'boolean',
      default: false,
      description:
        'Quote and flag on-screen text or speech that reads like an instruction to the AI. Nothing is removed.',
    },
    minShotMs: { type: ['integer', 'null'], minimum: 0 },
    sourceIsComposition: { type: ['boolean', 'null'] },
    locateSubjects: { type: ['boolean', 'null'] },
    subjectLabel: { type: ['string', 'null'] },
    maxSubjectShots: { type: ['integer', 'null'], minimum: 0 },
    maxShotSec: { type: ['number', 'null'], minimum: 0 },
    // Cut points inside long shots (append-only on VideoAnalyzeStepConfig). Enum words as examples, not a
    // closed enum, for the same forward-compatibility reason as the decision-engine enums above.
    subShotMode: { type: ['string', 'null'], examples: ['Off', 'Even', 'Content'] },
    minSubShotSec: { type: ['number', 'null'], minimum: 0.5, maximum: 10 },
    maxSubShotSec: { type: ['number', 'null'], minimum: 1, maximum: 60 },
    maxSubShotsPerShot: { type: ['integer', 'null'], minimum: 2, maximum: 64 },
    analyzeMusicBeats: { type: ['boolean', 'null'] },
    musicBeatTrackProjectFileId: { type: ['string', 'null'] },
    maxBeatAnalyzedTracks: { type: ['integer', 'null'], minimum: 0, maximum: 20 },
    // -- Prompted region effects (plan §4): objects followed by a hosted segmentation model instead
    //    of the free planar tracker. Appended after maxBeatAnalyzedTracks, all optional, none
    //    required, so a config persisted before the feature existed still validates unchanged. --
    trackObjects: {
      type: ['array', 'null'],
      description:
        'Objects to find and follow. A label is what the AI looks for, or the prompt a segmentation model follows; a region pins the search to one area of one frame. Absent is exactly today\'s behaviour.',
      items: {
        type: 'object',
        properties: {
          label: {
            type: 'string',
            maxLength: 120,
            description:
              'What to look for (\'licence plate\') or, when method is Segmentation, the prompt. Capped at 120 characters and sanitized before it is sent; it never reaches a video command line.',
          },
          atSec: { type: ['number', 'null'], minimum: 0 },
          region: {
            type: ['object', 'null'],
            description: 'Corners in 0..1 of the source frame, TL/TR/BL/BR, like every insert quad. Used as the point/box prompt.',
            properties: {
              x0: { type: 'number', minimum: 0, maximum: 1 },
              y0: { type: 'number', minimum: 0, maximum: 1 },
              x1: { type: 'number', minimum: 0, maximum: 1 },
              y1: { type: 'number', minimum: 0, maximum: 1 },
              x2: { type: 'number', minimum: 0, maximum: 1 },
              y2: { type: 'number', minimum: 0, maximum: 1 },
              x3: { type: 'number', minimum: 0, maximum: 1 },
              y3: { type: 'number', minimum: 0, maximum: 1 },
            },
          },
          method: {
            type: 'string',
            enum: [...OBJECT_TRACK_METHODS],
            description:
              'How the object is followed. Feature (the default when absent) is the free tracker on this host. Segmentation sends the clip to a paid hosted model, and needs allowSourceMediaEgress.',
          },
          matchScope: {
            type: 'string',
            enum: [...OBJECT_MATCH_SCOPES],
            description:
              'For segmentation: every instance the prompt matches (the default when absent), or one object only. Ignored by the free tracker.',
          },
        },
      },
    },
    segmentationProviderId: {
      type: ['string', 'null'],
      description:
        'Which VideoSegmentation provider follows the object. Absent means that capability\'s default provider.',
    },
    allowSourceMediaEgress: {
      type: 'boolean',
      description:
        'Explicit consent for the clip\'s picture to leave this server for the segmentation provider. Without it a segmentation target fails closed with EGRESS_REFUSED and names the clip.',
    },
    maxSegmentationSeconds: {
      type: 'number',
      minimum: 1,
      maximum: 600,
      default: 60,
      description:
        'Longest source analysed per target, in seconds. A longer source fails with SEGMENTATION_SOURCE_TOO_LONG, which names the duration and this cap.',
    },
    segmentationDetectionThreshold: {
      type: 'number',
      minimum: 0.01,
      maximum: 1,
      default: 0.5,
      description: 'Model detection threshold, sent to the provider verbatim. Lower finds more objects, with more false ones.',
    },
    maxSegmentationObjects: {
      type: 'integer',
      minimum: 1,
      maximum: 16,
      default: 4,
      description: 'Most objects tracked at once, sent as max_num_objects.',
    },
    segmentationTimeoutSeconds: {
      type: 'integer',
      minimum: 60,
      maximum: 3600,
      default: 900,
      description: 'How long one provider call may take before it is abandoned, in seconds.',
    },
  },
  required: [
    'version',
    'detectSilence',
    'silenceThresholdDb',
    'minSilenceMs',
    'detectShots',
    'sceneThreshold',
    'transcription',
    'wordTimestamps',
    'maxAsrChunkBytes',
    'maxDurationSeconds',
    'maxInputBytes',
    'maxOutputChars',
    'maxViewSegments',
    'maxSegmentTextChars',
    'analyzeVisuals',
    'visualSampleFps',
    'visualGridWidth',
    'visualGridHeight',
    'maxVisualSampleFrames',
    'stillMotionThreshold',
    'minStillWindowMs',
    'maxStillWindowsPerShot',
    'detectLetterbox',
    'detectSharpness',
    'analyzeAudioLevels',
    'detectNearDuplicates',
    'duplicateSimilarityThreshold',
    'duplicateWindowShots',
    'visualDetail',
    'maxViewDuplicateGroups',
    'analyzeColorGrading',
    'detectLookGroups',
    'lookSimilarityThreshold',
    'maxViewLookGroups',
    'keyframesPerShot',
    'maxSharpnessShots',
  ],
  definitions: DEFINITIONS,
};

/** Capped and Average need a video bitrate; Quality (or none) forbids one. Mirrors the server's Validate(). */
const RATE_CONTROL_RULES: JSONSchema7[] = [
  {
    if: { properties: { rateControl: { enum: ['Capped', 'Average'] } }, required: ['rateControl'] },
    then: { properties: { videoBitrateKbps: { type: 'integer' } }, required: ['videoBitrateKbps'] },
  },
  {
    if: { properties: { rateControl: { enum: ['Quality'] } }, required: ['rateControl'] },
    then: { properties: { videoBitrateKbps: { type: 'null' } } },
  },
  // A bitrate with no rate control (absent or null) is refused too: it only applies to Capped or Average.
  {
    if: { properties: { videoBitrateKbps: { type: 'integer' } }, required: ['videoBitrateKbps'] },
    then: { properties: { rateControl: { enum: ['Capped', 'Average'] } }, required: ['rateControl'] },
  },
];

/** An audio-only render has no picture, so it refuses both rate fields. Mirrors the server's Validate(). */
const AUDIO_ONLY_RATE_RULE: JSONSchema7 = {
  if: { properties: { preset: { enum: ['AudioOnly'] } }, required: ['preset'] },
  then: {
    properties: {
      encode: {
        properties: {
          rateControl: { enum: ['Quality', null] },
          videoBitrateKbps: { type: 'null' },
        },
      },
    },
  },
};

/**
 * VideoCompile step config schema.
 */
const VIDEO_COMPILE_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    version: { type: 'integer', minimum: 1 },
    decision: { $ref: '#/definitions/ExtractInputRef' },
    analysisStepOrder: { type: 'integer', minimum: 1 },
    analysisStepResultId: { type: ['string', 'null'] },
    mode: {
      type: 'string',
      enum: ['Reencode', 'StreamCopy'],
    },
    prePaddingMs: { type: 'integer', minimum: 0 },
    postPaddingMs: { type: 'integer', minimum: 0 },
    minSegmentMs: { type: 'integer', minimum: 0 },
    maxSegments: { type: 'integer', minimum: 1 },
    allowKeyframeSnapping: { type: 'boolean' },
    outputFileName: { type: 'string' },
    videoCodec: { type: 'string' },
    audioCodec: { type: 'string' },
    crf: { type: 'integer', minimum: 0, maximum: 51 },
    rateControl: { type: ['string', 'null'], enum: ['Quality', 'Capped', 'Average', null], description: 'Optional. Quality keeps the plain CRF encode. Capped and Average need videoBitrateKbps.' },
    videoBitrateKbps: { type: ['integer', 'null'], minimum: 200, maximum: 100000, description: 'Video bitrate in kbps. Only with rateControl Capped or Average.' },
    preset: { type: 'string' },
    registerProjectFile: { type: 'boolean' },
    graphicsPlan: { $ref: '#/definitions/ExtractInputRef' },
    enableGraphics: { type: 'boolean' },
    maxOverlays: { type: 'integer', minimum: 0 },
    overlayShortMs: { type: 'integer', minimum: 0 },
    overlayMediumMs: { type: 'integer', minimum: 0 },
    overlayHoldMs: { type: 'integer', minimum: 0 },
    overlayFadeMs: { type: 'integer', minimum: 0 },
    overlayFontSizePct: { type: 'integer', minimum: 2, maximum: 12 },
    overlayFontColor: { type: 'string' },
    overlayBoxColor: { type: 'string' },
    maxOverlayTextChars: { type: 'integer', minimum: 0 },
    maxOverlaySubtextChars: { type: 'integer', minimum: 0 },
    programFadeInMs: { type: ['integer', 'null'], minimum: 0 },
    programFadeOutMs: { type: ['integer', 'null'], minimum: 0 },
    programAudioFadeInMs: { type: ['integer', 'null'], minimum: 0 },
    programAudioFadeOutMs: { type: ['integer', 'null'], minimum: 0 },
    programFadeColor: { type: ['string', 'null'] },
    transitionPolicy: {
      type: ['string', 'null'],
      enum: ['Off', 'AudioOnly', 'Auto', 'Expressive', 'Editor'],
    },
    audioSeamRampMs: { type: ['integer', 'null'], minimum: 0 },
    softCutMs: { type: ['integer', 'null'], minimum: 0 },
    dissolveMs: { type: ['integer', 'null'], minimum: 0 },
    dipToBlackMs: { type: ['integer', 'null'], minimum: 0 },
    dipCutMs: { type: ['integer', 'null'], minimum: 0 },
    maxTransitionMs: { type: ['integer', 'null'], minimum: 0 },
    maxTransitionRatioPct: { type: ['number', 'null'], minimum: 0 },
    maxTransitionSegments: { type: ['integer', 'null'], minimum: 1 },
    sectionBreakGapMs: { type: ['integer', 'null'], minimum: 0 },
    musicPlan: { $ref: '#/definitions/ExtractInputRef' },
    musicTrackProjectFileId: { type: ['string', 'null'] },
    enableMusic: { type: ['boolean', 'null'] },
    musicDucking: {
      type: ['string', 'null'],
      enum: ['Off', 'SpeechEnvelope'],
    },
    musicFitPolicy: {
      type: ['string', 'null'],
      enum: ['LoopToFit', 'PlayOnce'],
    },
    musicFadeInMs: { type: 'integer', minimum: 0 },
    musicFadeOutMs: { type: 'integer', minimum: 0 },
    enableInserts: { type: ['boolean', 'null'] },
    minInsertConfidence: { type: ['number', 'null'], minimum: 0, maximum: 1 },
    maxInserts: { type: ['integer', 'null'], minimum: 1 },
    insertOverscan: { type: ['number', 'null'], minimum: 0, maximum: 0.1 },
    insertSurface: { type: ['string', 'null'] },
    enableInsertMatte: { type: ['boolean', 'null'] },
    insertContentProjectFileId: { type: ['string', 'null'] },
    insertFit: { type: ['string', 'null'] },
    censorLabels: { type: ['array', 'null'], items: { type: 'string' } },
    censorStyle: { type: ['string', 'null'] },
    censorPadding: { type: ['number', 'null'], minimum: 0, maximum: 1 },
    censorHoldSec: { type: ['number', 'null'], minimum: 0, maximum: 3 },
    keepWholeSources: { type: ['boolean', 'null'] },
    colorGradePlan: { $ref: '#/definitions/ExtractInputRef' },
    enableColorGrade: { type: ['boolean', 'null'] },
    sfxPlan: { $ref: '#/definitions/ExtractInputRef' },
    enableSfx: { type: ['boolean', 'null'] },
    maxSfxCues: { type: ['integer', 'null'], minimum: 1 },
    maxSfxCueSeconds: { type: ['integer', 'null'], minimum: 0 },
    generatedClips: { $ref: '#/definitions/ExtractInputRef' },
    enableGeneratedClips: { type: ['boolean', 'null'] },
    maxGeneratedClips: { type: ['integer', 'null'], minimum: 1 },
    maxSeamBridgeMs: { type: ['integer', 'null'], minimum: 500 },
    canvasFill: { type: ['string', 'null'], enum: ['Black', 'Blur', null] },
    outputFormat: {
      type: ['string', 'null'],
      enum: ['Source', 'Vertical', 'Square', 'Landscape', 'Portrait', null],
    },
    outputFit: { type: ['string', 'null'], enum: ['Crop', 'Pad', 'Subject', null] },
    targetDurationSec: { type: ['integer', 'null'], minimum: 0 },
    expect: { $ref: '#/definitions/VideoCompileExpectation' },
    // Voiceover / captions / dialogue treatment / pickups (docs/voiceover.md). All optional: an
    // existing config without them must still validate, exactly like the generated-clips fields.
    enableVoiceover: { type: ['boolean', 'null'] },
    voiceoverStepOrder: { type: ['integer', 'null'], minimum: 1 },
    enableCaptions: { type: ['boolean', 'null'] },
    captionStyle: { type: ['string', 'null'], enum: ['Subtitle', 'Karaoke', 'Punch', 'Bold', null] },
    // 0 (and 4, the pre-automatic default) mean the automatic size; 2..12 is a % of frame height.
    captionFontSizePct: { type: ['integer', 'null'], minimum: 0, maximum: 12 },
    captionFontColor: { type: ['string', 'null'] },
    captionBoxColor: { type: ['string', 'null'] },
    captionHighlightColor: { type: ['string', 'null'] },
    captionMaxCharsPerCue: { type: ['integer', 'null'], minimum: 12, maximum: 120 },
    maxCaptionCues: { type: ['integer', 'null'], minimum: 1 },
    voiceoverMode: { type: ['string', 'null'], enum: ['OverMusic', 'DuckDialogue', 'ReplaceDialogue', null] },
    voiceoverDuckDb: { type: ['integer', 'null'], minimum: -40, maximum: 0 },
    voiceoverDuckRampMs: { type: ['integer', 'null'], minimum: 0, maximum: 1000 },
    enablePickups: { type: ['boolean', 'null'] },
    pickupVoiceoverStepOrder: { type: ['integer', 'null'], minimum: 1 },
    // Caption typography (docs/voiceover.md "Captions") — closed lists, never a font path.
    captionFont: {
      type: ['string', 'null'],
      enum: ['Auto', 'Inter', 'Montserrat', 'Poppins', 'BebasNeue', 'Anton', 'DejaVuSans', null],
    },
    captionPosition: { type: ['string', 'null'], enum: ['Auto', 'Bottom', 'Center', 'Top', null] },
    muteSourceAudio: { type: ['boolean', 'null'] },
    // Story order (docs/video-editing.md "Playing spans in the editor's order").
    keepOrder: { type: ['string', 'null'], enum: ['SourceOrder', 'AsListed', null] },
    // Several insert contents + insert-content dips (docs/video-editing.md "Inserting an existing video").
    insertContents: {
      type: ['array', 'null'],
      items: {
        type: 'object',
        properties: {
          projectFileId: { type: 'string' },
          sourceIndex: { type: ['integer', 'null'], minimum: 0 },
          regionId: { type: ['string', 'null'] },
          fit: { type: ['string', 'null'], enum: ['Auto', 'Stretch', 'Contain', 'Cover', null] },
          insertContentRef: { $ref: '#/definitions/InsertContentRef' },
        },
        required: ['projectFileId'],
      },
    },
    insertContentDips: { type: ['string', 'null'], enum: ['Freeze', 'Cut', 'Keep', null] },
    // Cutting to the beat (docs/video-editing.md "Cutting to the beat").
    beatSync: { type: ['string', 'null'], enum: ['Off', 'Beat', 'Bar', null] },
    loudnessTarget: { type: ['string', 'null'], enum: ['Off', 'Social', 'Web', 'Broadcast', null] },
    // Issue #93 phase 4 — the dailies room's answer, appended LAST in the C# record (after
    // LoudnessTarget): which step's TakeSelectionOutput picks one generated take per planned shot.
    takeSelection: { $ref: '#/definitions/ExtractInputRef' },
    // Sound from an inserted clip (docs/video-editing.md "Sound from an inserted clip"), appended
    // after TakeSelection in the C# record: the LAST two members of the positional record. Both are
    // optional so a config written before the feature still validates. MixInsertAudio=true needs
    // EnableInserts=true and an AudioCodec other than 'copy', or the step fails
    // (INSERT_AUDIO_REQUIRES_INSERTS / INSERT_AUDIO_REQUIRES_AUDIO_REENCODE); the gain outside
    // -24..12 fails (INSERT_AUDIO_GAIN_OUT_OF_RANGE).
    mixInsertAudio: {
      type: ['boolean', 'null'],
      description: 'Also mix the sound an inserted clip carries into the program while its picture is on screen, once, at its own length. Needs screen inserts on and an audio codec other than copy. An insert with no sound, such as a still image, stays silent without failing the run.',
    },
    insertAudioGainDb: {
      type: ['integer', 'null'],
      description: 'Volume for the inserted clip\'s own sound, in dB from -24 to 12. Ignored unless mixInsertAudio is on.',
      minimum: -24,
      maximum: 12,
    },
    // Keyframed reframing, per-source fits, subject safe area and insert content by reference
    // (docs/video-editing.md "Keyframed reframing"), appended LAST in the C# record. All optional;
    // the server's validator (REFRAME_* / SOURCE_FIT_* / SUBJECT_SAFE_AREA_* codes) is the authority
    // on keyframe ordering, overlap and the per-compile caps, so only the shape is stated here.
    reframes: {
      type: ['array', 'null'],
      description: 'Keyframed zoom/pan over stretches of source clips (needs mode Reencode). At most 64 reframes in all and 16 per source, each with up to 32 keyframes.',
      items: {
        type: 'object',
        properties: {
          sourceIndex: { type: 'integer', minimum: 0 },
          fromSec: { type: ['number', 'null'], minimum: 0 },
          toSec: { type: ['number', 'null'], minimum: 0 },
          keyframes: {
            type: 'array',
            maxItems: 32,
            items: {
              type: 'object',
              properties: {
                atSec: { type: 'number', minimum: 0 },
                zoom: { type: 'number', minimum: 1, maximum: 8 },
                centerX: { type: 'number', minimum: 0, maximum: 1 },
                centerY: { type: 'number', minimum: 0, maximum: 1 },
                easing: { type: 'string', enum: ['Linear', 'EaseIn', 'EaseOut', 'EaseInOut', 'Hold'] },
              },
              required: ['atSec'],
            },
          },
        },
        required: ['sourceIndex', 'keyframes'],
      },
    },
    sourceFits: {
      type: ['object', 'null'],
      description: 'Per-source override of outputFit, keyed by 0-based source index.',
      additionalProperties: { type: 'string', enum: ['Crop', 'Pad', 'Subject'] },
    },
    subjectSafeArea: {
      type: ['number', 'null'],
      minimum: 0,
      maximum: 0.25,
      description: 'outputFit Subject only: the frame-edge margin (fraction of the canvas) on-screen text is kept inside. Empty means 0.05; 0 switches the guard off.',
    },
    insertContentRef: { $ref: '#/definitions/InsertContentRef' },
    // Prompted region effects (plan §4/§5): apply one filter to just one tracked object. Appended
    // after DesignSystemFonts in the C# record, and last here. Optional, so a config saved before
    // the feature existed still validates; any entry requires mode = Reencode.
    regionEffects: {
      type: ['array', 'null'],
      description:
        'Filters applied to objects the Watch footage step tracked with segmentation, each for as long as that object is on screen. At most 8 entries; more is refused with EFFECT_LIMIT_EXCEEDED, never silently dropped. Needs mode = Reencode (EFFECT_REQUIRES_REENCODE).',
      maxItems: 8,
      items: {
        type: 'object',
        properties: {
          target: {
            type: 'string',
            maxLength: 120,
            description:
              'A segmentation track id such as g1, or the prompt it was tracked with. Matched case-insensitively and used only as a lookup key, never as a path.',
          },
          effect: {
            type: 'string',
            enum: [...REGION_EFFECTS],
            description:
              'Blur and Pixelate obscure the object; Highlight brightens it and dims the rest; Spotlight dims the rest and leaves it untouched; ColorPop desaturates the rest; Outline draws its silhouette.',
          },
          invert: {
            type: 'boolean',
            description: 'Apply the effect to everything except the object. Default false.',
          },
          strength: {
            type: 'number',
            minimum: 0,
            maximum: 2,
            description: 'Per-effect strength: blur radius, pixelate block size or brighten gain. Default 1.',
          },
          featherPx: {
            type: 'number',
            minimum: 0,
            maximum: 64,
            description: 'Edge softness in pixels at output resolution. 0 (default) is a crisp mask edge.',
          },
          opacity: {
            type: 'number',
            minimum: 0,
            maximum: 1,
            description: 'Mask alpha: 1 (default) applies the effect fully, 0 leaves the picture untouched.',
          },
        },
        required: ['target', 'effect'],
      },
    },
  },
  required: [
    'version',
    'decision',
    'analysisStepOrder',
    'mode',
    'prePaddingMs',
    'postPaddingMs',
    'minSegmentMs',
    'maxSegments',
    'allowKeyframeSnapping',
    'outputFileName',
    'videoCodec',
    'audioCodec',
    'crf',
    'preset',
    'registerProjectFile',
    'enableGraphics',
    'maxOverlays',
    'overlayShortMs',
    'overlayMediumMs',
    'overlayHoldMs',
    'overlayFadeMs',
    'overlayFontSizePct',
    'overlayFontColor',
    'overlayBoxColor',
    'maxOverlayTextChars',
    'maxOverlaySubtextChars',
    'musicFadeInMs',
    'musicFadeOutMs',
  ],
  definitions: DEFINITIONS,
  allOf: RATE_CONTROL_RULES,
};

/**
 * Voiceover step config schema.
 */
const VOICEOVER_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    source: { $ref: '#/definitions/VoiceoverSource' },
    providerId: { type: ['string', 'null'] },
    model: { type: ['string', 'null'] },
    defaultVoiceId: { type: ['string', 'null'] },
    maxBytes: { type: 'integer', minimum: 1 },
    // Phase 3/4 members (docs/voiceover.md), all optional so an existing config still validates.
    pronunciationDictionary: { type: ['string', 'null'], enum: ['Off', 'Auto', null] },
    maxPronunciationEntries: { type: ['integer', 'null'], minimum: 1, maximum: 64 },
    projectVoiceId: { type: ['string', 'null'] },
    language: { type: ['string', 'null'] },
    voiceCasting: { type: ['string', 'null'], enum: ['Off', 'Decision', null] },
    // Mirrors `VoiceoverStepConfig.PronunciationOverrides` (`[{term, ipa, caseSensitive}]`); declared so a saved list is kept.
    pronunciationOverrides: {
      type: ['array', 'null'],
      items: {
        type: 'object',
        properties: {
          term: { type: 'string' },
          ipa: { type: 'string' },
          caseSensitive: { type: 'boolean' },
        },
        required: ['term', 'ipa'],
      },
    },
  },
  required: ['source'],
  additionalProperties: true,
  definitions: DEFINITIONS,
};

/**
 * VideoGenerate step config schema. Mirrors `VideoGenerateStepConfig.cs` (ReelBolt.Shared/Workflows/)
 * 1:1. Bounds are only those the backend enforces: `Validate()` (takes 1-64, maxClips 1-64 with C#
 * default 1, takes <= maxClips — each take is one paid clip and maxClips is the author's hard spend
 * ceiling — maxSpendCredits > 0, and planSource StepRef requiring a `plan` reference) and
 * `VideoGenerateStepExecutor` (inlinePrompt <= 7000 chars). `plan` and `analysis` are step references
 * sharing the `ExtractInputRef` definition, exactly as the C# properties reuse that type.
 * The cross-field takes <= maxClips rule is not expressible in plain draft-07 properties, so it is
 * documented on `maxClips` and enforced by the VideoGenerate editor rather than by this schema. `planSource` is serialized as a string
 * (JsonStringEnumConverter), like VideoAnalyze's `transcription`.
 */
const VIDEO_GENERATE_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    planSource: {
      type: 'string',
      enum: ['Inline', 'StepRef', 'ProjectFile'],
    },
    inlinePrompt: { type: ['string', 'null'], maxLength: 7000 },
    plan: { $ref: '#/definitions/ExtractInputRef' },
    planProjectFileId: { type: ['string', 'null'] },
    analysis: { $ref: '#/definitions/ExtractInputRef' },
    model: { type: 'string' },
    resolution: { type: 'string' },
    durationSeconds: { type: 'integer' },
    ratio: { type: 'string' },
    providerId: { type: ['string', 'null'] },
    takes: { type: 'integer', minimum: 1, maximum: 64 },
    maxClips: {
      type: 'integer',
      minimum: 1,
      maximum: 64,
      default: 1,
      description: 'Hard cap on paid clips (1-64). takes must be <= maxClips; each take is one paid clip.',
    },
    maxSpendCredits: {
      type: 'number',
      exclusiveMinimum: 0,
      description: "Step spend cap in platform credits (the backend's Validate() requires > 0).",
    },
    allowSourceMediaEgress: { type: 'boolean' },
    pollIntervalSeconds: {
      type: 'integer',
      minimum: MIN_POLL_INTERVAL_SECONDS,
      maximum: MAX_POLL_INTERVAL_SECONDS,
    },
    timeoutSeconds: {
      type: 'integer',
      minimum: MIN_TIMEOUT_SECONDS,
      maximum: MAX_TIMEOUT_SECONDS,
      description: 'Must be >= pollIntervalSeconds (the step would otherwise time out before its first poll).',
    },
    loopBackBehavior: {
      type: 'string',
      enum: ['Reuse', 'Regenerate'],
      description: 'On a loop-back iteration: reuse already-generated clips (no cost) or regenerate them (costs money).',
    },
    // -- issue #93 phases 4-5, appended in C# order --
    lookMatching: { type: 'boolean', default: false },
    cameraContinuity: { type: 'boolean', default: false },
    maxGpuMinutes: {
      type: 'number',
      minimum: 0,
      default: 0,
      description: 'GPU-minute cap for a self-hosted ComfyUI provider; a priced vendor uses maxSpendCredits instead. The backend requires the one that fits the resolved provider kind.',
    },
    conformFps: { type: 'integer', minimum: 0, maximum: 120, default: 0 },
    conformHeight: { type: 'integer', minimum: 0, maximum: 4320, default: 0 },
  },
  // maxSpendCredits stays required for the schema's purposes: the C# Validate() accepts a GPU-only
  // budget (maxSpendCredits 0 with maxGpuMinutes > 0), but the editor keeps maxSpendCredits present and
  // relaxes its own > 0 check when a GPU budget is set.
  required: ['maxSpendCredits'],
};

/**
 * ThumbnailGenerate step config schema. Mirrors `ThumbnailGenerateStepConfig.cs`
 * (ReelBolt.Shared/Workflows/) 1:1 — a COMPLETE mirror, so unlike the room/conditional schemas this
 * one carries no `additionalProperties: true` and may be asserted against by exact key-set equality
 * (the same standing VIDEO_GENERATE_SCHEMA has).
 *
 * Every bound below is one the backend itself enforces, either in `ThumbnailGenerateStepConfig.Validate`
 * (the save-path check) or by the executor's clamp — a bound the engine does not enforce would reject
 * a config it accepts. The one cross-field rule draft-07 cannot state — `analysisStepOrder` may be 0
 * only when `analysisStepResultId` names a prior run's analysis — is documented on the property, the
 * same way VIDEO_GENERATE_SCHEMA documents takes <= maxClips, and enforced by the save validator.
 */
const THUMBNAIL_GENERATE_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    // No `default` on either required member, exactly like the Extract/VideoAnalyze/VideoCompile
    // schemas: they are the record's required positional parameters, which carry no default value.
    version: {
      type: 'integer',
      minimum: 1,
      description: 'Config schema version. 1 is the only version this build writes.',
    },
    analysisStepOrder: {
      type: 'integer',
      minimum: 1,
      description:
        'The step number of the Watch footage (VideoAnalyze) step whose clips the frame is chosen from. Required: the frame is picked from that step\'s measurements. May be 0 only when analysisStepResultId names a prior run\'s analysis.',
    },
    analysisStepResultId: {
      type: ['string', 'null'],
      default: null,
      description: 'Reuse a previous run\'s footage analysis instead of this run\'s. Blank uses this run\'s analysis.',
    },
    selection: {
      type: 'string',
      enum: ['Auto', 'AtTime'],
      default: 'Auto',
      description: 'Auto (default) picks the moment from the footage analysis: the best take, the sharpest and calmest frame, ignoring repeated attempts. AtTime takes one exact moment you give, which is the only way to use a source the Watch footage step never looked at.',
    },
    atTimeSec: {
      type: 'number',
      minimum: 0,
      default: 0,
      description: 'The moment to take, in seconds. Only used when selection is AtTime.',
    },
    sourceIndex: {
      type: 'integer',
      minimum: 0,
      default: 0,
      description: 'Which analysed clip to read, counting from 0. Only used when selection is AtTime; Auto picks the clip as part of choosing the shot.',
    },
    width: {
      type: 'integer',
      minimum: 16,
      maximum: 4096,
      default: 1280,
      description: 'Image width in pixels.',
    },
    height: {
      type: 'integer',
      minimum: 16,
      maximum: 4096,
      default: 720,
      description: 'Image height in pixels.',
    },
    fit: {
      type: 'string',
      enum: ['Cover', 'Contain'],
      default: 'Cover',
      description: 'Cover (default) fills the image and crops the overflow. Contain fits the whole frame inside, with bars on the spare side.',
    },
    jpegQuality: {
      type: 'integer',
      minimum: 2,
      maximum: 31,
      default: 4,
      description: 'How compressed the image is. 2 is the best quality and 31 the smallest file.',
    },
    outputFileName: {
      type: 'string',
      default: 'thumbnail.jpg',
      description: 'The name the image is saved under. A bare file name, not a path.',
    },
    registerProjectFile: {
      type: 'boolean',
      default: true,
      description: 'Also list the image in the project\'s Files so it can be downloaded or set as the video\'s cover.',
    },
    maxCandidates: {
      type: 'integer',
      minimum: 1,
      maximum: 500,
      default: 24,
      description: 'How many moments are scored. A higher number can find a better frame and takes a little longer.',
    },
    minShotSeconds: {
      type: 'number',
      minimum: 0,
      default: 0.4,
      description: 'Moments shorter than this are ignored, because a very short shot is usually a flash frame.',
    },
    preferBestTake: {
      type: 'boolean',
      default: true,
      description: 'Prefer the best attempt when the same moment was filmed more than once. Off picks purely on score.',
    },
    overlayText: {
      type: ['string', 'null'],
      default: null,
      description: 'Text burned into the image, or blank for none. It is written into the picture, so it cannot be edited afterwards.',
    },
    overlayPosition: {
      type: 'string',
      enum: ['None', 'LowerThird', 'UpperThird', 'Center'],
      default: 'None',
      description: 'Where the text sits. None (default) draws no text at all.',
    },
    overlayFontSizePct: {
      type: 'integer',
      minimum: 2,
      maximum: 25,
      default: 9,
      description: 'Text size as a percentage of the image height.',
    },
    overlayFontColor: {
      type: 'string',
      default: 'white',
      description: 'Text colour, from a fixed list — it reaches the renderer directly.',
    },
    overlayBoxColor: {
      type: 'string',
      default: 'black@0.45',
      description: 'The panel drawn behind the text, or "none" for no panel. From a fixed list.',
    },
    maxOverlayChars: {
      type: 'integer',
      minimum: 1,
      maximum: 400,
      default: 90,
      description: 'Longest text accepted; anything longer is cut.',
    },
  },
  required: ['version', 'analysisStepOrder'],
};

/**
 * Room step config schema (shared by EditRoom, GraphicsRoom, ColorGradeRoom, DailiesRoom).
 * Preserves unknown fields; validates only known schema-declared keys.
 *
 * PARTIAL mirror of the three room records on purpose — see the note above CONDITIONAL_SCHEMA.
 * The `convene*` fields below are the decision-engine phase-2 convening gate, whose defaults are
 * cross-checked against `createDefaultRoomConveneConfig()` in RoomStepConfigEditor.tsx.
 */
const ROOM_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    version: { type: 'integer', minimum: 1 },
    view: { $ref: '#/definitions/ExtractInputRef' },
    rounds: { type: 'integer', minimum: 1, maximum: 6 },
    maxTurns: { type: 'integer', minimum: 1 },
    persistTranscript: { type: 'boolean' },
    fallbackToSoloEditor: { type: 'boolean' },
    fallbackToSoloPlanner: { type: 'boolean' },
    fallbackToSoloColorist: { type: 'boolean' },
    fallbackToSoloSelector: { type: 'boolean' },
    // -- phase 2: the convening gate (`IRoomStepConfig` + all three room records) --
    conveneGate: {
      type: 'string',
      enum: ['Off', 'Shadow', 'Gate'],
      default: 'Off',
      description: 'Off by default. Only the colour team acts on it; the editing and graphics teams accept the setting but ignore it.',
    },
    conveneAcceptAt: {
      type: 'number',
      default: 0.85,
      description: 'How confident the decision model must be before the team is skipped. Only read when the gate is not Off.',
    },
    conveneMinMargin: {
      type: 'number',
      default: 0.25,
      description: 'How far apart the top two answers must be before the team is skipped. Only read when the gate is not Off.',
    },
    conveneProviderId: {
      type: ['string', 'null'],
      default: null,
      description: 'Which decision model answers. Blank uses the default one. Only read when the gate is not Off.',
    },
    // decision-engine-p3: declared once here because all three room configs expose it through the
    // backend's shared IRoomStepConfig surface. Optional/defaulted, so a room config persisted
    // before this field existed still validates unchanged.
    decisionScheduling: openEnumSchema(
      ROOM_DECISION_MODES,
      'Whether the team asks decision-model questions about convergence and the next speaker, and whether it acts on the answers.',
      '`Adaptive` is not available yet: it behaves exactly like `Questions` and nothing acts on the answers.',
    ),
  },
  required: ['version'],
  additionalProperties: true,
  definitions: DEFINITIONS,
};

/**
 * ReviewLoop step config schema.
 *
 * PARTIAL mirror on purpose, and it MIXES two sources: `minScore`/`maxIterations`/
 * `loopTargetStepOrder` are `WorkflowStep` COLUMNS, while `enabled`/`acceptAt`/`bimodalMargin`/
 * `providerId`/`timeoutSeconds` are phase-2 `ReviewLoopStepConfig` record members. It carries
 * `additionalProperties: true`, so an exact key-set assertion against the C# record would be wrong;
 * the phase-2 defaults are cross-checked against `createDefaultReviewLoopStepConfig()` in
 * ReviewLoopStepConfig.tsx instead.
 */
const REVIEW_LOOP_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    minScore: { type: ['integer', 'null'], minimum: 1, maximum: 10 },
    maxIterations: { type: 'integer', minimum: 1, maximum: 10 },
    loopTargetStepOrder: { type: ['integer', 'null'], minimum: 1 },
    // -- phase 2: the calibrated early stop (`ReviewLoopStepConfig.cs`, D-008) --
    enabled: {
      type: 'boolean',
      default: false,
      description: 'Whether the early stop runs at all. Off by default, in which case the reviewer always runs.',
    },
    acceptAt: {
      type: 'number',
      default: 0.85,
      description: 'How confident the decision model must be that the score passes before the review is skipped. A starting point, not a tuned value.',
    },
    bimodalMargin: {
      type: 'number',
      default: 0.15,
      description: 'Minimum gap between the top two score levels; a narrower gap sends the work to the reviewer even when the confidence above is high.',
    },
    providerId: {
      type: ['string', 'null'],
      default: null,
      description: 'Which decision model answers. Blank uses the default one, and with no decision model the reviewer runs.',
    },
    timeoutSeconds: {
      type: 'integer',
      default: 5,
      description: 'How long to wait for the decision model. On timeout the reviewer runs.',
    },
    // -- the frame-sampling visual check (`VisualCheckConfig.cs`, T-102), appended LAST --
    visualCheck: {
      type: ['object', 'null'],
      description: 'Samples frames of the target step\'s video before each review and flags text outside the safe margin or too small to read. Off unless enabled is true.',
      properties: {
        enabled: { type: 'boolean', default: false },
        frames: { type: 'integer', minimum: 2, maximum: 16, default: 8 },
        safeMarginPercent: { type: 'number', minimum: 0, maximum: 20, default: 5 },
        minTextHeightPercent: { type: 'number', minimum: 0.5, maximum: 20, default: 2.5 },
        failScoreCap: { type: 'integer', minimum: 0, maximum: 10, default: 6 },
        visionProviderId: { type: ['string', 'null'], default: null },
      },
    },
  },
  additionalProperties: true,
};

/**
 * Generic/empty schema for step types with no special config (Parallel).
 */
const GENERIC_SCHEMA: JSONSchema7 = {
  type: 'object',
  additionalProperties: true,
};

/**
 * Agent step config schema — defines caching and custom input mapping overrides.
 */
const AGENT_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    cacheMode: {
      type: ['string', 'null'],
      enum: ['Default', 'Always', 'Never', null],
    },
    inputMappingJson: {
      type: ['string', 'null'],
    },
  },
  additionalProperties: true,
};

/**
 * Conditional and ForEach step config schemas.
 *
 * PARTIAL mirror on purpose. `conditionExpression` is a `WorkflowStep` COLUMN, not a
 * `ConditionalStepConfig` member; the five `mode`/`decisionQuestion`/`threshold`/`providerId`/
 * `timeoutSeconds` properties below ARE phase-2 `ConditionalStepConfig` members (D-007). All three
 * of this file's partial schemas carry `additionalProperties: true`, so none of them may be asserted
 * against with exact key-set equality — that assertion belongs only to `VIDEO_GENERATE_SCHEMA`,
 * which is a complete 1:1 mirror. The phase-2 defaults here are cross-checked against
 * `createDefaultConditionalStepConfig()` in ConditionalStepConfig.tsx.
 *
 * Bounds are deliberately omitted for the phase-2 fields: the backend enforces none (it compares
 * `p_yes` against `threshold`, and a non-positive timeout is simply an immediately-failing gate), so
 * a bound here would reject a config the engine accepts.
 */
const CONDITIONAL_SCHEMA: JSONSchema7 = { type: 'object', properties: {
  conditionExpression: { type: ['string', 'null'] },
  // -- phase 2: the decision arm (`ConditionalStepConfig.cs`, D-007) --
  mode: {
    type: 'string',
    enum: ['Expression', 'Decision'],
    default: 'Expression',
    description: 'Expression (default) evaluates the condition formula locally, so a step with no config is an Expression step. Decision asks a decision model a yes/no question first, and with no decision model configured it takes the False branch.',
  },
  decisionQuestion: {
    type: ['string', 'null'],
    default: null,
    description: 'The question\'s name in Decision mode. Blank derives one from the condition. It is the name the calibration page groups by.',
  },
  threshold: {
    type: 'number',
    default: 0.5,
    description: 'Yes-probability the step compares against when conditionExpression is blank in Decision mode. Ignored in Expression mode.',
  },
  providerId: {
    type: ['string', 'null'],
    default: null,
    description: 'Which decision model answers. Blank uses the default one.',
  },
  timeoutSeconds: {
    type: 'integer',
    default: 5,
    description: 'How long to wait for the decision model. On timeout the step evaluates false, so a slow provider cannot block or fail it.',
  },
}, additionalProperties: true };
const FOREACH_SCHEMA: JSONSchema7 = { type: 'object', properties: {
  loopSourceExpression: { type: ['string', 'null'] },
  maxIterations: { type: 'integer', minimum: 1, maximum: 50 } }, additionalProperties: true };

const TIMELINE_RENDER_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    editTimelineId: {
      type: 'string',
      description: 'The editor timeline to render. Required.',
    },
    version: {
      type: 'integer',
      minimum: 1,
      description: 'Config schema version. 1 is the only version this build writes.',
    },
    revision: {
      type: ['integer', 'null'],
      minimum: 1,
      default: null,
      description: 'The revision to render. Blank renders the latest when the step starts; the run records the revision it used.',
    },
    range: {
      type: ['object', 'null'],
      default: null,
      description: 'The part of the timeline to render, in frames [inFrame, outFrame). Blank renders all of it.',
      properties: {
        inFrame: { type: 'integer', minimum: 0 },
        outFrame: { type: 'integer', minimum: 1 },
      },
      required: ['inFrame', 'outFrame'],
    },
    preset: {
      type: 'string',
      enum: ['Hd1080p16x9', 'Uhd4k16x9', 'Vertical9x16', 'Square1x1', 'Portrait4x5', 'Master', 'AudioOnly'],
      default: 'Hd1080p16x9',
      description: 'The delivery format. Master keeps the timeline\'s own size at the highest quality; AudioOnly writes the mixed audio alone.',
    },
    encode: {
      type: ['object', 'null'],
      default: null,
      description: 'Encoder settings that refine the preset. Blank keeps the preset\'s own.',
      properties: {
        width: { type: ['integer', 'null'], minimum: 16, maximum: 7680 },
        height: { type: ['integer', 'null'], minimum: 16, maximum: 4320 },
        codec: { type: 'string', enum: ['H264', 'H265'], default: 'H264' },
        crf: { type: 'integer', minimum: 0, maximum: 51, default: 18 },
        speed: { type: 'string', enum: ['Fast', 'Medium', 'Slow'], default: 'Medium' },
        audioBitrateKbps: { type: 'integer', minimum: 64, maximum: 320, default: 192 },
        rateControl: { type: 'string', enum: ['Quality', 'Capped', 'Average'], default: 'Quality', description: 'Quality keeps the plain quality encode. Capped adds a top bitrate; Average aims for a steady bitrate. Capped and Average need videoBitrateKbps.' },
        videoBitrateKbps: { type: ['integer', 'null'], minimum: 200, maximum: 100000, description: 'Video bitrate in kbps. Only with rateControl Capped or Average.' },
      },
      allOf: RATE_CONTROL_RULES,
    },
    outputFileName: {
      type: 'string',
      maxLength: 120,
      pattern: '^[A-Za-z0-9][A-Za-z0-9._ -]*$',
      default: 'timeline-render.mp4',
      description: 'The file name the render is saved under. A bare name, never a path.',
    },
    registerProjectFile: {
      type: 'boolean',
      default: true,
      description: 'Also list the render in the project\'s Files.',
    },
    loudness: {
      type: ['object', 'null'],
      default: null,
      description: 'Two-pass loudness normalisation targets. Blank measures the mix without changing it.',
      properties: {
        targetLufs: { type: 'number', minimum: -36, maximum: -5, default: -16 },
        truePeakDbtp: { type: 'number', minimum: -9, maximum: 0, default: -1.5 },
        loudnessRange: { type: 'number', minimum: 1, maximum: 30, default: 11 },
      },
    },
  },
  required: ['editTimelineId', 'version'],
  allOf: [AUDIO_ONLY_RATE_RULE],
  additionalProperties: false,
};

/**
 * Brand and product profile (DesignSystem) config. Bounds mirror the server's `Validate()` on
 * `DesignSystemStepConfig` so the form can never submit a value the save would reject on range.
 */
const DESIGN_SYSTEM_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    op: {
      type: 'string',
      enum: ['Parse', 'Compose', 'Require'],
      default: 'Parse',
      description: 'What this step does: read the design files, combine them into the profile, or require an approved one.',
    },
    version: {
      type: 'integer',
      minimum: 1,
      description: 'Config schema version. 1 is the only version this build writes.',
    },
    parse: {
      type: ['object', 'null'],
      default: null,
      description: 'Read options, present only when op is Parse.',
      properties: {
        includeDesign: { type: 'boolean', default: true, description: 'Read the brand (colours, fonts, spacing).' },
        includeProductMap: { type: 'boolean', default: true, description: 'Read the product structure (pages, parts, libraries).' },
        maxFiles: { type: 'integer', minimum: 1, maximum: 5000, default: 3000 },
        maxBytesPerFile: { type: 'integer', minimum: 1024, maximum: 1048576, default: 262144 },
        maxTotalBytes: { type: 'integer', minimum: 1048576, maximum: 67108864, default: 25165824 },
        extraGlobs: {
          type: ['array', 'null'],
          items: { type: 'string', maxLength: 100 },
          maxItems: 20,
          default: null,
          description: 'Extra file patterns to read, in addition to the built-in defaults.',
        },
      },
    },
    compose: {
      type: ['object', 'null'],
      default: null,
      description: 'Combine options, present only when op is Compose.',
      properties: {
        parseStepOrder: { type: 'integer', minimum: 1, description: 'The earlier step that read the files.' },
        designFallbackStepOrders: {
          type: ['array', 'null'],
          items: { type: 'integer', minimum: 1 },
          default: null,
          description: 'Steps whose result can fill a missing brand profile.',
        },
        productFallbackStepOrders: {
          type: ['array', 'null'],
          items: { type: 'integer', minimum: 1 },
          default: null,
          description: 'Steps whose result can fill a missing product profile.',
        },
        ingest: { type: 'boolean', default: true, description: 'Save the profile for this project.' },
      },
      required: ['parseStepOrder'],
    },
    require: {
      type: ['object', 'null'],
      default: null,
      description: 'Require options, present only when op is Require.',
      properties: {
        requireDesign: { type: 'boolean', default: true },
        requireProductMap: { type: 'boolean', default: true },
      },
    },
  },
  required: ['op', 'version'],
  additionalProperties: false,
};

/**
 * ExtractFrame step config schema. Mirrors `ExtractFrameStepConfig.cs` (T-105) 1:1, so like
 * THUMBNAIL_GENERATE_SCHEMA it is a COMPLETE mirror with no `additionalProperties: true`.
 * The cross-field rules (`atSec` only with `at` Time, a source that names exactly its own id) are enforced by the
 * server's `Validate()` and the save validator, and described on the properties.
 */
const EXTRACT_FRAME_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    source: {
      $ref: '#/definitions/VideoSourceRef',
      description: 'The one video the frame is taken from. GeneratedClips (many videos) is refused.',
    },
    at: {
      type: 'string',
      enum: ['Time', 'Last', 'First'],
      default: 'Last',
      description: 'Which frame: the first, the last (default), or the moment given in atSec.',
    },
    atSec: {
      type: ['number', 'null'],
      minimum: 0,
      default: null,
      description: 'Seconds from the start of the video. Required with at Time, and refused with any other value of at.',
    },
    format: { type: 'string', enum: ['png', 'jpeg'], default: 'png' },
    maxLongSide: {
      type: ['integer', 'null'],
      minimum: 16,
      maximum: 8192,
      default: null,
      description: 'Longest side in pixels. Empty keeps the video\'s own size.',
    },
    crop: {
      type: ['object', 'null'],
      default: null,
      properties: {
        x: { type: 'number', minimum: 0, maximum: 1 },
        y: { type: 'number', minimum: 0, maximum: 1 },
        w: { type: 'number', minimum: 0.01, maximum: 1 },
        h: { type: 'number', minimum: 0.01, maximum: 1 },
      },
      required: ['x', 'y', 'w', 'h'],
    },
    registerProjectFile: { type: 'boolean', default: true },
  },
  required: ['source'],
  additionalProperties: false,
  definitions: { VideoSourceRef: DEFINITIONS.VideoSourceRef },
};

/**
 * AudioCleanup step config schema. Mirrors `AudioCleanupStepConfig.cs` 1:1 (a COMPLETE mirror). The cross-field rules
 * (`providerId` only with a hosted target, a source that names exactly its own id) are enforced by the server's
 * `Validate()` and the save validator, and described on the properties.
 */
export const AUDIO_CLEANUP_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    target: {
      type: 'string',
      enum: ['VoiceBasic', 'Hum', 'Wind', 'Hiss', 'Echo', 'IsolateVoice', 'RemoveMusic'],
      description: 'What to remove. VoiceBasic, Hum, Wind, Hiss and Echo run free on this server; IsolateVoice and RemoveMusic are hosted AI, metered per audio minute.',
    },
    source: {
      $ref: '#/definitions/VideoSourceRef',
      description: 'The one audio file or video to clean. GeneratedClips and BriefFiles are refused.',
    },
    providerId: { type: ['string', 'null'], default: null, description: 'AudioIsolation provider id for a hosted target; blank uses the default. Refused with a free target.' },
    allowSourceMediaEgress: {
      type: 'boolean',
      default: false,
      description: 'Consent for the source audio to be uploaded to a hosted model. Only the customer gives it, in the app.',
    },
    registerProjectFile: { type: 'boolean', default: true },
    outputKind: {
      type: 'string',
      enum: ['Auto', 'Audio', 'VideoWithCleanAudio'],
      default: 'Auto',
      description: 'Auto: video in, video out with the picture copied; audio in, WAV out.',
    },
    maxSourceMinutes: {
      type: ['integer', 'null'],
      minimum: 1,
      maximum: 1440,
      default: null,
      description: 'Longest source for a hosted target in minutes; can only lower the deployment cap.',
    },
  },
  required: ['target', 'source'],
  additionalProperties: false,
  definitions: { VideoSourceRef: DEFINITIONS.VideoSourceRef },
};

/**
 * VideoEnhance step config schema. Mirrors `VideoEnhanceStepConfig.cs` 1:1 (a COMPLETE mirror). The cross-field rules (one of
 * `upscaleFactor` / `targetHeight`, hosted-only fields, the per-op fields) are enforced by the server's `Validate()` and the
 * save validator, and described on the properties.
 */
export const VIDEO_ENHANCE_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    op: { type: 'string', enum: ['Upscale', 'Interpolate', 'Denoise', 'MotionBlur', 'Extend'], description: 'What to do. Denoise and MotionBlur are local only; Extend is hosted only.' },
    engine: {
      type: 'string',
      enum: ['Local', 'Hosted'],
      description: 'Local runs free on this server; Hosted is metered AI (upscale, interpolate and extend) and needs the customer consent.',
    },
    source: { $ref: '#/definitions/VideoSourceRef', description: 'The one video to enhance. GeneratedClips and BriefFiles are refused.' },
    providerId: { type: ['string', 'null'], default: null, description: 'Hosted provider id; blank uses the default. Refused with a local operation.' },
    allowSourceMediaEgress: {
      type: 'boolean',
      default: false,
      description: 'Consent for the source video file to be uploaded to a hosted model. Only the customer gives it, in the app.',
    },
    registerProjectFile: { type: 'boolean', default: true },
    maxSourceSeconds: {
      type: ['integer', 'null'],
      minimum: 1,
      maximum: 600,
      default: null,
      description: 'Longest source in seconds; can only lower the deployment cap (120 hosted, 600 local).',
    },
    upscaleFactor: { type: ['integer', 'null'], enum: [2, 4, null], default: null, description: 'Upscale multiplier. Set exactly one of upscaleFactor and targetHeight.' },
    targetHeight: { type: ['integer', 'null'], enum: [720, 1080, 1440, 2160, null], default: null, description: 'Upscale target height. Set exactly one of upscaleFactor and targetHeight.' },
    frameMultiplier: { type: ['integer', 'null'], enum: [2, 4, 8, null], default: null, description: 'Interpolate multiplier (hosted: 2 or 4); the output stays at or below 240 fps.' },
    denoiseStrength: { type: ['string', 'null'], enum: ['Light', 'Strong', null], default: null, description: 'Denoise strength (Denoise only).' },
    keepGrain: { type: 'boolean', default: false, description: 'Keep a light grain after denoising (Denoise only).' },
    shutterAngle: { type: ['integer', 'null'], minimum: 90, maximum: 360, default: null, description: 'Motion-blur shutter angle in degrees (MotionBlur only); blank means 180.' },
    sourceWidth: { type: ['integer', 'null'], minimum: 1, maximum: 16384, default: null, description: 'Hosted only: the source width the route measured. Set by the server with sourceHeight and sourceFps, all three or none.' },
    sourceHeight: { type: ['integer', 'null'], minimum: 1, maximum: 16384, default: null, description: 'Hosted only: the source height the route measured. Set by the server with sourceWidth and sourceFps, all three or none.' },
    sourceFps: { type: ['number', 'null'], exclusiveMinimum: 0, maximum: 240, default: null, description: 'Hosted only: the source frame rate the route measured. Set by the server with sourceWidth and sourceHeight, all three or none.' },
    targetAspect: { type: ['string', 'null'], enum: ['16:9', '9:16', '1:1', '4:5', null], default: null, description: 'Extend only: the shape to extend the picture to. Set this OR sides + one amount (the four margins), never both.' },
    marginLeft: { type: ['number', 'null'], minimum: 0, maximum: 0.5, default: null, description: 'Extend only: fraction of the width to add on the left, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0. Not with targetAspect.' },
    marginRight: { type: ['number', 'null'], minimum: 0, maximum: 0.5, default: null, description: 'Extend only: fraction of the width to add on the right, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0. Not with targetAspect.' },
    marginTop: { type: ['number', 'null'], minimum: 0, maximum: 0.5, default: null, description: 'Extend only: fraction of the height to add on the top, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0. Not with targetAspect.' },
    marginBottom: { type: ['number', 'null'], minimum: 0, maximum: 0.5, default: null, description: 'Extend only: fraction of the height to add on the bottom, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0. Not with targetAspect.' },
    prompt: { type: ['string', 'null'], maxLength: 500, default: null, description: 'Extend only: what the new edges should show, up to 500 characters. Blank uses a neutral default.' },
  },
  required: ['op', 'engine', 'source'],
  additionalProperties: false,
  definitions: { VideoSourceRef: DEFINITIONS.VideoSourceRef },
};

/**
 * AudioGenerate step config schema. Mirrors `AudioGenerateStepConfig.cs` (T-108) 1:1 (a COMPLETE mirror). Bounds are
 * the record's `Validate()`; the chosen model's own narrower range is checked by the server at save and run time.
 */
const AUDIO_GENERATE_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    kind: { type: 'string', enum: ['Music', 'SoundEffect'], description: 'Music or SoundEffect.' },
    prompt: { type: 'string', maxLength: 4000, description: 'What to generate. Leave empty when compositionPlanJson is set.' },
    durationSec: {
      type: 'number',
      minimum: 0,
      maximum: 300,
      description: 'Seconds per take: music 3-300, sound effects 0.5-30. 0 with a composition plan.',
    },
    instrumental: { type: ['boolean', 'null'], default: null, description: 'Music, prompt mode only.' },
    compositionPlanJson: { type: ['string', 'null'], default: null, description: 'Music only: the vendor composition plan JSON, used instead of a prompt.' },
    loop: { type: ['boolean', 'null'], default: null, description: 'Sound effects only, where the model supports it.' },
    promptInfluence: { type: ['number', 'null'], minimum: 0, maximum: 1, default: null, description: 'Sound effects only.' },
    providerId: { type: ['string', 'null'], default: null, description: 'Provider row id; blank uses the default MusicGeneration or SoundEffectGeneration row.' },
    model: { type: ['string', 'null'], default: null },
    takes: { type: 'integer', minimum: 1, maximum: 4, default: 1 },
    maxSpendCredits: { type: ['number', 'null'], exclusiveMinimum: 0, default: null, description: 'Spend limit for the whole step in credits.' },
    registerProjectFile: { type: 'boolean', default: true },
    fileNamePrefix: { type: ['string', 'null'], pattern: '^[A-Za-z0-9_-]{1,40}$', default: null },
  },
  required: ['kind', 'prompt', 'durationSec'],
  additionalProperties: false,
};

/**
 * ImageGenerate step config schema. Mirrors `ImageGenerateStepConfig.cs` 1:1 (a COMPLETE mirror). Bounds are the
 * record's `Validate()`; which of prompt / instruction / source applies depends on `mode` and is the server's rule.
 */
const IMAGE_GENERATE_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    mode: { type: 'string', enum: ['Generate', 'Edit'], description: 'Generate (text to image) or Edit (change one source picture).' },
    prompt: { type: ['string', 'null'], maxLength: 4000, default: null, description: 'Generate only: what to draw.' },
    instruction: { type: ['string', 'null'], maxLength: 4000, default: null, description: 'Edit only: what to change in the source picture.' },
    source: { anyOf: [{ $ref: '#/definitions/VideoSourceRef' }, { type: 'null' }], description: 'Edit only: the one picture to change.' },
    variations: { type: 'integer', minimum: 1, maximum: 4, default: 1, description: 'How many pictures (Generate and Edit).' },
    maxSpendCredits: { type: ['number', 'null'], exclusiveMinimum: 0, default: null, description: 'Spend limit for the whole step in credits.' },
    providerId: { type: ['string', 'null'], default: null, description: 'Provider row id; blank uses the default ImageGeneration or ImageEdit row.' },
    aspectRatio: { type: ['string', 'null'], default: null, description: 'Aspect ratio word such as 16:9. Not with width/height.' },
    width: { type: ['integer', 'null'], minimum: 1, maximum: 8192, default: null, description: 'Generate only, with height.' },
    height: { type: ['integer', 'null'], minimum: 1, maximum: 8192, default: null, description: 'Generate only, with width.' },
    resolution: { type: ['string', 'null'], enum: ['1K', '2K', '4K', null], default: null, description: 'Generation tier (long edge 1024, 2048 or 4096); null is automatic. Generate and Edit.' },
    outputWidth: { type: ['integer', 'null'], minimum: 64, maximum: 8192, default: null, description: 'Exact final width in pixels, with outputHeight. Not with width/height/aspectRatio.' },
    outputHeight: { type: ['integer', 'null'], minimum: 64, maximum: 8192, default: null, description: 'Exact final height in pixels, with outputWidth. Not with width/height/aspectRatio.' },
    registerProjectFile: { type: 'boolean', default: true },
    allowSourceMediaEgress: { type: 'boolean', default: false, description: 'Edit only: consent to upload the source picture to the hosted model.' },
    fileNamePrefix: { type: ['string', 'null'], pattern: '^[A-Za-z0-9_-]{1,40}$', default: null },
    itemList: { type: ['string', 'null'], default: null, description: 'Batch: one item per line or comma separated (max 24); {item} in the prompt or instruction is replaced per item.' },
  },
  required: ['mode'],
  // Mirrors the record's Validate(): both exact sides or neither, and an exact size excludes width/height/aspectRatio.
  allOf: [
    { if: { required: ['outputWidth'], properties: { outputWidth: { type: 'integer' } } }, then: { required: ['outputHeight'], properties: { outputHeight: { type: 'integer' } } } },
    { if: { required: ['outputHeight'], properties: { outputHeight: { type: 'integer' } } }, then: { required: ['outputWidth'], properties: { outputWidth: { type: 'integer' } } } },
    {
      if: { required: ['outputWidth'], properties: { outputWidth: { type: 'integer' } } },
      then: { properties: { width: { type: 'null' }, height: { type: 'null' }, aspectRatio: { type: 'null' } } },
    },
  ],
  additionalProperties: false,
  definitions: { VideoSourceRef: DEFINITIONS.VideoSourceRef },
};

/**
 * PinInsert step config schema. Mirrors `PinInsertStepConfig.cs` (T-206) 1:1 (a COMPLETE mirror). The cross-step
 * rules (the analysis is an earlier VideoAnalyze step, Step content an earlier step that renders) are the save
 * validator's; the shape rules below are the record's `Validate()`.
 */
const PIN_INSERT_SCHEMA: JSONSchema7 = {
  type: 'object',
  properties: {
    analysis: {
      $ref: '#/definitions/ExtractInputRef',
      description: 'The VideoAnalyze step whose tracked screens are pinned: from Step (with stepOrder) or Previous.',
    },
    pins: {
      type: 'array',
      minItems: 1,
      maxItems: 16,
      items: {
        type: 'object',
        properties: {
          regionId: {
            type: ['string', 'null'],
            pattern: '^r[0-9]{1,4}$',
            default: null,
            description: 'An offered tracked screen (r0, r1, ...). Empty puts the content on every offered screen.',
          },
          content: { $ref: '#/definitions/InsertContentRef' },
        },
        required: ['content'],
        additionalProperties: false,
      },
    },
    requireApplied: {
      type: 'boolean',
      default: true,
      description: 'Fail the step when a pin cannot be applied (default), or skip it and report it.',
    },
  },
  required: ['analysis', 'pins'],
  additionalProperties: false,
  definitions: { ExtractInputRef: DEFINITIONS.ExtractInputRef, InsertContentRef: DEFINITIONS.InsertContentRef },
};

/**
 * Step config schema registry — one JSON Schema per StepType, keyed by StepType.
 * Each schema validates step configs passed to editors and validators.
 */
export const STEP_CONFIG_SCHEMAS: Record<StepType, JSONSchema7> = {
  Agent: AGENT_SCHEMA,
  Conditional: CONDITIONAL_SCHEMA,
  ForEach: FOREACH_SCHEMA,
  ReviewLoop: REVIEW_LOOP_SCHEMA,
  Parallel: GENERIC_SCHEMA,
  Extract: EXTRACT_SCHEMA,
  VideoAnalyze: VIDEO_ANALYZE_SCHEMA,
  VideoCompile: VIDEO_COMPILE_SCHEMA,
  VideoGenerate: VIDEO_GENERATE_SCHEMA,
  EditRoom: ROOM_SCHEMA,
  GraphicsRoom: ROOM_SCHEMA,
  ColorGradeRoom: ROOM_SCHEMA,
  Voiceover: VOICEOVER_SCHEMA,
  ThumbnailGenerate: THUMBNAIL_GENERATE_SCHEMA,
  // A system step with no author-editable config.
  Revise: { type: 'object', properties: {}, additionalProperties: true },
  DesignGraphic: { type: 'object', properties: {}, additionalProperties: true },
  DailiesRoom: ROOM_SCHEMA,
  TimelineRender: TIMELINE_RENDER_SCHEMA,
  DesignSystem: DESIGN_SYSTEM_SCHEMA,
  ExtractFrame: EXTRACT_FRAME_SCHEMA,
  AudioGenerate: AUDIO_GENERATE_SCHEMA,
  ImageGenerate: IMAGE_GENERATE_SCHEMA,
  VideoEnhance: VIDEO_ENHANCE_SCHEMA,
  PinInsert: PIN_INSERT_SCHEMA,
  AudioCleanup: AUDIO_CLEANUP_SCHEMA,
};
