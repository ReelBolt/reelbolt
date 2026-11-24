import { test } from '@playwright/test';

/**
 * Expected failures of the editor-v2 regression suite, one array per Wave 0 item
 * (audit remediation-plan.md §2; feature ids from §2.9 / verdict-table.md, top-10 ids TOP10-01..10 for
 * report.md §3, guard ids GUARD-* for the T0.4 behavioural guards).
 *
 * An entry is either `ID` (expected to fail on every project) or `ID@project` (only there:
 * `desktop`, `pixel7`, `iphone14webkit`). A row with two independent halves (e.g. the render half and the
 * phone "needs a larger screen" half of P8-DSP-08) names the half as a facet: `ID:facet` / `ID:facet@project`,
 * matched by `knownFailure(id, facet)`. A test whose title starts with a listed id calls
 * `knownFailure(id)`, which marks it `test.fail()`: Playwright then reports it red as soon as it PASSES,
 * so a fix that lands without removing its id is caught. A fix PR removes only its own ids from its own
 * array, nothing else.
 *
 * Every audit ❌ row of §2.9 has a spec under `NN-<area>/` (T-002) whose title starts with its feature id; a
 * row that passes on origin/master 9cb5337e1 is NOT listed (the W0.1 / W0.4 notes name them). Rows cut or
 * deferred on phone assert the designed "needs a larger screen" state there (PHONE_CUT, facet `:phone` when
 * the desktop half of the same row is listed under its own Wave 0 item).
 */

/**
 * State after every Wave 0 fix PR has merged (measured 2026-10-13 on a local branch merging all of them, run on the
 * isolated stack: desktop, pixel7, iphone14webkit). This PR therefore merges AFTER every fix PR. Every row that
 * failed on origin/master 9cb5337e1 now passes, except the one environment limit listed below. A new fix that
 * regresses a row turns it red; a row added here must name why it cannot pass.
 */

/** W0.1 Render can start and finishes (top-10 #1). */
export const W0_1: readonly string[] = [];

/** W0.2 Saves never silently stop; concurrency answers 409 (top-10 #2, #9). */
export const W0_2: readonly string[] = [];

/** W0.3 Preview plays the right picture and sound (top-10 #3, #6, #7). */
export const W0_3: readonly string[] = [];

/** W0.4 Media is probed and processed at upload (top-10 #5). */
export const W0_4: readonly string[] = [];

/** W0.5 Analysis-driven editing reads the analysis (top-10 #4). */
export const W0_5: readonly string[] = [];

/** W0.6 Missing basics the ❌ rows need. */
export const W0_6: readonly string[] = [];

/** W0.7 Phone ❌ rows: stop the bleeding before the phone redesign (top-10 #8). */
export const W0_7: readonly string[] = [];

/** W0.8 Picture editor P0 (top-10 #10). */
export const W0_8: readonly string[] = [];

/** Rows the verdicts cut or defer on phone: they get a "needs a larger screen" state, not a fix. */
export const PHONE_CUT: readonly string[] = [];

/**
 * Pre-existing on iPhone (WebKit): the picture editor's AI tool option tap times out (picture-dock-ai intercepts the
 * tap). Measured failing on the base before any editor-phone change (usability-fixes C7 batch, 2026-10-27), and again on
 * the merged integration; unrelated to the phone editor's transport / sheet changes.
 */
export const PRE_EXISTING_IPHONE: readonly string[] = [
  'RM.14 R14.2 Run is pinned@iphone14webkit',
  'RM.14 R14.2 T13@iphone14webkit',
];

export const ALL_EXPECTED_FAILURES: readonly string[] = [...W0_1, ...W0_2, ...W0_3, ...W0_4, ...W0_5, ...W0_6, ...W0_7, ...W0_8, ...PHONE_CUT, ...PRE_EXISTING_IPHONE];

/** True when `id` (optionally `id:facet`) is an expected failure on `project`. */
export function isKnownFailure(id: string, project: string, facet?: string): boolean {
  const key = facet ? `${id}:${facet}` : id;
  return ALL_EXPECTED_FAILURES.some((e) => e === key || e === `${key}@${project}`);
}

/**
 * Marks the running test as an expected failure when `id` (or `id:facet`) is listed for the running
 * project. Call it first thing in the test body; the test title must start with the same id.
 */
export function knownFailure(id: string, facet?: string): void {
  const info = test.info();
  if (!info.title.startsWith(id)) throw new Error(`test title "${info.title}" must start with ${id}`);
  if (isKnownFailure(id, info.project.name, facet)) {
    info.annotations.push({ type: 'expected-failure', description: `${facet ? `${id}:${facet}` : id}: listed in expected-failures.ts` });
    test.fail();
  }
}
