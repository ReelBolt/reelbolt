/** @vitest-environment node */
/**
 * Drift guard for the hand-written mirror of the two inference-provider enums.
 *
 * `inference/src/ReelBolt.Shared/Data/Models/Enums.cs` is the authority for both
 * `InferenceProviderKind` and `InferenceProviderCapability`; `web/lib/types/inference-provider.ts` is
 * a hand-written TypeScript mirror of them, and a hand-written mirror only stays honest if something
 * executes an assertion against the source of truth. Nothing did: a member appended in C# compiled
 * fine on both sides and reached the admin form as a value its own Capability <Select> could not
 * offer. This file is that assertion.
 *
 * Two halves, deliberately, because each catches a failure the other cannot:
 *   1. COMPILE TIME, through `npx tsc --noEmit` — the same gate CI runs. The two literal lists below
 *      are `satisfies`-checked against the unions, and the two `Exclude` collapses are `true` only
 *      when those lists cover their union completely. A member added to the TypeScript union without
 *      being mirrored here therefore fails the type-check, even though the runtime half below would
 *      have no C# member to compare it against.
 *   2. RUNTIME, in this file. It parses the real C# enums out of Enums.cs and compares them, as sets,
 *      with those same two lists — which is what fails when someone appends an enum member in C#
 *      without touching the web union.
 *
 * Parsing the file was chosen over a second hand-maintained expectation array on purpose: a
 * hand-copied copy of an enum is one more thing that goes out of step with the same edit, which is
 * the exact failure this guard exists to prevent. Parsing is brittle in one specific way — the doc
 * comments in Enums.cs carry prose AND braces, one of them literally `{id, polling_url}` — so every
 * comment is stripped before a single brace is counted, and the parse is then asserted to have found
 * a non-trivial, identifier-shaped member set, so that neither comparison below can pass vacuously.
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import type { InferenceProviderCapability, InferenceProviderKind } from '../inference-provider';

// ─────────────────────────────────────────────────────────────────────────────
// Read the source of truth relatively from this file, never from process.cwd(),
// so the guard does not depend on where the runner was invoked from.
// ─────────────────────────────────────────────────────────────────────────────

const ENUMS_PATH = fileURLToPath(
  new URL('../../../../inference/src/ReelBolt.Shared/Data/Models/Enums.cs', import.meta.url),
);

const source = readFileSync(ENUMS_PATH, 'utf8');

// ─────────────────────────────────────────────────────────────────────────────
// Compile-time half: the only place these two lists are written down, each
// type-checked against its union by `npx tsc --noEmit`.
//   - `satisfies` rejects a literal that is not a member of the union;
//   - each collapse is `true` only when its list covers the union with nothing
//     left over, so a union member missing from the list is a type error.
// The runtime half then proves those same two lists equal the C# enums.
// ─────────────────────────────────────────────────────────────────────────────

const MIRRORED_KINDS = [
  'AzureOpenAI',
  'OpenAICompatible',
  'Anthropic',
  'Gemini',
  'DeepSeek',
  'TypeSafe',
  'MiniMax',
  'FishAudio',
  'BlackForestLabs',
  'FalAi',
  'Higgsfield',
  'ComfyUI',
  'ElevenLabs',
  'LocalRunner',
] as const satisfies readonly InferenceProviderKind[];

const MIRRORED_CAPABILITIES = [
  'Chat',
  'Transcription',
  'Vision',
  'Decision',
  'VideoGeneration',
  'SpeechSynthesis',
  'Embedding',
  'ImageGeneration',
  'ImageEdit',
  'Segmentation',
  'Depth',
  'ImageErase',
  'ImageFill',
  'ImageExpand',
  'ImageBackgroundReplace',
  'ImageRelight',
  'ImageUpscale',
  'Compute',
  'MusicGeneration',
  'SoundEffectGeneration',
  'VideoSegmentation',
  'ContentModeration',
  'Rerank',
  'WebSearch',
  'AudioIsolation',
  'VideoUpscale',
  'FrameInterpolation',
  'VideoOutpaint',
  'LipSync',
] as const satisfies readonly InferenceProviderCapability[];


type UnmirroredKind = Exclude<InferenceProviderKind, (typeof MIRRORED_KINDS)[number]>;
const allKindsMirrored: UnmirroredKind extends never ? true : false = true;

type UnmirroredCapability = Exclude<InferenceProviderCapability, (typeof MIRRORED_CAPABILITIES)[number]>;
const allCapabilitiesMirrored: UnmirroredCapability extends never ? true : false = true;

/**
 * The one declared asymmetry between the C# enum and the TypeScript kind union, carried forward from
 * `lib/types/rate-card.ts`, which says of it: "the backend enum also carries `OpenJev`, which this
 * union's home predates because no provider *form* offers it yet". The kind union is what the admin
 * form can submit, so it deliberately stops short of a kind no form offers; the billing vocabulary
 * widens it back there.
 *
 * It is named here — rather than the kind comparison being loosened into a subset check — so that a
 * genuinely new C# kind is still a failure, which is the whole point of this file.
 */
const KINDS_NOT_IN_THE_UNION = ['OpenJev'] as const;

/**
 * Capability members appended to the TypeScript union by a web change that lands alongside the C#
 * change appending them (the model catalog, design docs/design/model-catalog.md §2.3: `Compute`).
 * Each may be ABSENT from Enums.cs only while that backend change is unmerged on this branch; once
 * the C# member exists the comparison below is exact again, and it must then be the LAST member,
 * because the enum is append-only and persisted by name. Remove an entry once both sides have it.
 */
const CAPABILITIES_PENDING_IN_CSHARP: readonly InferenceProviderCapability[] = [];

// ─────────────────────────────────────────────────────────────────────────────
// The C# side
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Enums.cs with every comment removed — block comments first, because a doc comment that happens to
 * sit on one line would otherwise be only half-removed by the line filter below.
 */
const stripComments = (text: string): string =>
  text
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .split('\n')
    .filter((line) => !/^\s*\/\//.test(line))
    .join('\n');

const stripped = stripComments(source);

/**
 * The members of the C# enum `name`, in declaration order.
 *
 * The body is located by counting braces from the declaration's own opening brace, which is only
 * sound because every comment has already been stripped. Explicit values (`Member = 1`) are
 * tolerated even though neither enum uses them, so an assignment can never be read as a member name.
 */
function csharpEnumMembers(name: string): string[] {
  // Matched by plain index rather than a regular expression so the lookup needs no escaping, with the
  // surrounding characters checked instead: `enum Foo` must not be a prefix of `enum FooBar`.
  const marker = `enum ${name}`;
  const declaration = stripped.indexOf(marker);
  if (declaration === -1) throw new Error(`${name} not found in Enums.cs`);

  const preceding = stripped[declaration - 1];
  const following = stripped[declaration + marker.length];
  if (preceding !== undefined && !/\s/.test(preceding)) throw new Error(`${marker} matched mid-identifier`);
  if (following !== undefined && !/\s/.test(following)) throw new Error(`${marker} matched a longer name`);

  const open = stripped.indexOf('{', declaration + marker.length);
  if (open === -1) throw new Error(`enum ${name} has no body`);

  let depth = 0;
  let close = -1;
  for (let i = open; i < stripped.length; i += 1) {
    if (stripped[i] === '{') depth += 1;
    else if (stripped[i] === '}') {
      depth -= 1;
      if (depth === 0) {
        close = i;
        break;
      }
    }
  }
  if (close === -1) throw new Error(`enum ${name} body is unterminated`);

  return stripped
    .slice(open + 1, close)
    .split(',')
    .map((member) => member.split('=')[0].trim())
    .filter((member) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(member));
}

const csharpKinds = csharpEnumMembers('InferenceProviderKind');
const csharpCapabilities = csharpEnumMembers('InferenceProviderCapability');

const sorted = (values: Iterable<string>): string[] => [...values].sort();

// ─────────────────────────────────────────────────────────────────────────────
// The assertions
// ─────────────────────────────────────────────────────────────────────────────

describe('InferenceProvider enum drift', () => {
  it('resolves Enums.cs relatively, not from process.cwd()', () => {
    expect(ENUMS_PATH.endsWith('inference/src/ReelBolt.Shared/Data/Models/Enums.cs')).toBe(true);
    expect(ENUMS_PATH.startsWith('/')).toBe(true);
    // The comment stripping this file depends on actually happened.
    expect(source).toContain('<summary>');
    expect(stripped).not.toContain('<summary>');
    expect(stripped).not.toContain('///');
  });

  it('parses both enums non-trivially, so neither comparison below can pass vacuously', () => {
    expect(csharpKinds.length).toBeGreaterThan(5);
    expect(csharpCapabilities.length).toBeGreaterThan(5);

    // Spot-checks of members that have been in the file far longer than this guard: a parser that
    // quietly matched the wrong text would drop these too.
    expect(csharpKinds).toContain('AzureOpenAI');
    expect(csharpKinds).toContain('MiniMax');
    expect(csharpCapabilities).toContain('Chat');
    expect(csharpCapabilities).toContain('Embedding');

    // Identifiers only — no leftover prose, braces or comments where a member should be.
    for (const member of [...csharpKinds, ...csharpCapabilities]) {
      expect(member).toMatch(/^[A-Za-z_][A-Za-z0-9_]*$/);
    }
  });

  it("mirrors every InferenceProviderKind member of the C# enum, both directions", () => {
    expect(sorted([...MIRRORED_KINDS, ...KINDS_NOT_IN_THE_UNION])).toEqual(sorted(csharpKinds));
  });

  it('mirrors every InferenceProviderCapability member of the C# enum, both directions', () => {
    const pending = CAPABILITIES_PENDING_IN_CSHARP as readonly string[];
    // A pending member is excused only while C# lacks it; every other member must match exactly.
    const expected = MIRRORED_CAPABILITIES.filter((c) => !pending.includes(c) || csharpCapabilities.includes(c));
    expect(sorted(expected)).toEqual(sorted(csharpCapabilities));
  });

  it('appends each pending capability last, on both sides, never in the middle', () => {
    for (const capability of CAPABILITIES_PENDING_IN_CSHARP) {
      expect(MIRRORED_CAPABILITIES[MIRRORED_CAPABILITIES.length - 1]).toBe(capability);
      if (csharpCapabilities.includes(capability)) {
        expect(csharpCapabilities[csharpCapabilities.length - 1]).toBe(capability);
      }
    }
  });


  it('covers both TypeScript unions with nothing left over (the compile-time half)', () => {
    expect(allKindsMirrored).toBe(true);
    expect(allCapabilitiesMirrored).toBe(true);
  });

  it('keeps each declared kind exception out of the mirrored list', () => {
    for (const kind of KINDS_NOT_IN_THE_UNION) {
      expect(MIRRORED_KINDS as readonly string[]).not.toContain(kind);
    }
  });
});
