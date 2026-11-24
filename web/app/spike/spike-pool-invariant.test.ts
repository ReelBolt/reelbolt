/** @vitest-environment node */
/**
 * Spike (a) — the decoder-pool invariant drift guard.  REMOVABLE (see
 * `lib/editing/preview/spike/README.md`).
 *
 * `decisions.md`'s binding spike (a) outcome records a footgun worth more than the budget it came
 * from: a pool whose `lookAheadFrames` exceeded `maxDecodedBufferedFrames` produced **0.00 fps, 0
 * frames presented and 2292 frames evicted unseen — while reporting healthy**, and P3-F1 was
 * required to assert `maxDecodedBufferedFrames >= lookAheadFrames + 2` at construction.
 *
 * This file makes that requirement executable rather than advisory:
 *
 *   1. the invariant is ASSERTED in the harness, not merely written in `BUDGET.md`;
 *   2. the shipped pool configuration satisfies it (`18 >= 12 + 2`);
 *   3. it is re-asserted after EVERY `__spikeSetPool` override, because `lookAheadFrames` is the
 *      exact knob that broke it;
 *   4. suspending it is an explicit opt-in, and the result says whether the pool in force actually
 *      satisfies it — so a deliberately suspended run cannot be read as a normal one;
 *   5. the one caller that may suspend it (the `--lookahead` experiment) says so.
 *
 * Source text is scanned by regex, for the same reasons `spike-isolation.test.ts` gives: an
 * AST-based check would need a parser dependency for throwaway scaffolding, and over-reporting is
 * the safe direction — a false positive fails loudly and is fixed by rewording, while a false
 * negative silently defeats the guard.
 *
 * The harness CANNOT be imported here: it touches `window` at module scope. That is why this guard
 * reads text, and it is also why the invariant had to be a throw inside the harness rather than a
 * helper only a test ever calls.
 */

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

const HERE = dirname(fileURLToPath(import.meta.url)); // web/app/spike
const WEB_ROOT = resolve(HERE, '..', '..'); // web
const SPIKE_DIR = resolve(WEB_ROOT, 'lib', 'editing', 'preview', 'spike');

const harnessSource = readFileSync(resolve(SPIKE_DIR, 'playback-harness.mjs'), 'utf8');
const driverSource = readFileSync(resolve(SPIKE_DIR, 'run-playback.mjs'), 'utf8');
const budgetSource = readFileSync(resolve(SPIKE_DIR, 'BUDGET.md'), 'utf8');
const readmeSource = readFileSync(resolve(SPIKE_DIR, 'README.md'), 'utf8');

/** The shipped pool constants, read out of the source so a silent edit is visible here. */
function poolConstants(source: string): { lookAheadFrames: number; maxDecodedBufferedFrames: number } {
  const lookAhead = /lookAheadFrames:\s*(\d+)/.exec(source)?.[1];
  const ceiling = /maxDecodedBufferedFrames:\s*(\d+)/.exec(source)?.[1];
  if (lookAhead === undefined || ceiling === undefined) {
    throw new Error('pool constants not found in playback-harness.mjs');
  }
  return { lookAheadFrames: Number(lookAhead), maxDecodedBufferedFrames: Number(ceiling) };
}

describe('spike (a) — the decoder-pool invariant', () => {
  it('asserts the invariant in the harness, with the +2 headroom the spike recorded', () => {
    expect(harnessSource).toContain('export function assertPoolInvariant(pool)');
    // The rule itself, spelled as the comparison that throws.
    expect(harnessSource).toMatch(/ceiling\s*<\s*lookAhead\s*\+\s*2/);
    // And the consequence, named, so a future reader does not "simplify" it away.
    expect(harnessSource).toContain('0.00 fps');
    expect(harnessSource).toContain('2292 frames');
  });

  it('runs the assertion at module load, so a bad shipped pool cannot be measured at all', () => {
    // The call must sit OUTSIDE any function body — a top-level statement after the POOL literal.
    const afterPool = harnessSource.slice(harnessSource.indexOf('maxDecodedBufferedFrames:'));
    expect(afterPool).toMatch(/^assertPoolInvariant\(POOL\);/m);
  });

  it('ships a pool that satisfies the invariant', () => {
    const { lookAheadFrames, maxDecodedBufferedFrames } = poolConstants(harnessSource);

    expect(lookAheadFrames).toBe(12);
    expect(maxDecodedBufferedFrames).toBe(18);
    expect(maxDecodedBufferedFrames).toBeGreaterThanOrEqual(lookAheadFrames + 2);
  });

  it('re-asserts after EVERY pool override, because lookAheadFrames is the knob that broke it', () => {
    expect(harnessSource).toContain('window.__spikeSetPool');
    expect(harnessSource).toContain('assertPoolInvariant(POOL);');
    // The unsafe path is an explicit opt-in rather than the default.
    expect(harnessSource).toContain('overrides.allowUnsafeLookAhead === true');
    // ...and the result reports whether the pool in force actually satisfies it, derived rather
    // than copied from the caller's intent.
    expect(harnessSource).toContain('poolInvariantSatisfied: satisfied');
    expect(harnessSource).toMatch(/const satisfied = POOL\.maxDecodedBufferedFrames >= POOL\.lookAheadFrames \+ 2;/);
  });

  it('makes the one caller that may suspend it say so', () => {
    const poolCall = /__spikeSetPool\(\{([^}]*)\}/.exec(driverSource)?.[1] ?? '';
    expect(poolCall).toContain('lookAheadFrames');
    expect(poolCall).toContain('allowUnsafeLookAhead: true');
    // The suspension is recorded in the driver report, so the run is self-describing.
    expect(driverSource).toContain('poolOverride,');
  });

  it('keeps BUDGET.md as the durable statement of the invariant', () => {
    expect(budgetSource).toContain('maxDecodedBufferedFrames');
    expect(budgetSource).toMatch(/lookAheadFrames\s*\+\s*2/);
    // The lower-bound caveat must survive too: these numbers are SwiftShader, not hardware GL.
    expect(budgetSource.toLowerCase()).toContain('swiftshader');
    expect(budgetSource.toLowerCase()).toContain('lower bound');
  });

  it('keeps every harness file named as removable, this guard included', () => {
    // T-200's acceptance requires the README to name every harness file REMOVABLE. A guard that
    // nothing lists is the one file a later wave would leave behind.
    for (const file of ['playback-harness.mjs', 'run-playback.mjs', 'measurements.t201.json']) {
      expect(readmeSource).toContain(file);
    }
    expect(readmeSource).toContain('REMOVABLE');
  });

  it('keeps the harness unreachable from the product surface', () => {
    // The invariant is enforced where the pool is built; the pool is throwaway. Nothing under the
    // product surface may reach it, which the isolation guard proves exhaustively — this is the
    // cheap directional check that the two guards agree about the same directory.
    expect(harnessSource).not.toContain('@/lib/editing/preview/spike');
    expect(readmeSource.toLowerCase()).toContain('removable');
  });
});
