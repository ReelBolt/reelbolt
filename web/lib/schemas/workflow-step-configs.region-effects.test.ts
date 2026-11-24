import { describe, it, expect } from 'vitest';
import { STEP_CONFIG_SCHEMAS } from './workflow-step-configs';
import { validateStepConfig } from './schema-form';
import { createDefaultVideoAnalyzeStepConfig } from '@/components/workflows/VideoAnalyzeStepConfig';
import { createDefaultVideoCompileStepConfig } from '@/components/workflows/VideoCompileStepConfig';
import { OBJECT_TRACK_METHODS, REGION_EFFECTS } from '@/lib/types/workflow';

/**
 * prompted-region-effects T-006 — the schema half.
 *
 * Two families of assertions, both load-bearing:
 *  - the NEW members are bounded and closed exactly as plan §4 (and T-001's Validate()) says, so the
 *    editor, the assistant's ListStepSchemas payload and the server agree;
 *  - a config written BEFORE the feature existed still validates UNCHANGED, because every new member
 *    is optional. A regression here is a saved step whose Apply can never succeed.
 */

/** A VideoAnalyze config as it was persisted before this feature: the factory minus `expect` (a pre-existing null/$ref mismatch, pinned by the other schema suite). */
const legacyAnalyzeConfig = (): Record<string, unknown> => {
  const config = { ...(createDefaultVideoAnalyzeStepConfig() as unknown as Record<string, unknown>) };
  delete config.expect;
  return config;
};

/** The same for VideoCompile: the factory minus the keys whose $refs are non-nullable objects it emits as null, plus the two fades the schema requires. */
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

describe('VideoAnalyze — tracked objects and the segmentation knobs', () => {
  const props = STEP_CONFIG_SCHEMAS.VideoAnalyze.properties as Record<string, Record<string, unknown>>;

  it('takes the method list from the one shared member list, as a closed enum', () => {
    const items = props.trackObjects.items as { properties: Record<string, Record<string, unknown>> };
    expect(items.properties.method.enum).toEqual([...OBJECT_TRACK_METHODS]);
    expect(OBJECT_TRACK_METHODS).toEqual(['Feature', 'Segmentation']);
    // Closed on purpose: a member the UI cannot render must never reach a saved config.
    expect(items.properties.method.type).toBe('string');
  });

  it('bounds the four segmentation knobs exactly as plan §4 does', () => {
    expect(props.maxSegmentationSeconds).toMatchObject({ minimum: 1, maximum: 600, default: 60 });
    expect(props.segmentationDetectionThreshold).toMatchObject({ minimum: 0.01, maximum: 1, default: 0.5 });
    expect(props.maxSegmentationObjects).toMatchObject({ minimum: 1, maximum: 16, default: 4, type: 'integer' });
    expect(props.segmentationTimeoutSeconds).toMatchObject({ minimum: 60, maximum: 3600, default: 900 });
  });

  it('declares the consent flag as a boolean and the provider as a nullable id', () => {
    expect(props.allowSourceMediaEgress.type).toBe('boolean');
    expect(props.segmentationProviderId.type).toEqual(['string', 'null']);
  });

  it('requires none of the new members, so a config written before the feature still validates', () => {
    const required = STEP_CONFIG_SCHEMAS.VideoAnalyze.required as string[];
    for (const field of [
      'trackObjects',
      'segmentationProviderId',
      'allowSourceMediaEgress',
      'maxSegmentationSeconds',
      'segmentationDetectionThreshold',
      'maxSegmentationObjects',
      'segmentationTimeoutSeconds',
    ]) {
      expect(required).not.toContain(field);
      expect(props).toHaveProperty(field);
    }

    const legacy = legacyAnalyzeConfig();
    for (const field of ['trackObjects', 'allowSourceMediaEgress', 'maxSegmentationSeconds']) {
      expect(legacy).not.toHaveProperty(field);
    }
    expect(validateStepConfig('VideoAnalyze', legacy)).toEqual({ valid: true, errors: [] });
    // Validated twice: AJV must not inject a default into the object it was handed.
    expect(validateStepConfig('VideoAnalyze', legacy)).toEqual({ valid: true, errors: [] });
    expect(legacy).not.toHaveProperty('maxSegmentationSeconds');
  });

  it('accepts Feature, Segmentation and an absent method, and rejects anything else', () => {
    const base = legacyAnalyzeConfig();
    const withTargets = (trackObjects: unknown[]) => validateStepConfig('VideoAnalyze', { ...base, trackObjects });

    expect(withTargets([{ label: 'speedometer' }])).toEqual({ valid: true, errors: [] });
    expect(withTargets([{ label: 'speedometer', method: 'Feature' }])).toEqual({ valid: true, errors: [] });
    expect(withTargets([{ label: 'speedometer', method: 'Segmentation' }])).toEqual({ valid: true, errors: [] });
    expect(withTargets([{ label: 'plate', atSec: 2.5, region: { x0: 0, y0: 0, x1: 0.2, y1: 0, x2: 0, y2: 0.2, x3: 0.2, y3: 0.2 } }]).valid).toBe(true);
    // Unknown members never validate: they would be written by a form that cannot render them back.
    expect(withTargets([{ label: 'speedometer', method: 'Auto' }]).valid).toBe(false);
    expect(withTargets([{ label: 'speedometer', method: 'Tracked' }]).valid).toBe(false);
    // A quad corner outside 0..1 is not a position on the frame.
    expect(withTargets([{ label: 'plate', region: { x0: 1.5, y0: 0, x1: 0.2, y1: 0, x2: 0, y2: 0.2, x3: 0.2, y3: 0.2 } }]).valid).toBe(false);
  });

  it('rejects every out-of-range knob and accepts the inclusive endpoints', () => {
    const base = legacyAnalyzeConfig();
    for (const [field, value] of [
      ['maxSegmentationSeconds', 0.5], ['maxSegmentationSeconds', 601],
      ['segmentationDetectionThreshold', 0.009], ['segmentationDetectionThreshold', 1.01],
      ['maxSegmentationObjects', 0], ['maxSegmentationObjects', 17],
      ['segmentationTimeoutSeconds', 59], ['segmentationTimeoutSeconds', 3601],
      ['maxSegmentationObjects', 2.5],
    ] as const) {
      expect(validateStepConfig('VideoAnalyze', { ...base, [field]: value }).valid).toBe(false);
    }

    expect(validateStepConfig('VideoAnalyze', {
      ...base,
      maxSegmentationSeconds: 1,
      segmentationDetectionThreshold: 0.01,
      maxSegmentationObjects: 1,
      segmentationTimeoutSeconds: 60,
      allowSourceMediaEgress: true,
      segmentationProviderId: '11111111-1111-1111-1111-111111111111',
    })).toEqual({ valid: true, errors: [] });

    expect(validateStepConfig('VideoAnalyze', {
      ...base,
      maxSegmentationSeconds: 600,
      segmentationDetectionThreshold: 1,
      maxSegmentationObjects: 16,
      segmentationTimeoutSeconds: 3600,
    }).valid).toBe(true);
  });
});

describe('VideoCompile — region effects', () => {
  const schema = STEP_CONFIG_SCHEMAS.VideoCompile;
  const props = schema.properties as Record<string, Record<string, unknown>>;

  it('spreads the one shared effect list into a closed enum, in the C# order', () => {
    const items = props.regionEffects.items as {
      properties: Record<string, Record<string, unknown>>;
      required: string[];
    };
    expect(items.properties.effect.enum).toEqual([...REGION_EFFECTS]);
    expect(REGION_EFFECTS).toEqual(['Blur', 'Pixelate', 'Highlight', 'Spotlight', 'ColorPop', 'Outline']);
    expect(items.required).toEqual(['target', 'effect']);
  });

  it('caps the list at eight entries and bounds every number', () => {
    const items = props.regionEffects.items as { properties: Record<string, Record<string, unknown>> };
    expect(props.regionEffects.maxItems).toBe(8);
    expect(props.regionEffects.type).toEqual(['array', 'null']);
    expect(items.properties.strength).toMatchObject({ minimum: 0, maximum: 2 });
    expect(items.properties.featherPx).toMatchObject({ minimum: 0, maximum: 64 });
    expect(items.properties.opacity).toMatchObject({ minimum: 0, maximum: 1 });
    expect(items.properties.target.maxLength).toBe(120);
    expect(items.properties.invert.type).toBe('boolean');
  });

  it('appends regionEffects last, after the last member master had, and requires nothing', () => {
    const keys = Object.keys(props);
    expect(keys[keys.length - 1]).toBe('regionEffects');
    expect(schema.required).not.toContain('regionEffects');
  });

  it('still validates a config written before the feature existed, unchanged', () => {
    const legacy = legacyCompileConfig();
    expect(legacy).not.toHaveProperty('regionEffects');
    expect(validateStepConfig('VideoCompile', legacy)).toEqual({ valid: true, errors: [] });
    expect(legacy).not.toHaveProperty('regionEffects');
  });

  it('accepts a well-formed list and rejects an unknown effect or a stray bound', () => {
    const base = legacyCompileConfig();
    const withEffects = (regionEffects: unknown[]) => validateStepConfig('VideoCompile', { ...base, regionEffects });

    expect(withEffects([{ target: 'g1', effect: 'Blur' }])).toEqual({ valid: true, errors: [] });
    expect(withEffects([
      { target: 'speedometer', effect: 'Highlight', invert: false, strength: 1.5, featherPx: 8, opacity: 0.75 },
      { target: 'g2', effect: 'ColorPop', invert: true },
    ])).toEqual({ valid: true, errors: [] });

    // An effect the engine does not implement must never validate: the enum is the argv allowlist.
    expect(withEffects([{ target: 'g1', effect: 'Glow' }]).valid).toBe(false);
    expect(withEffects([{ target: 'g1', effect: 'blur' }]).valid).toBe(false);
    // Out-of-range numbers are refused here rather than clamped at render time.
    expect(withEffects([{ target: 'g1', effect: 'Blur', strength: 2.1 }]).valid).toBe(false);
    expect(withEffects([{ target: 'g1', effect: 'Blur', strength: -0.1 }]).valid).toBe(false);
    expect(withEffects([{ target: 'g1', effect: 'Blur', featherPx: 65 }]).valid).toBe(false);
    expect(withEffects([{ target: 'g1', effect: 'Blur', opacity: 1.1 }]).valid).toBe(false);
    // The target is a lookup key, and it is never a leaf name.
    expect(withEffects([{ target: 'x'.repeat(121), effect: 'Blur' }]).valid).toBe(false);
    // Both members are required: an entry with no object, or no effect, cannot be executed.
    expect(withEffects([{ effect: 'Blur' }]).valid).toBe(false);
    expect(withEffects([{ target: 'g1' }]).valid).toBe(false);
  });

  it('accepts eight entries and refuses a ninth (EFFECT_LIMIT_EXCEEDED, never a silent drop)', () => {
    const base = legacyCompileConfig();
    const effects = (count: number) =>
      Array.from({ length: count }, (_, i) => ({ target: `g${i + 1}`, effect: 'Blur' }));

    expect(validateStepConfig('VideoCompile', { ...base, regionEffects: effects(8) })).toEqual({ valid: true, errors: [] });
    const nine = validateStepConfig('VideoCompile', { ...base, regionEffects: effects(9) });
    expect(nine.valid).toBe(false);
    expect(nine.errors.some((e) => e.path === '/regionEffects')).toBe(true);
  });
});
