// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest';
import type { OnboardingServerState } from '@/lib/onboarding/state';
import {
  NO_FACTS,
  failureSide,
  firstResultStorageKey,
  hasFacts,
  isFirstResult,
  isFirstRunFailed,
  mayBeFirstResult,
  openedAResultBefore,
  readFirstResultMark,
  runFacts,
  thirdStepFor,
  writeFirstResultMark,
} from './first-result';

const analyze = (view: unknown, extra: Record<string, unknown> = {}) => ({
  stepType: 'VideoAnalyze', status: 'Completed' as const, stepOrder: 1, outputJson: JSON.stringify({ view, meta: {} }), ...extra,
});
const compile = (out: unknown, extra: Record<string, unknown> = {}) => ({
  stepType: 'VideoCompile', status: 'Completed' as const, stepOrder: 3, outputJson: JSON.stringify(out), ...extra,
});
/** The compile's real keys (VideoCompileStepExecutor `outputSummary`). */
const COMPILED = { status: 'completed', segments: 9, outputDurationSec: 58.2, retainedRatio: 0.23 };

describe('runFacts: before and after, from the run\'s own step outputs', () => {
  it('reads one clip from view.media and the cut from the compile', () => {
    const facts = runFacts([analyze({ media: { durationSec: 252.4, width: 1920, height: 1080 }, shots: [] }), compile(COMPILED)]);
    expect(facts).toEqual({ sourceSec: 252.4, sourceClips: 1, outputSec: 58.2, parts: 9 });
    expect(hasFacts(facts)).toBe(true);
  });

  it('adds up several clips from view.sources', () => {
    const view = { media: { durationSec: 60 }, sources: [{ src: 0, durationSec: 60 }, { src: 1, durationSec: 90.5 }, { src: 2, durationSec: 30 }] };
    expect(runFacts([analyze(view), compile(COMPILED)])).toMatchObject({ sourceSec: 180.5, sourceClips: 3, parts: 9 });
  });

  it('shows no figure the run did not report: nothing is worked out from a ratio', () => {
    // A compile with a retained ratio but no analysis: the footage length is not derived from it.
    expect(runFacts([compile(COMPILED)])).toEqual({ sourceSec: null, sourceClips: null, outputSec: 58.2, parts: null });
    // An analysis with no compile and no probed file: only the footage is known, and "parts" is not.
    expect(runFacts([analyze({ media: { durationSec: 40 } })])).toEqual({ sourceSec: 40, sourceClips: 1, outputSec: null, parts: null });
    expect(runFacts([])).toEqual(NO_FACTS);
    expect(runFacts(undefined)).toEqual(NO_FACTS);
    expect(hasFacts(NO_FACTS)).toBe(false);
  });

  it('drops values that are missing, not numbers or not believable', () => {
    for (const bad of [0, -3, Number.NaN, '58', null, 90_000]) {
      expect(runFacts([compile({ ...COMPILED, outputDurationSec: bad })]).outputSec).toBeNull();
      expect(runFacts([analyze({ media: { durationSec: bad } }), compile(COMPILED)]).sourceSec).toBeNull();
    }
    for (const bad of [0, -1, 2.5, '9', 20_000]) {
      expect(runFacts([analyze({ media: { durationSec: 300 } }), compile({ ...COMPILED, segments: bad })]).parts).toBeNull();
    }
    // One clip of several without a length: the total would be a guess, so there is none.
    expect(runFacts([analyze({ media: { durationSec: 60 }, sources: [{ durationSec: 60 }, { src: 1 }] }), compile(COMPILED)]).sourceSec).toBeNull();
    // Output that is not JSON is not a report.
    expect(runFacts([{ stepType: 'VideoCompile', status: 'Completed', outputJson: 'done' }])).toEqual(NO_FACTS);
  });

  it('does not call generated or rendered material "your footage"', () => {
    expect(runFacts([analyze({ media: { durationSec: 30, composition: true } }), compile(COMPILED)]).sourceSec).toBeNull();
    expect(runFacts([analyze({ media: { durationSec: 6 }, sources: [{ src: 0, durationSec: 6, clipId: 'v1t1' }] }), compile(COMPILED)]).sourceSec).toBeNull();
  });

  it('takes the first footage analysis and the last compile that really encoded', () => {
    const facts = runFacts([
      // A later analysis of generated takes must not replace the footage.
      analyze({ media: { durationSec: 8 }, sources: [{ durationSec: 8, clipId: 'v1t1' }] }, { stepOrder: 5 }),
      analyze({ media: { durationSec: 200 } }, { stepOrder: 1 }),
      compile({ ...COMPILED, outputDurationSec: 61, segments: 7 }, { stepOrder: 7, iterationNumber: 0 }),
      compile({ ...COMPILED, outputDurationSec: 59, segments: 8 }, { stepOrder: 7, iterationNumber: 1 }),
      // A plan-only pass and a failed pass wrote no video.
      compile({ status: 'completed', planOnly: true, encoded: false, segments: 3, outputDurationSec: 12 }, { stepOrder: 9 }),
      { ...compile({ ...COMPILED, outputDurationSec: 5 }, { stepOrder: 10 }), status: 'Failed' as const },
    ]);
    expect(facts).toEqual({ sourceSec: 200, sourceClips: 1, outputSec: 59, parts: 8 });
  });

  it('uses the finished file\'s own length only when no compile reported one', () => {
    expect(runFacts([], 31.4).outputSec).toBe(31.4);
    expect(runFacts([compile(COMPILED)], 31.4).outputSec).toBe(58.2);
    expect(runFacts([], 0).outputSec).toBeNull();
  });

  it('drops the footage when the finished video is longer than it', () => {
    expect(runFacts([analyze({ media: { durationSec: 20 } }), compile({ ...COMPILED, outputDurationSec: 35 })]))
      .toEqual({ sourceSec: null, sourceClips: null, outputSec: 35, parts: null });
    // The same length (a whole-source pass) still compares.
    expect(runFacts([analyze({ media: { durationSec: 35 } }), compile({ ...COMPILED, outputDurationSec: 35.2, segments: 1 })]).sourceSec).toBe(35);
  });

  it('ignores a summary result whose output was left out', () => {
    expect(runFacts([{ stepType: 'VideoCompile', status: 'Completed', output: '', outputJson: null }])).toEqual(NO_FACTS);
  });
});

const server = (openResult: boolean): OnboardingServerState => ({
  tour: { status: 'pending', version: null, updatedAt: null },
  checklist: { dismissed: false, items: [{ key: 'run_video', done: true }, { key: 'open_result', done: openResult }] },
});
const passed = { id: 'e1', status: 'Passed' as const };

describe('whose first result it is', () => {
  it('knows "opened before" only from an API answer, and believes this browser too', () => {
    expect(openedAResultBefore(null, {})).toBeNull();
    expect(openedAResultBefore(server(false), {})).toBe(false);
    expect(openedAResultBefore(server(true), {})).toBe(true);
    expect(openedAResultBefore(server(false), { done: ['open_result'] })).toBe(true);
  });

  const STARTED = '2026-11-11T10:00:00Z';
  /** The workspace as the overview reports it: this run, and the one output it has made. */
  const workspace = (over: Record<string, unknown> = {}) =>
    ({
      recentExecutions: [{ id: 'e1', status: 'Passed', startedAt: STARTED }],
      recentOutputs: [{ stepResultId: 'sr1', producedAt: '2026-11-11T10:05:00Z' }],
      totals: { renders: 1 },
      ...over,
    }) as never;

  it('is the first only when nothing says otherwise', () => {
    const overview = workspace({ recentExecutions: [{ id: 'e1', status: 'Passed', startedAt: STARTED }, { id: 'e0', status: 'Failed', startedAt: '2026-11-10T10:00:00Z' }] });
    expect(isFirstResult({ execution: passed, executions: [passed, { id: 'e0', status: 'Failed' }], openedBefore: false, overview, workspaceCount: 1 })).toBe(true);
    // A run that made several outputs (a review pass rendered twice) is still one first video.
    const twoRenders = workspace({
      recentOutputs: [{ stepResultId: 'sr2', producedAt: '2026-11-11T10:09:00Z' }, { stepResultId: 'sr1', producedAt: '2026-11-11T10:05:00Z' }],
      totals: { renders: 2 },
    });
    expect(isFirstResult({ execution: passed, executions: [passed], openedBefore: false, overview: twoRenders, workspaceCount: 1 })).toBe(true);
  });

  it('answers no on every doubt', () => {
    const overview = workspace();
    const base = { execution: passed, executions: [passed], openedBefore: false as boolean | null, overview, workspaceCount: 1 as number | null };
    expect(isFirstResult(base)).toBe(true);
    expect(isFirstResult({ ...base, openedBefore: null })).toBe(false); // the API did not answer
    expect(isFirstResult({ ...base, openedBefore: true })).toBe(false); // opened a result before
    expect(isFirstResult({ ...base, executions: [passed, { id: 'e0', status: 'Passed' }] })).toBe(false); // an earlier video of this app
    expect(isFirstResult({ ...base, overview: undefined })).toBe(false); // the workspace's runs did not load
    expect(isFirstResult({ ...base, overview: workspace({ recentExecutions: [{ id: 'x', status: 'Passed', startedAt: STARTED }] }) })).toBe(false); // a video elsewhere
    expect(isFirstResult({ ...base, execution: { id: 'e1', status: 'Running' } })).toBe(false);
    expect(isFirstResult({ ...base, execution: undefined })).toBe(false);
    expect(mayBeFirstResult({ execution: passed, executions: undefined, openedBefore: false })).toBe(true);
  });

  // The account's "opened a result" mark used to be set only on the run-detail page, so an account
  // can have finished videos and still read "never opened one". The workspace is asked directly.
  it('is not the first for an account that already has finished videos, whatever its mark says', () => {
    const base = { execution: passed, executions: [passed], openedBefore: false as boolean | null, workspaceCount: 1 as number | null };
    // An output made before this run started: an earlier video, older than the recent-runs window.
    const earlier = workspace({
      recentOutputs: [{ stepResultId: 'sr1', producedAt: '2026-11-11T10:05:00Z' }, { stepResultId: 'old', producedAt: '2026-09-01T08:00:00Z' }],
      totals: { renders: 2 },
    });
    expect(isFirstResult({ ...base, overview: earlier })).toBe(false);
    // More outputs than the overview lists: the unlisted ones cannot be vouched for.
    expect(isFirstResult({ ...base, overview: workspace({ totals: { renders: 12 } }) })).toBe(false);
    // This run is not among the recent runs (a stale overview): its start is unknown.
    expect(isFirstResult({ ...base, overview: workspace({ recentExecutions: [] }) })).toBe(false);
    expect(isFirstResult({ ...base, overview: workspace({ recentExecutions: [{ id: 'e1', status: 'Passed', startedAt: null }] }) })).toBe(false);
    // A second workspace (Personal next to a team) may hold a video nothing here can see.
    expect(isFirstResult({ ...base, overview: workspace(), workspaceCount: 2 })).toBe(false);
    expect(isFirstResult({ ...base, overview: workspace(), workspaceCount: null })).toBe(false);
  });

  it('a failed run is the first only with no finished video before it', () => {
    const failed = { id: 'e2', status: 'Failed' as const };
    expect(isFirstRunFailed({ execution: failed, executions: [failed], openedBefore: false })).toBe(true);
    expect(isFirstRunFailed({ execution: failed, executions: [failed, passed], openedBefore: false })).toBe(false);
    expect(isFirstRunFailed({ execution: failed, executions: [failed], openedBefore: true })).toBe(false);
    expect(isFirstRunFailed({ execution: failed, executions: [failed], openedBefore: null })).toBe(false);
    expect(isFirstRunFailed({ execution: passed, executions: [passed], openedBefore: false })).toBe(false);
  });
});

describe('whose side a failure was on', () => {
  const run = (...classes: (string | null)[]) => ({
    stepResults: classes.map((failureClass, i) => ({ id: `r${i}`, status: 'Failed', failureClass })) as never,
  });

  it('is ours only for the classes the platform refunds', () => {
    expect(failureSide(run('PlatformInternal'))).toBe('ours');
    expect(failureSide(run('PlatformProviderError'))).toBe('ours');
  });

  it('is yours for the classes that keep what was used', () => {
    for (const c of ['UserInput', 'UserCancelled', 'ByoProviderError', 'InsufficientCredits', 'EntitlementDenied', 'Runner']) {
      expect(failureSide(run(c))).toBe('yours');
    }
  });

  it('is unknown when the engine did not say, said something new, or said both', () => {
    expect(failureSide(run(null))).toBe('unknown');
    expect(failureSide(run('None'))).toBe('unknown');
    expect(failureSide(run('SomethingNew'))).toBe('unknown');
    expect(failureSide(run('PlatformInternal', 'UserInput'))).toBe('unknown');
    expect(failureSide({ stepResults: [] })).toBe('unknown');
    expect(failureSide(null)).toBe('unknown');
  });
});

describe('the third next step', () => {
  it('follows what the app really has', () => {
    expect(thirdStepFor({ hasClips: true, hasShape: true })).toBe('subtitlesOrShape');
    expect(thirdStepFor({ hasClips: true, hasShape: false })).toBe('subtitles');
    expect(thirdStepFor({ hasClips: false, hasShape: true })).toBe('shape');
    expect(thirdStepFor({ hasClips: false, hasShape: false })).toBe('recipes');
  });
});

describe('the per-person mark', () => {
  beforeEach(() => window.localStorage.clear());

  it('is kept per person and read back defensively', () => {
    expect(readFirstResultMark('u1')).toBeNull();
    expect(writeFirstResultMark('u1', { id: 'e1', dismissed: false })).toBe(true);
    expect(readFirstResultMark('u1')).toEqual({ id: 'e1', dismissed: false });
    expect(readFirstResultMark('u2')).toBeNull();
    writeFirstResultMark('u1', { id: 'e1', dismissed: true });
    expect(readFirstResultMark('u1')).toEqual({ id: 'e1', dismissed: true });
    window.localStorage.setItem(firstResultStorageKey('u3'), 'not json');
    expect(readFirstResultMark('u3')).toBeNull();
    window.localStorage.setItem(firstResultStorageKey('u4'), JSON.stringify({ dismissed: true }));
    expect(readFirstResultMark('u4')).toBeNull();
    // Nobody signed in: nothing is read or written.
    expect(writeFirstResultMark('', { id: 'e1', dismissed: false })).toBe(false);
    expect(readFirstResultMark('')).toBeNull();
  });
});
