/** @vitest-environment node */
/**
 * Coverage assertions for the fixture set.
 *
 * The schema-drift guard proves the fixtures VALIDATE. This file proves they actually cover the
 * cases the ticket names — every derivative kind, a source with `probe: null`, a probe with null
 * fields, a source with no derivatives, and BOTH mediaRef prefixes. Coverage that is only asserted
 * in prose rots the moment someone trims a fixture.
 */

import { describe, expect, it } from 'vitest';

import {
  DERIVATIVE_FIXTURES,
  FIXTURE_IDS,
  POOL,
  STEP_RESULT_ITEM,
  VIDEO_FILE_ITEM,
  DERIVATIVES_ALL_KINDS,
  DERIVATIVES_NONE,
  DERIVATIVES_NULL_FIELDS,
} from '../fixtures';
import { DERIVATIVE_KINDS, MEDIA_KINDS, MEDIA_REF_PREFIXES } from '../types';
import type { MediaDerivativeKind, MediaKind, MediaRef } from '../types';

/** The prefix a mediaRef carries — read ONLY here, in a test, to prove both are covered. */
const prefixOf = (mediaRef: MediaRef): string => mediaRef.slice(0, mediaRef.indexOf(':'));

describe('fixture coverage', () => {
  it('covers both mediaRef prefixes', () => {
    const prefixes = new Set(POOL.map((item) => prefixOf(item.mediaRef)));

    for (const prefix of MEDIA_REF_PREFIXES) expect([...prefixes]).toContain(prefix);
    expect(prefixes.size).toBe(MEDIA_REF_PREFIXES.length);
  });

  it('covers every media kind', () => {
    const kinds = new Set<MediaKind>(POOL.map((item) => item.kind));
    expect([...kinds].sort()).toEqual([...MEDIA_KINDS].sort());
  });

  it('covers every derivative kind', () => {
    const kinds = new Set<MediaDerivativeKind>(DERIVATIVES_ALL_KINDS.map((row) => row.kind));
    expect([...kinds].sort()).toEqual([...DERIVATIVE_KINDS].sort());
  });

  it('covers a source that was never analysed', () => {
    expect(POOL.some((item) => item.probe === null)).toBe(true);
    expect(STEP_RESULT_ITEM.probe).toBeNull();
  });

  it('covers a probe whose every field is null', () => {
    const allNull = POOL.some(
      (item) =>
        item.probe !== null &&
        item.probe.durationSec === null &&
        item.probe.fpsNum === null &&
        item.probe.fpsDen === null &&
        item.probe.width === null &&
        item.probe.height === null,
    );
    expect(allNull).toBe(true);
  });

  it('covers a source with no derivatives and one with null-valued rows', () => {
    expect(DERIVATIVES_NONE).toEqual([]);
    expect(DERIVATIVES_NULL_FIELDS[0].mimeType).toBeNull();
    expect(DERIVATIVES_NULL_FIELDS[0].durationSec).toBeNull();
    expect(DERIVATIVES_NULL_FIELDS[0].width).toBeNull();
    expect(DERIVATIVES_NULL_FIELDS[0].height).toBeNull();
  });

  it('offers every derivative scenario the component tests iterate over', () => {
    expect(DERIVATIVE_FIXTURES.map((fixture) => fixture.name)).toEqual([
      'all eight derivative kinds',
      'all nullable fields null',
      'no derivatives generated',
    ]);
  });

  it('keeps the probed video probe an exact rational, not a pre-divided fps', () => {
    // 30000/1001 is 29.97; collapsing it to a double is the drift the contract exists to prevent.
    expect(VIDEO_FILE_ITEM.probe?.fpsNum).toBe(30000);
    expect(VIDEO_FILE_ITEM.probe?.fpsDen).toBe(1001);
  });

  it('gives every fixture item a non-empty name and MIME type', () => {
    for (const item of POOL) {
      expect(item.fileName.trim().length).toBeGreaterThan(0);
      expect(item.mimeType.trim().length).toBeGreaterThan(0);
      expect(item.sizeBytes).toBeGreaterThanOrEqual(0);
    }
  });

  it('keeps stable, distinct ids for the fixtures the tests name individually', () => {
    const ids = Object.values(FIXTURE_IDS);
    expect(new Set(ids).size).toBe(ids.length);
    for (const id of ids) expect(id).toMatch(/^[0-9a-f-]{36}$/);
  });
});
