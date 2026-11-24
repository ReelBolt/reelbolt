/** @vitest-environment node */
/**
 * Drift guard for the thumbnail editor's hand-written enum and union mirrors.
 *
 * Three vocabularies cross the wire as strings and are mirrored by hand in
 * `web/lib/types/image-document.ts`:
 *
 *   - the layer `kind` (C# `EditImageLayerKind`, persisted by member name, append-only);
 *   - the layer `blendMode` (the contract's list, validated server-side);
 *   - the `operation` of `POST …/operations` (the contract's section 5 table).
 *
 * The first is compared with the C# enum itself. The other two have no C# home in this tree yet,
 * so they are compared with the contract (docs/design/thumbnail-editor.md), which is the document
 * both work packages were built from. Either way something executes an assertion against the
 * source, which is the only thing that keeps a hand-written mirror honest.
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  BLEND_MODES,
  EDIT_IMAGE_LAYER_KINDS,
  EDIT_IMAGE_OPERATIONS,
  EXPORT_CONTENT_TYPES,
  parseLayerKind,
  type BlendMode,
  type EditImageLayerKind,
  type EditImageOperation,
} from '../image-document';
import { EXPORT_FORMATS } from '../../editor/export-formats';

const read = (relative: string) => readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');

// Compile-time half: each list covers its union with nothing left over.
type Missing<T, L extends readonly T[]> = Exclude<T, L[number]>;
const kindsCovered: Missing<EditImageLayerKind, typeof EDIT_IMAGE_LAYER_KINDS> extends never ? true : false = true;
const blendCovered: Missing<BlendMode, typeof BLEND_MODES> extends never ? true : false = true;
const operationsCovered: Missing<EditImageOperation, typeof EDIT_IMAGE_OPERATIONS> extends never ? true : false = true;

describe('layer kind mirror', () => {
  const csharp = read('../../../../inference/src/ReelBolt.Shared/Data/Models/EditImageDocument.cs');
  const body = /enum\s+EditImageLayerKind\s*\{([\s\S]*?)\n\}/.exec(csharp)?.[1] ?? '';
  const members = [...body.replace(/\/\/\/.*$/gm, '').matchAll(/^\s*([A-Z][A-Za-z]*)\s*,?\s*$/gm)].map((m) => m[1]);

  it('found the C# enum', () => {
    expect(kindsCovered).toBe(true);
    expect(members.length).toBeGreaterThanOrEqual(2);
  });

  it('lists the C# members first and in the same order (the enum is append-only)', () => {
    expect(EDIT_IMAGE_LAYER_KINDS.slice(0, members.length)).toEqual(members.map((m) => m.toLowerCase()));
  });

  it('names exactly the five kinds of the contract, in order', () => {
    expect([...EDIT_IMAGE_LAYER_KINDS]).toEqual(['source', 'cutout', 'generated', 'text', 'image']);
  });

  it('reads the server\'s PascalCase and the web\'s lower-case alike', () => {
    for (const kind of EDIT_IMAGE_LAYER_KINDS) {
      expect(parseLayerKind(kind)).toBe(kind);
      expect(parseLayerKind(kind[0].toUpperCase() + kind.slice(1))).toBe(kind);
    }
  });

  it('reads an unknown or missing kind as a plain image layer instead of throwing', () => {
    expect(parseLayerKind('Hologram')).toBe('image');
    expect(parseLayerKind(null)).toBe('image');
    expect(parseLayerKind(undefined)).toBe('image');
    expect(parseLayerKind('')).toBe('image');
  });
});

describe('contract mirror', () => {
  const contract = read('../../../../docs/design/thumbnail-editor.md');

  it('blend modes equal the contract\'s list', () => {
    expect(blendCovered).toBe(true);
    const line = contract.split('\n').find((l) => l.includes('`blend_mode`')) ?? '';
    const names = [...line.matchAll(/`([a-z-]+)`/g)]
      .map((m) => m[1])
      .filter((n) => n !== 'blend_mode' && n !== 'source-over');
    expect(names.length).toBeGreaterThan(5);
    expect([...BLEND_MODES].sort()).toEqual([...new Set(names)].sort());
  });

  it('operations equal the first column of the contract\'s section 5 table', () => {
    expect(operationsCovered).toBe(true);
    const section = contract.split('## 5. Operations')[1]?.split('\n## ')[0] ?? '';
    const names = [...section.matchAll(/^\| `([A-Za-z]+)` \|/gm)].map((m) => m[1]).filter((n) => n !== 'operation');
    expect(names.length).toBeGreaterThan(5);
    expect([...EDIT_IMAGE_OPERATIONS]).toEqual(names);
  });
});

describe('export content types mirror', () => {
  const csharp = read('../../../../inference/src/ReelBolt.Inference.Api/Controllers/EditImageDocumentSupport.cs');
  const block = /ExportFormats\s*=[\s\S]*?\{([\s\S]*?)\};/.exec(csharp)?.[1] ?? '';
  const server = [...block.matchAll(/\["([^"]+)"\]\s*=\s*"(\.[a-z]+)"/g)].map((m) => ({ type: m[1], extension: m[2] }));

  it('found the server table', () => {
    expect(server.length).toBeGreaterThanOrEqual(2);
  });

  it('offers exactly the content types the server accepts for an export', () => {
    expect([...EXPORT_CONTENT_TYPES].sort()).toEqual(server.map((s) => s.type).sort());
  });

  it('names each format with the extension the server gives its file', () => {
    for (const format of EXPORT_FORMATS) {
      expect(server.find((s) => s.type === format.mime)?.extension).toBe(format.extension);
    }
    expect(EXPORT_FORMATS.map((f) => f.mime).sort()).toEqual([...EXPORT_CONTENT_TYPES].sort());
  });
});
