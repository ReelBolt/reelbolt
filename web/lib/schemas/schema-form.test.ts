/**
 * Tests for validateStepConfig and applySchemaEdits: round-trip validation,
 * unknown-field preservation, null handling, required fields, and error paths.
 */

import { describe, it, expect } from 'vitest';
import { validateStepConfig, applySchemaEdits, applySchemaEditsForStepType } from './schema-form';
import { STEP_CONFIG_SCHEMAS } from './workflow-step-configs';
import type { StepType } from '@/lib/types/workflow';

describe('validateStepConfig', () => {
  describe('Extract schema validation', () => {
    it('should accept valid Extract config', () => {
      const config = {
        version: 1,
        operation: 'Project' as const,
        inputs: { source: { from: 'Previous' as const } },
        idPrefix: 'item_',
        skip: 0,
        onUnknownId: 'Fail' as const,
        includeSummaries: true,
        includeContent: false,
        maxCharsPerFile: 5000,
        maxOutputChars: 10000,
      };
      const result = validateStepConfig('Extract', config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should reject Extract config missing required field version', () => {
      const config = {
        operation: 'Project' as const,
        inputs: { source: { from: 'Previous' as const } },
        idPrefix: 'item_',
        skip: 0,
        onUnknownId: 'Fail' as const,
        includeSummaries: true,
        includeContent: false,
        maxCharsPerFile: 5000,
        maxOutputChars: 10000,
      };
      const result = validateStepConfig('Extract', config);
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
      expect(result.errors.some((e) => e.message.includes('required'))).toBe(true);
    });

    it('should reject Extract config with invalid operation enum', () => {
      const config = {
        version: 1,
        operation: 'InvalidOp',
        inputs: { source: { from: 'Previous' as const } },
        idPrefix: 'item_',
        skip: 0,
        onUnknownId: 'Fail' as const,
        includeSummaries: true,
        includeContent: false,
        maxCharsPerFile: 5000,
        maxOutputChars: 10000,
      };
      const result = validateStepConfig('Extract', config);
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });

    it('should provide path information in validation errors', () => {
      const config = {
        version: 1,
        operation: 'Project' as const,
        inputs: {
          source: {
            from: 'InvalidSource',
          },
        },
        idPrefix: 'item_',
        skip: 0,
        onUnknownId: 'Fail' as const,
        includeSummaries: true,
        includeContent: false,
        maxCharsPerFile: 5000,
        maxOutputChars: 10000,
      };
      const result = validateStepConfig('Extract', config);
      expect(result.valid).toBe(false);
      const errors = result.errors;
      expect(errors.length).toBeGreaterThan(0);
      // At least one error should have a path
      expect(errors.some((e) => e.path !== undefined)).toBe(true);
    });

    it('should accept nested structures like ExtractExpectation', () => {
      const config = {
        version: 1,
        operation: 'Project' as const,
        inputs: { source: { from: 'Previous' as const } },
        idPrefix: 'item_',
        skip: 0,
        onUnknownId: 'Fail' as const,
        includeSummaries: true,
        includeContent: false,
        maxCharsPerFile: 5000,
        maxOutputChars: 10000,
        expect: {
          requiredPaths: ['/data/items'],
          minItems: 1,
          maxItems: 100,
        },
      };
      const result = validateStepConfig('Extract', config);
      expect(result.valid).toBe(true);
    });

    it('should reject nested structure with invalid field types', () => {
      const config = {
        version: 1,
        operation: 'Project' as const,
        inputs: { source: { from: 'Previous' as const } },
        idPrefix: 'item_',
        skip: 0,
        onUnknownId: 'Fail' as const,
        includeSummaries: true,
        includeContent: false,
        maxCharsPerFile: 5000,
        maxOutputChars: 10000,
        expect: {
          minItems: 'not a number',
        },
      };
      const result = validateStepConfig('Extract', config);
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });
  });

  describe('VideoAnalyze schema validation', () => {
    it('should accept valid VideoAnalyze config', () => {
      const config = {
        version: 1,
        detectSilence: true,
        silenceThresholdDb: -40,
        minSilenceMs: 300,
        detectShots: true,
        sceneThreshold: 0.5,
        transcription: 'Required' as const,
        wordTimestamps: true,
        maxAsrChunkBytes: 1000000,
        maxDurationSeconds: 3600,
        maxInputBytes: 5000000000,
        maxOutputChars: 50000,
        maxViewSegments: 1000,
        maxSegmentTextChars: 500,
        analyzeVisuals: true,
        visualSampleFps: 2,
        visualGridWidth: 16,
        visualGridHeight: 9,
        maxVisualSampleFrames: 1000,
        stillMotionThreshold: 0.05,
        minStillWindowMs: 1000,
        maxStillWindowsPerShot: 3,
        detectLetterbox: true,
        detectSharpness: false,
        analyzeAudioLevels: true,
        detectNearDuplicates: true,
        duplicateSimilarityThreshold: 0.9,
        duplicateWindowShots: 10,
        visualDetail: 'Full' as const,
        maxViewDuplicateGroups: 50,
        analyzeColorGrading: true,
        detectLookGroups: true,
        lookSimilarityThreshold: 0.8,
        maxViewLookGroups: 20,
        keyframesPerShot: 1,
        maxSharpnessShots: 100,
      };
      const result = validateStepConfig('VideoAnalyze', config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should reject VideoAnalyze config missing required fields', () => {
      const config = {
        version: 1,
      };
      const result = validateStepConfig('VideoAnalyze', config);
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });
  });

  describe('Room schema validation', () => {
    it('should accept valid EditRoom config', () => {
      const config = {
        version: 1,
        view: { from: 'Previous' as const },
        rounds: 2,
        maxTurns: 10,
        persistTranscript: true,
        fallbackToSoloEditor: false,
      };
      const result = validateStepConfig('EditRoom', config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should accept EditRoom config with unknown fields', () => {
      const config = {
        version: 1,
        view: { from: 'Previous' as const },
        rounds: 2,
        maxTurns: 10,
        persistTranscript: true,
        fallbackToSoloEditor: false,
        unknownField: 'should be preserved',
        anotherUnknown: { nested: 'data' },
      };
      const result = validateStepConfig('EditRoom', config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should reject EditRoom config missing required version', () => {
      const config = {
        view: { from: 'Previous' as const },
      };
      const result = validateStepConfig('EditRoom', config);
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });

    it('should accept GraphicsRoom config with fallbackToSoloPlanner', () => {
      const config = {
        version: 1,
        view: { from: 'Step' as const, stepOrder: 2 },
        rounds: 3,
        fallbackToSoloPlanner: true,
      };
      const result = validateStepConfig('GraphicsRoom', config);
      expect(result.valid).toBe(true);
    });

    it('should accept ColorGradeRoom config with fallbackToSoloColorist', () => {
      const config = {
        version: 1,
        view: { from: 'Step' as const, stepOrder: 3 },
        fallbackToSoloColorist: false,
      };
      const result = validateStepConfig('ColorGradeRoom', config);
      expect(result.valid).toBe(true);
    });
  });

  describe('Generic schema validation (Agent, Conditional, etc.)', () => {
    it('should accept any object for Agent', () => {
      const config = { anything: 'goes', nested: { fields: 123 } };
      const result = validateStepConfig('Agent', config);
      expect(result.valid).toBe(true);
    });

    it('should accept any object for Conditional', () => {
      const config = { foo: 'bar', baz: [1, 2, 3] };
      const result = validateStepConfig('Conditional', config);
      expect(result.valid).toBe(true);
    });
  });
});

describe('applySchemaEdits', () => {
  it('should apply edits to existing object', () => {
    const schema = STEP_CONFIG_SCHEMAS.EditRoom;
    const raw = {
      version: 1,
      view: { from: 'Previous' as const },
      rounds: 2,
    };
    const edits = { rounds: 5, maxTurns: 20 };
    const result = applySchemaEdits(raw, schema, edits);
    expect(result.version).toBe(1);
    expect(result.view).toEqual({ from: 'Previous' });
    expect(result.rounds).toBe(5);
    expect(result.maxTurns).toBe(20);
  });

  it('should preserve unknown fields when applying edits', () => {
    const schema = STEP_CONFIG_SCHEMAS.EditRoom;
    const raw = {
      version: 1,
      view: { from: 'Previous' as const },
      rounds: 2,
      unknownField: 'preserve me',
      anotherUnknown: { nested: true },
    };
    const edits = { rounds: 5 };
    const result = applySchemaEdits(raw, schema, edits);
    expect(result.unknownField).toBe('preserve me');
    expect(result.anotherUnknown).toEqual({ nested: true });
    expect(result.rounds).toBe(5);
  });

  it('should default null/undefined raw to empty object', () => {
    const schema = STEP_CONFIG_SCHEMAS.EditRoom;
    const edits = { version: 1, rounds: 2 };
    const result1 = applySchemaEdits(null, schema, edits);
    const result2 = applySchemaEdits(undefined, schema, edits);
    expect(result1.version).toBe(1);
    expect(result1.rounds).toBe(2);
    expect(result2.version).toBe(1);
    expect(result2.rounds).toBe(2);
  });

  it('should only overwrite schema-declared fields', () => {
    const schema = STEP_CONFIG_SCHEMAS.EditRoom;
    const raw = {
      version: 1,
      view: { from: 'Previous' as const },
      rounds: 2,
      secretField: 'do not touch',
    };
    const edits = {
      rounds: 5,
      secretField: 'attempt override',
      maxTurns: 30,
    };
    const result = applySchemaEdits(raw, schema, edits);
    // rounds and maxTurns are schema fields, so they get edited
    expect(result.rounds).toBe(5);
    expect(result.maxTurns).toBe(30);
    // secretField is NOT in schema, so it's not overwritten by edits, but preserved from raw
    expect(result.secretField).toBe('do not touch');
  });

  it('should handle Extract schema edits', () => {
    const schema = STEP_CONFIG_SCHEMAS.Extract;
    const raw = {
      version: 1,
      operation: 'Project' as const,
      inputs: { source: { from: 'Previous' as const } },
      idPrefix: 'old_',
      skip: 5,
      onUnknownId: 'Fail' as const,
      includeSummaries: false,
      includeContent: true,
      maxCharsPerFile: 2000,
      maxOutputChars: 5000,
      customField: 'preserve this',
    };
    const edits = {
      idPrefix: 'new_',
      skip: 10,
    };
    const result = applySchemaEdits(raw, schema, edits);
    expect(result.idPrefix).toBe('new_');
    expect(result.skip).toBe(10);
    expect(result.operation).toBe('Project');
    expect(result.customField).toBe('preserve this');
  });
});

describe('applySchemaEditsForStepType', () => {
  it('should apply edits using the stepType schema lookup', () => {
    const raw = {
      version: 1,
      view: { from: 'Previous' as const },
      rounds: 2,
      unknownField: 'preserve',
    };
    const edits = { rounds: 4, maxTurns: 15 };
    const result = applySchemaEditsForStepType('EditRoom', raw, edits);
    expect(result.rounds).toBe(4);
    expect(result.maxTurns).toBe(15);
    expect(result.unknownField).toBe('preserve');
  });

  it('should work for Extract step type', () => {
    const raw = {
      version: 1,
      operation: 'Project' as const,
      inputs: { source: { from: 'Previous' as const } },
      idPrefix: 'item_',
      skip: 0,
      onUnknownId: 'Fail' as const,
      includeSummaries: true,
      includeContent: false,
      maxCharsPerFile: 5000,
      maxOutputChars: 10000,
      customExtensionData: { foo: 'bar' },
    };
    const edits = { skip: 5, take: 20 };
    const result = applySchemaEditsForStepType('Extract', raw, edits);
    expect(result.skip).toBe(5);
    expect(result.take).toBe(20);
    expect(result.customExtensionData).toEqual({ foo: 'bar' });
  });

  it('should default null to empty object', () => {
    const edits = { version: 1, rounds: 2 };
    const result = applySchemaEditsForStepType('GraphicsRoom', null, edits);
    expect(result.version).toBe(1);
    expect(result.rounds).toBe(2);
  });
});

describe('Full round-trip: validate -> edit -> validate', () => {
  it('should support EditRoom round-trip with unknown fields', () => {
    // Start with a valid config including unknown fields
    const original = {
      version: 1,
      view: { from: 'Step' as const, stepOrder: 2 },
      rounds: 3,
      maxTurns: 12,
      persistTranscript: false,
      customSeats: [{ name: 'Editor1' }, { name: 'Editor2' }],
      temperatures: { editor1: 0.7, editor2: 0.8 },
    };

    // Validate it
    let result = validateStepConfig('EditRoom', original);
    expect(result.valid).toBe(true);

    // Apply edits
    const edited = applySchemaEditsForStepType('EditRoom', original, {
      rounds: 4,
      persistTranscript: true,
    });

    // Validate the edited version
    result = validateStepConfig('EditRoom', edited);
    expect(result.valid).toBe(true);

    // Verify edits were applied and unknown fields preserved
    expect(edited.rounds).toBe(4);
    expect(edited.persistTranscript).toBe(true);
    expect(edited.customSeats).toEqual(original.customSeats);
    expect(edited.temperatures).toEqual(original.temperatures);
  });

  it('should support Extract round-trip', () => {
    const original = {
      version: 1,
      operation: 'Project' as const,
      inputs: { main: { from: 'Step' as const, stepOrder: 1 } },
      idPrefix: 'id_',
      skip: 0,
      take: 50,
      onUnknownId: 'Skip' as const,
      includeSummaries: true,
      includeContent: true,
      maxCharsPerFile: 10000,
      maxOutputChars: 100000,
      path: '/items',
      fields: ['name', 'description'],
      backendMetadata: { version: 2, encoding: 'utf-8' },
    };

    let result = validateStepConfig('Extract', original);
    expect(result.valid).toBe(true);

    const edited = applySchemaEditsForStepType('Extract', original, {
      skip: 10,
      take: 25,
      path: '/updated/path',
    });

    result = validateStepConfig('Extract', edited);
    expect(result.valid).toBe(true);

    expect(edited.skip).toBe(10);
    expect(edited.take).toBe(25);
    expect(edited.path).toBe('/updated/path');
    expect(edited.backendMetadata).toEqual(original.backendMetadata);
  });
});
