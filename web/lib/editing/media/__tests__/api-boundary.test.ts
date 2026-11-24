/** @vitest-environment node */
/**
 * The API boundary, asserted rather than reviewed.
 *
 * The ticket requires that `web/lib/editing/api/media.ts` be the ONLY module knowing the wire shape,
 * and that no component call `fetch` directly. Both claims are checked here by reading the actual
 * source files — an assertion that cannot silently pass because somebody added a `fetch` to a
 * component.
 *
 * Three properties:
 *   1. No bare `fetch(` call anywhere in the media feature or the media hook.
 *   2. No `api/v1/...` URL literal outside the media API client.
 *   3. No response field the contract forbids — `storageKey`, `storageBucket`, `objectUrl` — is read
 *      anywhere in the feature, so an accidental leak in a response can never reach the UI.
 */

import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

const webRoot = fileURLToPath(new URL('../../../../', import.meta.url));
const apiModulePath = fileURLToPath(new URL('../../api/media.ts', import.meta.url));

const COMPONENTS_DIR = fileURLToPath(new URL('../../../../components/editor/media', import.meta.url));
const MEDIA_LIB_DIR = path.resolve(fileURLToPath(new URL('..', import.meta.url)));

/** `fetch(` not preceded by an identifier char — so `apiFetch(` and `prefetch(` do not count. */
const BARE_FETCH = /(?<![\w.$])fetch\s*\(/;

function sourceFiles(dir: string): string[] {
  const entries = readdirSync(dir);
  const files: string[] = [];

  for (const entry of entries) {
    const full = `${dir}/${entry}`;
    if (statSync(full).isDirectory()) {
      // Tests are allowed to read the source; they are not shipped code.
      if (entry === '__tests__') continue;
      files.push(...sourceFiles(full));
      continue;
    }
    if (entry.endsWith('.ts') || entry.endsWith('.tsx')) files.push(full);
  }

  return files;
}

/** A repo-relative path with no doubled separators, for stable assertions and messages. */
const relative = (file: string) => path.relative(webRoot, file);

const shippedMediaSources = [
  ...sourceFiles(COMPONENTS_DIR),
  ...sourceFiles(MEDIA_LIB_DIR).filter((file) => !file.endsWith('media-pool.schema.json')),
  fileURLToPath(new URL('../../../hooks/use-media.ts', import.meta.url)),
  // The route is part of the media surface too, so it is held to the same rule.
  fileURLToPath(new URL('../../../../app/(app)/editor/[id]/media/page.tsx', import.meta.url)),
];

describe('the media API client is the only module that knows the wire shape', () => {
  it('finds the shipped media sources it is supposed to be scanning', () => {
    // A glob that silently matched nothing would make every assertion below vacuous.
    expect(shippedMediaSources.length).toBeGreaterThanOrEqual(10);
    expect(shippedMediaSources.map(relative)).toContain('components/editor/media/MediaPool.tsx');
    expect(shippedMediaSources.map(relative)).toContain('lib/editing/media/bins.ts');
    expect(shippedMediaSources.map(relative)).toContain('lib/hooks/use-media.ts');
    expect(shippedMediaSources.map(relative)).toContain('app/(app)/editor/[id]/media/page.tsx');
  });

  it('has no bare fetch() call in any component or hook', () => {
    const offenders: string[] = [];

    for (const file of shippedMediaSources) {
      const source = readFileSync(file, 'utf8');
      if (BARE_FETCH.test(source)) offenders.push(relative(file));
    }

    expect(offenders).toEqual([]);
  });

  it('spells no api/v1 URL outside the media API client', () => {
    const offenders: string[] = [];

    for (const file of shippedMediaSources) {
      if (file === apiModulePath) continue;
      const source = readFileSync(file, 'utf8');
      if (source.includes('api/v1/')) offenders.push(relative(file));
    }

    expect(offenders).toEqual([]);
  });

  it('routes every call in the API client itself through apiFetch or fetcher', () => {
    const source = readFileSync(apiModulePath, 'utf8');

    expect(BARE_FETCH.test(source)).toBe(false);
    expect(source).toContain("from '../../api/client'");
    expect(source).toContain('apiFetch');
    expect(source).toContain('fetcher');
    // The paths are built here and nowhere else.
    expect(source).toContain('api/v1/projects');
  });

  it('exports the whole read and write surface the ticket names', () => {
    const source = readFileSync(apiModulePath, 'utf8');

    for (const name of [
      'fetchMediaPool',
      'fetchMediaDerivatives',
      'fetchMediaBins',
      'createBin',
      'updateBin',
      'deleteBin',
      'addBinItem',
      'moveBinItem',
      'removeBinItem',
      'derivativeContentUrl',
    ]) {
      // Either declaration form is fine; what matters is that the name is exported.
      expect(
        source.includes(`export const ${name}`) || source.includes(`export function ${name}`),
      ).toBe(true);
    }
  });

  it('never reads a storage key, a bucket or an object URL', () => {
    const forbidden = ['storageKey', 'storageBucket', 'objectUrl'];
    const offenders: string[] = [];

    for (const file of shippedMediaSources) {
      const source = readFileSync(file, 'utf8');
      for (const field of forbidden) {
        if (source.includes(field)) offenders.push(`${relative(file)}: ${field}`);
      }
    }

    expect(offenders).toEqual([]);
  });

  it('never parses a GUID out of a mediaRef', () => {
    // A component or helper that extracts the GUID is exactly how the two prefixes get collapsed.
    // (`isMediaRef` and the fixtures may VALIDATE the token shape; nothing may destructure it.)
    const guidExtraction = [
      /mediaRef\.(split|slice|substring|replace|match)\s*\(/,
      /mediaRef\.substring/,
    ];
    const offenders: string[] = [];

    for (const file of shippedMediaSources) {
      const source = readFileSync(file, 'utf8');
      if (guidExtraction.some((pattern) => pattern.test(source))) offenders.push(relative(file));
    }

    expect(offenders).toEqual([]);
  });
});
