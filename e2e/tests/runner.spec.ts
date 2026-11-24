import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';

const execFileAsync = promisify(execFile);

// The harness path is resolved the way every other spec in this directory does it.
// This file was the only one using import.meta.url, which does not load here:
// e2e/package.json declares no type: module, so a spec is CommonJS and
// import.meta is a SyntaxError before any test is found.
// The path also needs the extra .. - this file lives in e2e/tests/ while the
// harness lives in e2e/scripts/, so resolving from __dirname alone pointed at
// e2e/tests/scripts/runner-harness.mjs, which does not exist.
const HARNESS = join(__dirname, '..', 'scripts', 'runner-harness.mjs');

/**
 * E13 — the end-to-end runner harness.
 *
 * `worker-smoke.sh` is the D13 acceptance check, written as a manual script.
 * This spec is its CI form: it drives `runner-harness.mjs` and asserts the
 * outcome. The harness is the source of truth for WHAT was proven; this spec
 * only turns that evidence into a green/red/annotated-skip Playwright result.
 *
 * The harness never reports success without running. A leg whose preconditions
 * are missing in the current environment SKIPS with a printed reason (e.g. no
 * sandbox-runtime image, so no container can run). A leg the harness has no
 * implementation for (today the remotion.workspace-session leg) is listed as
 * NOT COVERED in every run and annotated here; it is not a skip. The spec
 * encodes that contract:
 *
 *   - exit 0 with no skips          → the whole chain ran and passed (green);
 *   - exit 0 with skips             → the runnable legs ran and passed; the
 *                                     skipped legs are annotated and the test
 *                                     PASSES, so a missing precondition is a
 *                                     visible skip, never a silent pass;
 *   - exit 1                        → a leg FAILED (red);
 *   - exit 2 (run-full mode)        → a leg skipped under --run-full, where
 *                                     every leg is required (red);
 *   - anything else (killed by a signal, the timeout, node missing)
 *                                   → red, with the reason in the message.
 *
 * Every non-zero exit is red. The checks on the output for exit 1 and 2 are
 * there so a red also says WHY; they are not what makes it red.
 *
 * The test runs the harness in its default mode (skip-tolerant). `RUN_FULL=1`
 * switches it to `--run-full`, for an environment where every leg is expected
 * to be runnable.
 */

interface HarnessResult {
  code: number;
  /** How the harness ended, for the failure message: "exit 1", "killed by SIGTERM", ... */
  how: string;
  stdout: string;
  stderr: string;
}

async function runHarness(mode: 'default' | 'run-full'): Promise<HarnessResult> {
  const args = [HARNESS, mode === 'run-full' ? '--run-full' : undefined, '--quiet'].filter(Boolean);
  try {
    const { stdout, stderr } = await execFileAsync('node', args as string[], {
      timeout: 18 * 60 * 1000, // the acceptance ceiling is 20 min
      maxBuffer: 64 * 1024 * 1024,
      env: { ...process.env },
    });
    return { code: 0, how: 'exit 0', stdout, stderr };
  } catch (err) {
    // execFile reports a non-zero exit as a numeric `code`. A child killed by a
    // signal (including execFile's own timeout) has `code` null and `signal`
    // set; one that never started has a string `code` such as ENOENT. Neither
    // is an exit code, so keep the reason rather than collapsing it to -1.
    const e = err as { code?: number | string | null; signal?: string | null; killed?: boolean; message?: string; stdout?: string; stderr?: string };
    const numeric = typeof e.code === 'number';
    const how = numeric
      ? `exit ${e.code}`
      : e.signal
        ? `killed by ${e.signal}${e.killed ? ' (the 18 min timeout)' : ''}`
        : `did not run: ${e.code ?? ''} ${e.message ?? ''}`.trim();
    return {
      code: numeric ? (e.code as number) : -1,
      how,
      stdout: e.stdout ?? '',
      stderr: e.stderr ?? '',
    };
  }
}

function listAfter(output: string, heading: string): string[] {
  const items: string[] = [];
  const block = output.match(new RegExp(`${heading}:\\n((?:    - .*\\n?)+)`));
  if (block) {
    for (const line of block[1].split('\n')) {
      const item = line.trim().replace(/^-\s*/, '');
      if (item) items.push(item);
    }
  }
  return items;
}

function parseSummary(output: string): { passed: number; skipped: number; failed: number; skippedLegs: string[]; notCoveredLegs: string[] } {
  const m = output.match(/(\d+) passed, (\d+) skipped, (\d+) failed/);
  return {
    passed: m ? Number(m[1]) : 0,
    skipped: m ? Number(m[2]) : 0,
    failed: m ? Number(m[3]) : 0,
    skippedLegs: listAfter(output, 'skipped legs'),
    // Legs the harness has no implementation for (see the harness header).
    // Not a skip, so they never turn --run-full red, but always annotated.
    notCoveredLegs: listAfter(output, 'not covered by this harness'),
  };
}

test.describe('E13 end-to-end runner harness', () => {
  test('pair → route → execute → re-probe, end to end', async () => {
    // The harness drives no browser, so the viewport projects add nothing but a
    // second identical run. Two runs at once would also collide: both bind the
    // same Postgres and gateway ports, and each kills the other's worker as a
    // "leftover". Run it once, under the desktop project.
    test.skip(test.info().project.name !== 'desktop', 'browserless harness: runs once, under the desktop project');
    test.setTimeout(20 * 60 * 1000);
    const mode = process.env.RUN_FULL === '1' ? 'run-full' : 'default';
    const { code, how, stdout, stderr } = await runHarness(mode);
    const output = stdout + '\n' + stderr;
    const summary = parseSummary(output);

    // Always show the evidence chain in the report, so a reader sees which legs
    // ran, which skipped, and what each proved.
    test.info().annotations.push({ type: 'harness-output', description: output.trim() });
    if (summary.skippedLegs.length > 0) {
      test.info().annotations.push({
        type: 'skipped-legs',
        description: summary.skippedLegs.join('\n'),
      });
    }
    if (summary.notCoveredLegs.length > 0) {
      test.info().annotations.push({
        type: 'not-covered-legs',
        description: summary.notCoveredLegs.join('\n'),
      });
    }

    if (code === 1) {
      expect(output, `a leg failed (exit 1), but the harness did not say which\n${output}`).toContain('FAIL');
      expect(summary.failed, `a leg failed, but the summary reports 0 failed\n${output}`).toBeGreaterThan(0);
    }

    if (code === 2) {
      // --run-full requires every leg to run; a skip is a failure here.
      expect(output, `run-full requires every leg; a leg skipped\n${output}`).toContain('not a full pass');
    }

    // Exit 1 (a leg failed), exit 2 (a required leg skipped) and anything else
    // (a signal, the timeout) are all red. The checks above only make the
    // message say why.
    expect(code, `the harness ended with ${how}\n${output}`).toBe(0);

    if (code === 0) {
      expect(summary.failed, `exit 0 but the summary reports failures`).toBe(0);
      if (mode === 'run-full') {
        expect(summary.skipped, `run-full mode requires no skips`).toBe(0);
        expect(summary.passed, `the chain produced no passing evidence`).toBeGreaterThan(0);
      } else {
        // Default mode: the runnable legs ran and passed. A missing precondition
        // is a visible skip, never a silent pass, so at least one leg must have
        // actually run for a green to mean anything.
        expect(summary.passed, `exit 0 produced no passing evidence — a green with no legs is a rubber stamp`).toBeGreaterThan(0);
      }
    }
  });
});
