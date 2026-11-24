// @vitest-environment node
/**
 * Integrity of the cursor catalogue the dashboard consumes.
 *
 * The contract in `brand/cursors/cursors.json` is the single source of truth: `sync.mjs` copies it
 * to `web/lib/cursors/catalog.json` and copies every PNG to `web/public/cursors/`. A cursor that
 * loses its artwork, a hotspot that leaves the 32 px canvas, or a copy that drifts from the master
 * makes a live rule point at nothing (the fallback keyword saves the interaction, but the product
 * silently loses its cursor). `sync.mjs --check` catches drift in CI; this test catches it in the
 * web suite too, and pins the shape of the contract the components rely on.
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import catalog from '@/lib/cursors/catalog.json';

interface CursorSpec {
  name: string;
  group: 'ui' | 'editor';
  file: string;
  at2x: string;
  hotspot: { x: number; y: number };
  fallback: string;
  intent: string;
}

const webRoot = path.resolve(__dirname, '../..');
const brandRoot = path.resolve(webRoot, '../brand');
const cursors = (catalog as { cursors: CursorSpec[]; sizePx: number }).cursors;

/** PNG IHDR: signature, then width/height as big-endian u32 at 16/20, colour type at 25. */
function pngHeader(file: string) {
  const bytes = readFileSync(path.join(webRoot, 'public/cursors', file));
  expect([...bytes.subarray(0, 4)]).toEqual([0x89, 0x50, 0x4e, 0x47]);
  return {
    width: bytes.readUInt32BE(16),
    height: bytes.readUInt32BE(20),
    bitDepth: bytes[24],
    colourType: bytes[25],
  };
}

describe('cursor catalogue', () => {
  it('matches the brand contract byte for byte', () => {
    const synced = readFileSync(path.join(webRoot, 'lib/cursors/catalog.json'));
    const master = readFileSync(path.join(brandRoot, 'cursors/cursors.json'));
    expect(synced.equals(master)).toBe(true);
  });

  it('lists 27 uniquely named cursors, 12 UI and 15 editor', () => {
    expect(cursors).toHaveLength(27);
    expect(new Set(cursors.map((c) => c.name)).size).toBe(27);
    expect(cursors.filter((c) => c.group === 'ui')).toHaveLength(12);
    expect(cursors.filter((c) => c.group === 'editor')).toHaveLength(15);
  });

  it.each(cursors.map((c) => [c.name, c] as const))(
    '%s names its files after itself and carries a fallback and intent',
    (name, cursor) => {
      expect(cursor.file).toBe(`${name}.png`);
      expect(cursor.at2x).toBe(`${name}@2x.png`);
      expect(cursor.fallback.trim()).not.toBe('');
      expect(cursor.intent.trim()).not.toBe('');
    },
  );

  it.each(cursors.map((c) => [c.name, c] as const))(
    '%s declares an integer hotspot inside the 32 px canvas',
    (_name, cursor) => {
      expect(Number.isInteger(cursor.hotspot.x)).toBe(true);
      expect(Number.isInteger(cursor.hotspot.y)).toBe(true);
      expect(cursor.hotspot.x).toBeGreaterThanOrEqual(0);
      expect(cursor.hotspot.y).toBeGreaterThanOrEqual(0);
      expect(cursor.hotspot.x).toBeLessThan(32);
      expect(cursor.hotspot.y).toBeLessThan(32);
    },
  );

  it.each(cursors.flatMap((c) => [[c.name, c.file, 32] as const, [c.name, c.at2x, 64] as const]))(
    '%s ships %s as a %i px RGBA PNG',
    (_name, file, px) => {
      const header = pngHeader(file);
      expect(header.width).toBe(px);
      expect(header.height).toBe(px);
      expect(header.bitDepth).toBe(8);
      expect(header.colourType).toBe(6); // truecolour + alpha
    },
  );
});
