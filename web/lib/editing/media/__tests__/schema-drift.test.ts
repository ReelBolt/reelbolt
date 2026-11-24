/** @vitest-environment node */
/**
 * Schema-drift guard for the media-pool contract.
 *
 * `web/lib/editing/media/media-pool.schema.json` is the committed contract — the frontend's own
 * transcription of T-101/T-102, because the backend C# was not on this branch (see that file's
 * `$comment`). `types.ts` is a hand-written TypeScript mirror of it, and the only way a hand-written
 * mirror stays honest is an executable assertion against the source of truth. That is this file, and
 * it follows the house style of `web/lib/editing/__tests__/schema-drift.test.ts`.
 *
 * Three independent things are proven:
 *   1. Every closed union in `types.ts` equals the schema's `enum`, in BOTH directions, and covers
 *      the union with nothing left over (checked at compile time by `npx tsc --noEmit`).
 *   2. The opaque mediaRef token's two prefixes agree between `types.ts` and the schema's `pattern`,
 *      and `isMediaRef` accepts exactly what the pattern accepts.
 *   3. The fixtures are genuinely validated by ajv — including a negative case whose failure is
 *      attributed to the right keyword, since a negative fixture that passes for an unrelated reason
 *      is the failure mode this half exists to prevent.
 */

import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import Ajv2020 from 'ajv/dist/2020';
import { describe, expect, it } from 'vitest';

import {
  DERIVATIVE_KINDS,
  MEDIA_CATEGORIES,
  MEDIA_KINDS,
  MEDIA_REF_PREFIXES,
  isMediaRef,
} from '../types';
import type { MediaCategory, MediaDerivativeKind, MediaKind } from '../types';
import { INVALID_FIXTURES, VALID_FIXTURES } from '../fixtures';

// ─────────────────────────────────────────────────────────────────────────────
// The BACKEND drift guard.
//
// `media-pool.schema.json` is the frontend's transcription of the backend contract, which is only
// honest while it is checked against the backend itself. These tests read the landed C# enums and
// compare them, name for name, with the mirror above — the same coupling
// `web/lib/editing/__tests__/schema-drift.test.ts` already has to the EditTimeline schema.
//
// They are skipped when the C# is not present in this checkout, because the backend half of T-101/
// T-102 landed concurrently with this ticket and a checkout without it is a legitimate state. A skip
// is visible in the run, unlike a silently-passing assertion.
// ─────────────────────────────────────────────────────────────────────────────

const MEDIA_DERIVATIVE_MODEL = fileURLToPath(
  new URL(
    '../../../../../inference/src/ReelBolt.Shared/Data/Models/MediaDerivative.cs',
    import.meta.url,
  ),
);

const MEDIA_DTOS = fileURLToPath(
  new URL(
    '../../../../../inference/src/ReelBolt.Inference.Api/Controllers/Dto/MediaDtos.cs',
    import.meta.url,
  ),
);

const backendIsPresent = existsSync(MEDIA_DERIVATIVE_MODEL) && existsSync(MEDIA_DTOS);

/**
 * The member names of `public enum <name>` in C#, in declaration order.
 *
 * Doc comments are stripped first: they mention other enum members by name, and counting a mention
 * as a member would make this guard pass for the wrong reason.
 */
function enumMembers(source: string, name: string): string[] {
  const body = new RegExp(`public enum ${name}\\s*\\{([\\s\\S]*?)\\n\\}`).exec(source)?.[1];
  if (body === undefined) throw new Error(`enum ${name} not found`);

  return body
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => !line.startsWith('//'))
    .map((line) => /^([A-Z][A-Za-z0-9]*)\s*,?$/.exec(line)?.[1])
    .filter((member): member is string => member !== undefined);
}

/** PascalCase member name -> the camelCase wire name JsonNamingPolicy.CamelCase produces. */
const wireName = (member: string): string => member.charAt(0).toLowerCase() + member.slice(1);

describe.skipIf(!backendIsPresent)('media-pool contract vs the landed C#', () => {
  it('finds the backend enums it is supposed to be reading', () => {
    const model = readFileSync(MEDIA_DERIVATIVE_MODEL, 'utf8');
    const dtos = readFileSync(MEDIA_DTOS, 'utf8');
    expect(enumMembers(model, 'MediaDerivativeKind').length).toBeGreaterThan(0);
    expect(enumMembers(dtos, 'MediaKind').length).toBeGreaterThan(0);
    expect(enumMembers(dtos, 'MediaCategory').length).toBeGreaterThan(0);
  });

  it("DERIVATIVE_KINDS equals the landed MediaDerivativeKind, which is EIGHT members", () => {
    const source = readFileSync(MEDIA_DERIVATIVE_MODEL, 'utf8');
    const landed = enumMembers(source, 'MediaDerivativeKind').map(wireName);

    // The count is asserted separately so a future narrowing is reported as a narrowing, not as an
    // opaque set mismatch.
    expect(landed).toHaveLength(8);
    expect(sorted(DERIVATIVE_KINDS)).toEqual(sorted(landed));
  });

  it('MEDIA_KINDS and MEDIA_CATEGORIES equal the landed DTO enums', () => {
    const dtos = readFileSync(MEDIA_DTOS, 'utf8');

    expect(sorted(MEDIA_KINDS)).toEqual(sorted(enumMembers(dtos, 'MediaKind').map(wireName)));
    expect(sorted(MEDIA_CATEGORIES)).toEqual(
      sorted(enumMembers(dtos, 'MediaCategory').map(wireName)),
    );
  });

  it('declares no field of a pool item, derivative row or bin beyond what the DTO sends', () => {
    // The three deltas this guard exists for: the shape is checked, not just the enum spelling.
    const dtos = readFileSync(MEDIA_DTOS, 'utf8');

    expect(dtos).toContain('MediaPoolItemResponse(');
    for (const field of ['MediaRef', 'Kind', 'FileName', 'MimeType', 'SizeBytes', 'Category', 'Probe']) {
      expect(dtos).toContain(field);
    }
    // The landed probe is NON-nullable; the mirror is deliberately wider, and that is recorded.
    expect(dtos).toMatch(/MediaProbeResponse\(\s*double DurationSec/);
  });

  it('sends the move field as binId, matching MoveMediaBinItemRequest', () => {
    // This was the one field NAME the frontend had guessed wrong; pinning it stops the regression.
    const path = fileURLToPath(
      new URL(
        '../../../../../inference/src/ReelBolt.Inference.Api/Controllers/MediaBinsController.cs',
        import.meta.url,
      ),
    );
    if (!existsSync(path)) return;

    const controller = readFileSync(path, 'utf8');
    expect(controller).toContain('MoveMediaBinItemRequest');

    const apiSource = readFileSync(fileURLToPath(new URL('../../api/media.ts', import.meta.url)), 'utf8');
    expect(apiSource).toContain('MoveBinItemRequest = { binId: targetBinId }');
    // ...and the guessed shape it replaced is gone. (Checked as an object-literal opening, since the
    // correct body legitimately mentions targetBinId as the VALUE.)
    expect(apiSource).not.toContain('{ targetBinId');
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// The three closed contract gaps, checked against the landed C# rather than
// against a comment claiming they were closed.
//
// Each of these was a documented frontend gap until the backend landed the read surface: no
// filmstrip index, no way to enumerate a bin's membership, and no probe field behind four of the
// metadata rows. The mirror is only honest while it is compared to the code, so each gap gets a
// guard that FAILS if the C# and the schema diverge — including on the wire shapes the UI now reads.
// ─────────────────────────────────────────────────────────────────────────────

const MEDIA_BINS_CONTROLLER = fileURLToPath(
  new URL(
    '../../../../../inference/src/ReelBolt.Inference.Api/Controllers/MediaBinsController.cs',
    import.meta.url,
  ),
);

const MEDIA_CONTROLLER = fileURLToPath(
  new URL(
    '../../../../../inference/src/ReelBolt.Inference.Api/Controllers/MediaController.cs',
    import.meta.url,
  ),
);

const MEDIA_BIN_DTOS = fileURLToPath(
  new URL(
    '../../../../../inference/src/ReelBolt.Inference.Api/Controllers/Dto/MediaBinDtos.cs',
    import.meta.url,
  ),
);

const readDtos = (): string => readFileSync(MEDIA_DTOS, 'utf8');
const readBinDtos = (): string => readFileSync(MEDIA_BIN_DTOS, 'utf8');

/** The `record` declaration block for a DTO, so a field is asserted inside the right shape. */
function recordBlock(source: string, name: string): string {
  const start = source.indexOf(`record ${name}(`);
  if (start < 0) throw new Error(`record ${name} not found`);
  const end = source.indexOf(');', start);
  return source.slice(start, end < 0 ? source.length : end);
}

describe.skipIf(!backendIsPresent)('the three closed gaps, vs the landed C#', () => {
  it('gap 1: the filmstrip index is a real route, and a sheet carries a URL, not a storage leaf', () => {
    const controller = readFileSync(MEDIA_CONTROLLER, 'utf8');
    const dtos = readDtos();

    // The index route exists, for BOTH mediaRef prefixes.
    expect(controller).toContain('FileMediaRefFilmstripRouteTemplate');
    expect(controller).toContain('StepResultMediaRefFilmstripRouteTemplate');
    // The sheet route, keyed by the owning derivative row plus the sheet's own index.
    expect(controller).toContain('"derivatives/{derivativeId:guid}/sheet/{index:int}/content"');
    // The envelope the frontend destructures: { filmstrip: { densities: [...] } }.
    expect(recordBlock(dtos, 'MediaFilmstripResponse')).toContain('MediaFilmstripDocument Filmstrip');
    expect(recordBlock(dtos, 'MediaFilmstripDocument')).toContain('Densities');
    expect(recordBlock(dtos, 'MediaFilmstripDensityResponse')).toContain('SheetCount');

    const sheet = recordBlock(dtos, 'MediaFilmstripSheetResponse');
    for (const field of ['FirstThumbIndex', 'ThumbCount', 'FirstTimeSec', 'ContentUrl']) {
      expect(sheet).toContain(field);
    }
    // The gap the shape exists to close: a sheet is NOT addressed by a storage leaf, because sheets
    // 1..N-1 have no media_derivatives row of their own to resolve one against.
    expect(sheet).not.toContain('StorageLeaf');
    expect(sheet).not.toContain('StorageKey');
  });

  it('gap 1: the schema mirror requires exactly the sheet and density fields the DTO sends', () => {
    expect(sorted(schema.$defs.MediaFilmstripSheet?.required ?? [])).toEqual(
      sorted(['firstThumbIndex', 'thumbCount', 'firstTimeSec', 'contentUrl']),
    );
    expect(sorted(schema.$defs.MediaFilmstripDensity?.required ?? [])).toEqual(
      sorted([
        'densitySec',
        'thumbWidth',
        'thumbHeight',
        'cols',
        'rows',
        'sheetCount',
        'sheets',
      ]),
    );
    // A storage key can never arrive on this shape, which is why the mirror has no field for one.
    for (const forbidden of ['storageKey', 'storageLeaf', 'storageBucket', 'url']) {
      expect(Object.keys(schema.$defs.MediaFilmstripSheet?.properties ?? {})).not.toContain(forbidden);
    }
  });

  it('gap 2: a bin can be enumerated, and an item can be removed', () => {
    const controller = readFileSync(MEDIA_BINS_CONTROLLER, 'utf8');

    // The listing route, and the item-scoped DELETE that needs the itemId it returns.
    expect(controller).toContain('[HttpGet("{binId:guid}/items")]');
    expect(controller).toContain('[HttpDelete("{binId:guid}/items/{itemId:guid}")]');

    const listing = recordBlock(readBinDtos(), 'MediaBinItemDetailResponse');
    for (const field of ['Id', 'BinId', 'MediaRef', 'SourceKind', 'SortOrder', 'CreatedAt']) {
      expect(listing).toContain(field);
    }
    // The four resolution fields, all nullable for a dangling pin.
    for (const field of ['Kind', 'FileName', 'MimeType', 'SizeBytes']) {
      expect(listing).toContain(field);
    }
    // ...and deliberately no `probe`: the pool is the single producer of that value.
    expect(listing).not.toContain('Probe');
  });

  it('gap 2: the pool item carries its own pins, which is what a derived count reads', () => {
    const pool = recordBlock(readDtos(), 'MediaPoolItemResponse');
    expect(pool).toContain('MediaBinMembershipResponse');
    expect(pool).toContain('FilmstripUrl');

    const membership = recordBlock(readDtos(), 'MediaBinMembershipResponse');
    expect(membership).toContain('Guid BinId');
    expect(membership).toContain('Guid ItemId');

    expect(sorted(schema.$defs.MediaBinMembership?.required ?? [])).toEqual(
      sorted(['binId', 'itemId']),
    );
    expect(sorted(schema.$defs.MediaBinItemDetail?.required ?? [])).toEqual(
      sorted([
        'id',
        'binId',
        'mediaRef',
        'sourceKind',
        'sortOrder',
        'createdAt',
        'kind',
        'fileName',
        'mimeType',
        'sizeBytes',
      ]),
    );
  });

  it('gap 3: the probe carries the six appended fields, nullable and never defaulted', () => {
    const probe = recordBlock(readDtos(), 'MediaProbeResponse');

    for (const field of [
      'VideoCodec',
      'AudioCodec',
      'Container',
      'AudioChannels',
      'AudioSampleRateHz',
      'BitrateBps',
    ]) {
      expect(probe).toContain(field);
    }
    // Nullable on the wire, which is what makes "not probed" a real state rather than a zero.
    for (const declaration of [
      'string? VideoCodec',
      'string? AudioCodec',
      'string? Container',
      'int? AudioChannels',
      'int? AudioSampleRateHz',
      'long? BitrateBps',
    ]) {
      expect(probe).toContain(declaration);
    }

    // The mirror requires all eleven, so a client cannot render a row that silently vanishes.
    expect(sorted(schema.$defs.MediaProbe?.required ?? [])).toEqual(
      sorted([
        'durationSec',
        'fpsNum',
        'fpsDen',
        'width',
        'height',
        'videoCodec',
        'audioCodec',
        'container',
        'audioChannels',
        'audioSampleRateHz',
        'bitrateBps',
      ]),
    );
  });

  it('gap 3: the container is ffprobe format_name verbatim, not a normalized name', () => {
    const probe = recordBlock(readDtos(), 'MediaProbeResponse');
    // The DTO documents the sniffed-MP4 probe list explicitly; the mirror must not contradict it.
    expect(probe).toContain('mov,mp4,m4a,3gp,3g2,mj2');

    const schemaContainer = schema.$defs.MediaProbe?.properties?.container ?? {};
    expect((schemaContainer.description ?? '').toLowerCase()).toContain('verbatim');
    expect(schemaContainer.enum).toBeUndefined();
  });

  it('records the derivative-request write the UI is allowed to call on an explicit open', () => {
    const controller = readFileSync(MEDIA_CONTROLLER, 'utf8');
    const dtos = readDtos();

    expect(controller).toContain('[HttpPost("derivatives/request")]');
    expect(recordBlock(dtos, 'RequestMediaDerivativesRequest')).toContain('string MediaRef');
    // The reason vocabulary the frontend sends, and the server's own default for it.
    expect(dtos).toContain('RequestedByEditorOpen = "editor-open"');
    expect(dtos).toContain('RequestedByUpload = "upload"');
    expect(dtos).toContain('RequestedByRenderOutput = "render-output"');

    const accepted = schema.$defs.RequestMediaDerivativesResponse;
    expect(sorted(accepted?.required ?? [])).toEqual(sorted(['mediaRef', 'kinds', 'requestedBy']));
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// The schema, read relatively from this file — never from process.cwd(), so the
// guard does not depend on where the runner was invoked from.
// ─────────────────────────────────────────────────────────────────────────────

const SCHEMA_PATH = fileURLToPath(new URL('../media-pool.schema.json', import.meta.url));

/** The subset of JSON Schema this guard reads. */
type Subschema = {
  enum?: readonly string[];
  pattern?: string;
  description?: string;
  required?: readonly string[];
  properties?: Record<string, Subschema>;
};

type MediaSchema = { $defs: Record<string, Subschema> };

const schema = JSON.parse(readFileSync(SCHEMA_PATH, 'utf8')) as MediaSchema & { $id: string };

const SCHEMA_ID = schema.$id;

// ─────────────────────────────────────────────────────────────────────────────
// Compile-time half of the drift guard.
//
// These three annotations fail `npx tsc --noEmit` if a literal in one of the const arrays is not a
// real member of the union it mirrors; `allKindsCovered` is `true` only when the arrays between them
// cover each union with nothing left over.
// ─────────────────────────────────────────────────────────────────────────────

const kindsAreMediaKinds: readonly MediaKind[] = MEDIA_KINDS;
const categoriesAreMediaCategories: readonly MediaCategory[] = MEDIA_CATEGORIES;
const derivativeKindsAreDerivativeKinds: readonly MediaDerivativeKind[] = DERIVATIVE_KINDS;

type UncoveredMediaKind = Exclude<MediaKind, (typeof MEDIA_KINDS)[number]>;
type UncoveredMediaCategory = Exclude<MediaCategory, (typeof MEDIA_CATEGORIES)[number]>;
type UncoveredDerivativeKind = Exclude<MediaDerivativeKind, (typeof DERIVATIVE_KINDS)[number]>;

const allKindsCovered: [
  UncoveredMediaKind extends never ? true : false,
  UncoveredMediaCategory extends never ? true : false,
  UncoveredDerivativeKind extends never ? true : false,
] = [true, true, true];

const sorted = (values: Iterable<string>): string[] => [...values].sort();

const enumOf = (def: string): string[] => sorted(schema.$defs[def]?.enum ?? []);

// ─────────────────────────────────────────────────────────────────────────────
// ajv
// ─────────────────────────────────────────────────────────────────────────────

// The schema is draft 2020-12 (hence Ajv2020); `strict: false` lets it compile without pulling in
// ajv-formats or editing the schema. It is NOT what makes the negative fixtures fail — that is
// asserted below, keyword by keyword.
const ajv = new Ajv2020({ strict: false, allErrors: true });

// Registered once under its own `$id`, then addressed by JSON pointer: compiling the same document
// three times over would collide on that `$id`, and a per-definition copy would let the three
// validators drift apart.
ajv.addSchema(schema);

function getValidator(def: string) {
  const validate = ajv.getSchema(`${SCHEMA_ID}#/$defs/${def}`);
  if (!validate) throw new Error(`ajv resolved no validator for $defs.${def}`);
  return validate;
}

const VALIDATORS = {
  MediaPoolList: getValidator('MediaPoolList'),
  MediaDerivativeList: getValidator('MediaDerivativeList'),
  MediaBinList: getValidator('MediaBinList'),
  // Added when the three landed contract gaps were closed: the fixture manifest now covers the
  // shapes those gaps were about, so each one gets a real validator rather than a happy-path claim.
  MediaBinItemDetailList: getValidator('MediaBinItemDetailList'),
  MediaFilmstripResponse: getValidator('MediaFilmstripResponse'),
  RequestMediaDerivativesResponse: getValidator('RequestMediaDerivativesResponse'),
} as const;

type DefName = keyof typeof VALIDATORS;

const errorsFor = (def: DefName, data: unknown) => {
  const validate = VALIDATORS[def];
  const valid = validate(data);
  return { valid, errors: validate.errors ?? [] };
};

// ─────────────────────────────────────────────────────────────────────────────
// The guard
// ─────────────────────────────────────────────────────────────────────────────

describe('media-pool schema drift', () => {
  it('resolves the schema relatively, not from process.cwd()', () => {
    expect(SCHEMA_PATH.endsWith('web/lib/editing/media/media-pool.schema.json')).toBe(true);
    expect(SCHEMA_PATH.startsWith('/')).toBe(true);
    expect(schema.$defs.MediaPoolItem).toBeDefined();
    expect(schema.$defs.MediaDerivativeRow).toBeDefined();
    expect(schema.$defs.MediaBin).toBeDefined();
  });

  it('compiled every definition, so a negative case below cannot pass for the wrong reason', () => {
    for (const validate of Object.values(VALIDATORS)) expect(typeof validate).toBe('function');
    expect(ajv.errors ?? []).toEqual([]);
  });

  it("matches MEDIA_KINDS to the schema's MediaKind enum exactly, both directions", () => {
    expect(kindsAreMediaKinds).toEqual([...MEDIA_KINDS]);
    expect(sorted(MEDIA_KINDS)).toEqual(enumOf('MediaKind'));
  });

  it("matches MEDIA_CATEGORIES to the schema's MediaCategory enum exactly, both directions", () => {
    expect(categoriesAreMediaCategories).toEqual([...MEDIA_CATEGORIES]);
    expect(sorted(MEDIA_CATEGORIES)).toEqual(enumOf('MediaCategory'));
  });

  it("matches DERIVATIVE_KINDS to the schema's MediaDerivativeKind enum exactly, both directions", () => {
    expect(derivativeKindsAreDerivativeKinds).toEqual([...DERIVATIVE_KINDS]);
    expect(sorted(DERIVATIVE_KINDS)).toEqual(enumOf('MediaDerivativeKind'));
  });

  it('covers every union with nothing left over, and with no literal in two lists', () => {
    expect(allKindsCovered).toEqual([true, true, true]);
    expect(sorted(MEDIA_KINDS).filter((k) => (DERIVATIVE_KINDS as readonly string[]).includes(k))).toEqual([]);
  });

  it('agrees on the opaque mediaRef prefixes with the schema pattern, both directions', () => {
    const pattern = schema.$defs.MediaRef?.pattern ?? '';
    expect(pattern.length).toBeGreaterThan(0);

    // The pattern is `^(file|step_result):<36 guid chars>$`; pull the alternation out of it rather
    // than re-stating it, so the two cannot drift apart.
    const alternatives = /^\^\(([^)]+)\)/.exec(pattern)?.[1]?.split('|') ?? [];
    expect(sorted(alternatives)).toEqual(sorted(MEDIA_REF_PREFIXES));
  });

  it('has isMediaRef accept exactly what the schema pattern accepts, for both prefixes', () => {
    const pattern = new RegExp(schema.$defs.MediaRef?.pattern ?? '$^');

    const samples = [
      ...MEDIA_REF_PREFIXES.map((prefix) => `${prefix}:00000000-0000-4000-8000-000000000101`),
      '00000000-0000-4000-8000-000000000101',
      'file:not-a-guid',
      'folder:00000000-0000-4000-8000-000000000101',
      '',
      'file:00000000-0000-4000-8000-000000000101 ',
    ];

    for (const sample of samples) {
      expect(isMediaRef(sample)).toBe(pattern.test(sample));
    }

    // And the two prefixes really are both accepted — a guard that only allowed `file:` would pass
    // every assertion above while silently excluding every step-result render.
    for (const prefix of MEDIA_REF_PREFIXES) {
      expect(isMediaRef(`${prefix}:00000000-0000-4000-8000-000000000101`)).toBe(true);
    }
    expect(isMediaRef('00000000-0000-4000-8000-000000000101')).toBe(false);
  });

  it('keeps the mediaRef and probe fields required on a pool item, and probe nullable', () => {
    const item = schema.$defs.MediaPoolItem;
    expect(sorted(item?.required ?? [])).toEqual(
      sorted([
        'mediaRef',
        'kind',
        'fileName',
        'mimeType',
        'sizeBytes',
        'category',
        'probe',
        // Appended by the landed MediaPoolItemResponse so the panel's follow-ups are addressable
        // from the payload it already holds. Required in the mirror: a client that tolerated their
        // absence would render a bin count of zero and a filmstrip it cannot fetch.
        'bins',
        'filmstripUrl',
      ]),
    );

    const probe = item?.properties?.probe as { oneOf?: { type?: string }[] } | undefined;
    expect(probe?.oneOf?.some((branch) => branch.type === 'null')).toBe(true);
  });

  it('carries no storage key, bucket or object URL on any response shape', () => {
    const forbidden = ['storageKey', 'storageBucket', 'bucket', 'objectUrl', 'url'];
    const responseDefs = [
      'MediaPoolItem',
      'MediaProbe',
      'MediaDerivativeRow',
      'MediaBin',
      'MediaRef',
    ];

    for (const def of responseDefs) {
      const properties = Object.keys(schema.$defs[def]?.properties ?? {});
      for (const name of forbidden) expect(properties).not.toContain(name);
    }
  });

  it('documents that a derivative row has no state field', () => {
    // The absence is a contract fact the whole UI rests on, so it is pinned rather than assumed.
    expect(Object.keys(schema.$defs.MediaDerivativeRow?.properties ?? {})).not.toContain('state');
  });
});

describe('media-pool fixtures under ajv', () => {
  it.each(VALID_FIXTURES.map((fixture) => [fixture.name, fixture] as const))(
    'accepts: %s',
    (_name, fixture) => {
      const { valid, errors } = errorsFor(fixture.def, fixture.data);
      expect(errors).toEqual([]);
      expect(valid).toBe(true);
    },
  );

  it.each(INVALID_FIXTURES.map((fixture) => [fixture.name, fixture] as const))(
    'rejects: %s',
    (_name, fixture) => {
      const { valid } = errorsFor(fixture.def, fixture.data);
      expect(valid).toBe(false);
    },
  );

  it('rejects a bare-GUID mediaRef for exactly that reason', () => {
    const fixture = INVALID_FIXTURES.find((f) => f.name.includes('bare GUID'));
    expect(fixture).toBeDefined();

    const { errors } = errorsFor(fixture!.def, fixture!.data);
    const reason = errors.find((e) => e.keyword === 'pattern');
    expect(reason).toBeDefined();
    expect(reason?.instancePath).toBe('/0/mediaRef');
    expect(errors.every((e) => e.keyword !== 'schema')).toBe(true);
  });

  it('rejects an unknown derivative kind on the discriminator, not on the schema', () => {
    const fixture = INVALID_FIXTURES.find((f) => f.name.includes('unknown derivative kind'));
    expect(fixture).toBeDefined();

    const { errors } = errorsFor(fixture!.def, fixture!.data);
    const reason = errors.find((e) => e.keyword === 'enum');
    expect(reason).toBeDefined();
    expect(reason?.instancePath).toBe('/0/kind');
  });

  it('rejects a bin with no itemCount because itemCount is required', () => {
    const fixture = INVALID_FIXTURES.find((f) => f.name.includes('no itemCount'));
    expect(fixture).toBeDefined();

    const { errors } = errorsFor(fixture!.def, fixture!.data);
    const reason = errors.find(
      (e) =>
        e.keyword === 'required' &&
        (e.params as { missingProperty?: string }).missingProperty === 'itemCount',
    );
    expect(reason).toBeDefined();
  });
});
