/** @vitest-environment node */
/**
 * Schema-drift guard for the mirrored EditTimeline v2 contract.
 *
 * `inference/src/ReelBolt.Shared/Editing/EditTimeline.schema.json` is the authoritative
 * contract (committed, then amended by the `MediaClip.gainDb` and `Provenance.agentName`/
 * `providerName` commits). `web/lib/editing/types.ts` is a hand-written TypeScript mirror of
 * it, and the only way a hand-written mirror stays honest is an executable assertion against
 * the source of truth — which is what this file is.
 *
 * Two independent things are proven here:
 *   1. The item-type discriminator sets in `types.ts` equal the schema's, both directions,
 *      and the fixture document itself type-checks against the mirrored `EditTimeline`.
 *   2. The fixtures are actually validated by ajv (a negative fixture that still passes would
 *      mean ajv silently no-op'ed, which is the failure mode this file exists to prevent).
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import Ajv2020 from 'ajv/dist/2020';
import { describe, expect, it } from 'vitest';

import type { EditTimeline, Item, Rational } from '../types';
import { framesToTimecode, timecodeToFrames } from '../time';

// ─────────────────────────────────────────────────────────────────────────────
// The schema, read relatively from this file — never from process.cwd(), so the
// guard does not depend on where the runner was invoked from.
// ─────────────────────────────────────────────────────────────────────────────

const SCHEMA_PATH = fileURLToPath(
  new URL('../../../../inference/src/ReelBolt.Shared/Editing/EditTimeline.schema.json', import.meta.url),
);

/** The subset of JSON Schema this guard reads. */
type Subschema = {
  description?: string;
  required?: readonly string[];
  properties?: Record<string, { const?: string }>;
  oneOf?: readonly { $ref: string }[];
};

type TimelineSchema = { $defs: Record<string, Subschema> };

const schema = JSON.parse(readFileSync(SCHEMA_PATH, 'utf8')) as TimelineSchema;

// ─────────────────────────────────────────────────────────────────────────────
// Compile-time half of the drift guard.
//
// These three constants are the *only* place the item-type lists are written down, and each
// is type-checked against `Item['type']` by `npx tsc --noEmit`:
//   - the two annotations fail if a listed literal is not a real `Item['type']` member;
//   - `allItemTypesCovered` is `true` only when the two lists together cover the union with
//     nothing left over (`UncoveredItemType` collapses to `never`).
// The runtime half then proves those same two lists equal the schema's.
// ─────────────────────────────────────────────────────────────────────────────

const IMPLEMENTED_ITEM_TYPES = [
  'mediaClip',
  'titleClip',
  'generatorClip',
  'insertClip',
  'caption',
  'transition',
  'adjustmentClip',
  'compoundClip',
  'multicamClip',
] as const;

/** Since P8 the schema documents no stub kinds; the list stays so the partition is still asserted. */
const STUB_ITEM_TYPES = [] as const;

const implementedAreItemTypes: readonly Item['type'][] = IMPLEMENTED_ITEM_TYPES;
const stubsAreItemTypes: readonly Item['type'][] = [...STUB_ITEM_TYPES];

type UncoveredItemType = Exclude<
  Item['type'],
  (typeof IMPLEMENTED_ITEM_TYPES)[number] | (typeof STUB_ITEM_TYPES)[number]
>;
const allItemTypesCovered: UncoveredItemType extends never ? true : false = true;

// ─────────────────────────────────────────────────────────────────────────────
// Discriminator sets, derived from $defs.Item.oneOf -> $defs.<Name>.properties.type.const
// ─────────────────────────────────────────────────────────────────────────────

const itemDef = schema.$defs.Item;
const oneOf = itemDef.oneOf ?? [];

/** A stub is a `$defs` entry the schema itself documents as a TODO. */
const isStubDefinition = (def: Subschema | undefined): boolean =>
  typeof def?.description === 'string' && def.description.startsWith('TODO(');

const discriminatorEntries = oneOf.map((entry) => {
  const defName = entry.$ref.replace('#/$defs/', '');
  const def = schema.$defs[defName];
  return {
    defName,
    typeConst: def?.properties?.type?.const as string,
    isStub: isStubDefinition(def),
    required: [...(def?.required ?? [])],
  };
});

const sorted = (values: Iterable<string>): string[] => [...values].sort();

const schemaImplementedTypes = sorted(
  discriminatorEntries.filter((e) => !e.isStub).map((e) => e.typeConst),
);
const schemaStubTypes = sorted(discriminatorEntries.filter((e) => e.isStub).map((e) => e.typeConst));

// ─────────────────────────────────────────────────────────────────────────────
// Fixtures
// ─────────────────────────────────────────────────────────────────────────────

const uuid = (n: number): string => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const IDS = {
  timeline: uuid(1),
  project: uuid(2),
  videoTrack: uuid(3),
  audioTrack: uuid(4),
  projectFile: uuid(5),
  stepResult: uuid(6),
};

const baseItem = (type: string, n: number) => ({
  id: uuid(100 + n),
  type,
  name: `${type} ${n}`,
  recordIn: n * 10,
  recordOut: n * 10 + 10,
  provenance: null,
});

/**
 * A complete EditTimeline carrying one item of every member kind — the six implemented ones
 * including the P8 compound and multicam clips, a sequence and a bus. Typed as `EditTimeline`, so this literal doubles as a
 * compile-time check that the mirrored contract actually describes a real document.
 */
const validTimeline: EditTimeline = {
  schemaVersion: 2,
  id: IDS.timeline,
  projectId: IDS.project,
  name: 'Schema drift fixture',
  settings: {
    width: 1920,
    height: 1080,
    rate: { num: 30000, den: 1001 },
    sampleRate: 48000,
    pixelAspect: { num: 1, den: 1 },
    aspectPolicy: 'fit',
    startTimecodeFrames: 0,
  },
  tracks: [
    {
      id: IDS.videoTrack,
      kind: 'video',
      name: 'V1',
      color: '#4C6EF5',
      height: 72,
      locked: false,
      enabled: true,
      mute: false,
      solo: false,
      busId: null,
      items: [
        {
          ...baseItem('mediaClip', 0),
          type: 'mediaClip',
          assetRef: { kind: 'projectFile', projectFileId: IDS.projectFile, stepResultId: null },
          sourceIn: 0,
          playbackRate: 1,
          enabled: true,
          gainDb: null,
        },
        {
          ...baseItem('titleClip', 1),
          type: 'titleClip',
          text: 'Lower third',
          fontSize: 48,
          fontFamily: 'Arial',
          color: '#FFFFFF',
          enabled: true,
        },
        {
          ...baseItem('generatorClip', 2),
          type: 'generatorClip',
          generatorType: 'bars',
          generatorConfig: null,
          enabled: true,
        },
        {
          ...baseItem('insertClip', 3),
          type: 'insertClip',
          assetRef: { kind: 'stepResult', projectFileId: null, stepResultId: IDS.stepResult },
          quad: {
            value: {
              topLeft: { x: 0, y: 0 },
              topRight: { x: 1, y: 0 },
              bottomRight: { x: 1, y: 1 },
              bottomLeft: { x: 0, y: 1 },
            },
            keys: null,
          },
          enabled: true,
        },
        {
          ...baseItem('caption', 4),
          type: 'caption',
          text: 'On screen',
          language: 'en',
          enabled: true,
        },
        {
          ...baseItem('transition', 5),
          type: 'transition',
          transitionType: 'dissolve',
          easing: 'linear',
          enabled: true,
        },
        { ...baseItem('compoundClip', 6), type: 'compoundClip', sequenceId: uuid(300), sourceIn: 0, enabled: true },
        {
          ...baseItem('multicamClip', 7),
          type: 'multicamClip',
          angles: [
            { id: uuid(310), name: 'Cam A', assetRef: { kind: 'projectFile', projectFileId: IDS.projectFile, stepResultId: null }, offsetFrames: 0 },
            { id: uuid(311), name: 'Cam B', assetRef: { kind: 'projectFile', projectFileId: IDS.projectFile, stepResultId: null }, offsetFrames: 12 },
          ],
          activeAngleId: uuid(310),
          cuts: [{ atFrame: 75, angleId: uuid(311) }],
          sourceIn: 0,
          enabled: true,
          gainDb: null,
        },
        { ...baseItem('adjustmentClip', 8), type: 'adjustmentClip' },
      ],
    },
    {
      id: IDS.audioTrack,
      kind: 'audio',
      name: 'A1',
      color: null,
      height: 56,
      locked: true,
      enabled: true,
      mute: false,
      solo: true,
      busId: uuid(9),
      items: [
        {
          ...baseItem('mediaClip', 9),
          type: 'mediaClip',
          assetRef: { kind: 'projectFile', projectFileId: IDS.projectFile, stepResultId: null },
          sourceIn: 0,
          playbackRate: 1,
          enabled: true,
          gainDb: {
            value: -6,
            keys: [
              { frame: 0, value: -18, interp: 'easeInOutQuad' },
              { frame: 120, value: -6, interp: 'step' },
            ],
          },
          provenance: {
            origin: 'generated',
            stepResultId: IDS.stepResult,
            agentDefinitionId: null,
            providerId: null,
            offeredId: 's3',
            rationale: 'Dialogue ducking envelope',
            editedByUser: false,
            agentName: 'SoundDesigner',
            providerName: 'Local vLLM',
          },
        },
      ],
    },
  ],
  markers: [
    { id: uuid(200), frame: 0, name: 'Start', color: '#FF0000', note: null, durationFrames: null },
  ],
  inOut: { inFrame: 0, outFrame: 300 },
  seed: { executionId: uuid(201), version: 1 },
  sequences: [{ id: uuid(300), name: 'Nested', tracks: [] }],
  buses: [
    {
      id: uuid(9),
      name: 'Dialogue',
      gainDb: null,
      pan: 0,
      eq: null,
      dynamics: { compressor: { thresholdDb: -18, ratio: 3, attackMs: 20, releaseMs: 250, makeupDb: 0 }, limiter: { ceilingDb: -1 }, gate: null, deesser: null },
      denoise: { kind: 'fft', strength: 0.5 },
      leveler: { kind: 'speech', targetDb: -18 },
      outputBusId: null,
    },
  ],
};

/** A deep copy widened to an untyped shape, so negative fixtures can break it on purpose. */
const brokenCopy = (): { tracks: { items: Record<string, unknown>[] }[] } =>
  JSON.parse(JSON.stringify(validTimeline)) as { tracks: { items: Record<string, unknown>[] }[] };

const missingAssetRefFixture = brokenCopy();
delete missingAssetRefFixture.tracks[0].items[0].assetRef;

const unknownItemTypeFixture = brokenCopy();
unknownItemTypeFixture.tracks[0].items[1].type = 'futureClip';

// ─────────────────────────────────────────────────────────────────────────────
// ajv
// ─────────────────────────────────────────────────────────────────────────────

// The schema is draft 2020-12 (hence Ajv2020), uses the non-core `discriminator` keyword and
// `format: "uuid"`; `strict: false` is what lets it compile without pulling in ajv-formats or
// editing the schema. It is NOT what makes the negative fixtures fail — that is asserted below.
const ajv = new Ajv2020({ strict: false, allErrors: true });
const validate = ajv.compile<TimelineSchema>(schema);

const errorsFor = (data: unknown) => {
  const valid = validate(data);
  return { valid, errors: validate.errors ?? [] };
};

describe('EditTimeline schema drift', () => {
  it('resolves the schema relatively, not from process.cwd()', () => {
    expect(SCHEMA_PATH.endsWith('inference/src/ReelBolt.Shared/Editing/EditTimeline.schema.json')).toBe(true);
    expect(SCHEMA_PATH.startsWith('/')).toBe(true);
    expect(schema.$defs.EditTimelineSettings).toBeDefined();
    expect(schema.$defs.Item).toBeDefined();
  });

  it('partitions $defs.Item.oneOf into nine implemented kinds and no stubs (P8)', () => {
    expect(oneOf).toHaveLength(9);
    expect(schemaImplementedTypes).toHaveLength(9);
    expect(schemaStubTypes).toHaveLength(0);

    // Every stub is a stub because the schema says so, and its required set is the bare
    // { id, type } a stub ships with.
    for (const entry of discriminatorEntries.filter((e) => e.isStub)) {
      expect(entry.required.sort()).toEqual(['id', 'type']);
    }
  });

  it("matches the nine implemented Item['type'] literals of types.ts exactly, both directions", () => {
    expect(implementedAreItemTypes).toEqual([...IMPLEMENTED_ITEM_TYPES]);
    expect([...implementedAreItemTypes].sort()).toEqual(schemaImplementedTypes);
  });

  it("matches the (empty) stub Item['type'] literals of types.ts exactly, both directions", () => {
    expect(stubsAreItemTypes).toEqual([...STUB_ITEM_TYPES]);
    expect([...stubsAreItemTypes].sort()).toEqual(schemaStubTypes);
  });

  it("covers Item['type'] with nothing left over, and with no literal in both lists", () => {
    expect(allItemTypesCovered).toBe(true);
    expect([...IMPLEMENTED_ITEM_TYPES].filter((t) => (STUB_ITEM_TYPES as readonly string[]).includes(t))).toEqual([]);
  });
});

describe('EditTimeline fixtures under ajv', () => {
  it('compiled the schema (so the negative cases below cannot pass for the wrong reason)', () => {
    expect(typeof validate).toBe('function');
    expect(ajv.errors ?? []).toEqual([]);
  });

  it('accepts a full nine-member-kind timeline', () => {
    const { valid, errors } = errorsFor(validTimeline);
    expect(errors).toEqual([]);
    expect(valid).toBe(true);
  });

  it('rejects a mediaClip with no assetRef, for exactly that reason', () => {
    const { valid, errors } = errorsFor(missingAssetRefFixture);
    expect(valid).toBe(false);

    const reason = errors.find(
      (e) => e.keyword === 'required' && (e.params as { missingProperty?: string }).missingProperty === 'assetRef',
    );
    expect(reason).toBeDefined();
    expect(reason?.instancePath).toBe('/tracks/0/items/0');
  });

  it('rejects an unknown item type, for exactly that reason', () => {
    const { valid, errors } = errorsFor(unknownItemTypeFixture);
    expect(valid).toBe(false);
    expect(errors.length).toBeGreaterThan(0);

    // The failure is about the discriminator value and belongs to the offending item — not to
    // the schema itself failing to compile.
    const reason = errors.find((e) => e.keyword === 'discriminator' || e.keyword === 'const' || e.keyword === 'oneOf');
    expect(reason).toBeDefined();
    expect(errors.every((e) => e.keyword !== 'schema' && typeof e.schemaPath === 'string')).toBe(true);
  });
});

describe('time.ts', () => {
  const NTSC = { num: 30000, den: 1001 };
  const RATES: Rational[] = [{ num: 24, den: 1 }, { num: 25, den: 1 }, { num: 30, den: 1 }, NTSC, { num: 24000, den: 1001 }];
  const FRAMES = [0, 1, 23, 24, 29, 30, 59, 60, 3599, 3600, 86399, 123456];

  it('round-trips every frame count at every rate (non-drop-frame)', () => {
    for (const rate of RATES) {
      for (const frames of FRAMES) {
        expect(timecodeToFrames(framesToTimecode(frames, rate), rate)).toBe(frames);
      }
    }
  });

  it('formats HH:MM:SS:FF with the rate rounded to a whole timecode base', () => {
    expect(framesToTimecode(0, { num: 30, den: 1 })).toBe('00:00:00:00');
    expect(framesToTimecode(29, { num: 30, den: 1 })).toBe('00:00:00:29');
    expect(framesToTimecode(30, { num: 30, den: 1 })).toBe('00:00:01:00');
    expect(framesToTimecode(3600, { num: 24, den: 1 })).toBe('00:02:30:00');
    // 30000/1001 is 29.97 — a 30-frame timecode base, the same convention lib/utils/timecode.ts uses.
    expect(framesToTimecode(30, NTSC)).toBe('00:00:01:00');
  });

  it('tolerates malformed input instead of throwing', () => {
    expect(framesToTimecode(Number.NaN, { num: 30, den: 1 })).toBe('00:00:00:00');
    expect(framesToTimecode(-5, { num: 30, den: 1 })).toBe('00:00:00:00');

    expect(timecodeToFrames('not a timecode', { num: 30, den: 1 })).toBe(0);
    expect(timecodeToFrames('', { num: 30, den: 1 })).toBe(0);
    expect(timecodeToFrames('00:00:00', { num: 30, den: 1 })).toBe(0);
    expect(timecodeToFrames('00:00:00:00:00', { num: 30, den: 1 })).toBe(0);
    expect(timecodeToFrames('00:00:00:00', { num: 0, den: 0 })).toBe(0);
  });
});
