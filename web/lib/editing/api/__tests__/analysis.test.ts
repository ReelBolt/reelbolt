import { describe, expect, it, vi } from 'vitest';

import { doc, media, track, TRACKS, assetRef } from '../../__tests__/fixtures';
import { isAnalysisArtifact, resolveAnalysisArtifact, sourceMatcher } from '../analysis';

const P = '11111111-1111-4111-8111-111111111111';
const SEED = '22222222-2222-4222-8222-222222222222';
const ANALYSIS = '33333333-3333-4333-8333-333333333333';
const EXEC = '44444444-4444-4444-8444-444444444444';
const KEY = `projects/${P}/agentFiles/video-analysis/${EXEC}/step-1-analysis.json`;
const FILE_A = assetRef(1).projectFileId;
const FILE_B = assetRef(2).projectFileId;

// The compile EDL as VideoCompileStepExecutor.BuildEdl writes it: no silence, words or shots.
const edl = (analysisStepResultId: string | null) => ({ version: 1, analysisArtifactStorageKey: KEY, analysisStepOrder: 1, analysisStepResultId, mode: 'Keep', segments: [{ index: 0, snappedStartSec: 0, snappedEndSec: 5, sourceIndex: 0 }] });
const analysis = {
  sources: [{ sourceIndex: 0, storageKey: 'projects/p/userFiles/a.mp4' }, { sourceIndex: 1, storageKey: 'projects/p/userFiles/b.mp4' }],
  shots: [{ id: 's0', startSec: 0, endSec: 10 }],
  silenceSpans: [{ id: 'g0', startSec: 3, endSec: 4.5, sourceIndex: 0 }, { id: 'g1', startSec: 8, endSec: 9.5, sourceIndex: 1 }],
};
const files = [{ id: FILE_A, storageKey: 'projects/p/userFiles/a.mp4' }, { id: FILE_B, storageKey: 'projects/p/userFiles/b.mp4' }];

function stub(routes: Record<string, unknown>) {
  return vi.fn(async (url: string) => {
    if (!(url in routes)) throw new Error(`unexpected GET ${url}`);
    return routes[url];
  });
}

describe('resolveAnalysisArtifact', () => {
  it('follows the EDL storage key to the run step result that wrote the analysis (same-run seed)', async () => {
    const fetchJson = stub({
      [`/api/v1/projects/${P}/step-results/${SEED}/artifact`]: edl(null),
      [`/api/v1/projects/${P}/executions/${EXEC}`]: { id: EXEC, stepResults: [{ id: SEED, artifactStorageKey: `projects/${P}/agentFiles/video-analysis/${EXEC}/step-2-edl.json` }, { id: ANALYSIS, artifactStorageKey: KEY }] },
      [`/api/v1/projects/${P}/step-results/${ANALYSIS}/artifact`]: analysis,
      [`/api/v1/projects/${P}/files`]: files,
    });
    const resolved = await resolveAnalysisArtifact(P, SEED, fetchJson);
    expect(resolved?.stepResultId).toBe(ANALYSIS);
    expect(resolved?.artifact).toBe(analysis);
    expect(resolved?.sources).toEqual([{ sourceIndex: 0, projectFileId: FILE_A }, { sourceIndex: 1, projectFileId: FILE_B }]);
  });

  it('uses analysisStepResultId directly when the EDL names it (cross-run recompile)', async () => {
    const fetchJson = stub({
      [`/api/v1/projects/${P}/step-results/${SEED}/artifact`]: edl(ANALYSIS),
      [`/api/v1/projects/${P}/step-results/${ANALYSIS}/artifact`]: analysis,
      [`/api/v1/projects/${P}/files`]: files,
    });
    expect((await resolveAnalysisArtifact(P, SEED, fetchJson))?.stepResultId).toBe(ANALYSIS);
    expect(fetchJson).toHaveBeenCalledTimes(3);
  });

  it('uses the seed itself when it already is an analysis, and null when nothing points at one', async () => {
    const legacy = { shots: [{ id: 's0', startSec: 0, endSec: 1 }] };
    const direct = await resolveAnalysisArtifact(P, SEED, stub({ [`/api/v1/projects/${P}/step-results/${SEED}/artifact`]: legacy }));
    expect(direct).toEqual({ stepResultId: SEED, artifact: legacy, sources: [] });
    expect(await resolveAnalysisArtifact(P, SEED, stub({ [`/api/v1/projects/${P}/step-results/${SEED}/artifact`]: { version: 1, segments: [] } }))).toBeNull();
    // A run whose step results no longer list that key: no analysis, not a guess.
    expect(
      await resolveAnalysisArtifact(P, SEED, stub({ [`/api/v1/projects/${P}/step-results/${SEED}/artifact`]: edl(null), [`/api/v1/projects/${P}/executions/${EXEC}`]: { stepResults: [] } })),
    ).toBeNull();
    expect(isAnalysisArtifact(edl(null))).toBe(false);
  });
});

describe('sourceMatcher', () => {
  const twoFiles = doc([track(TRACKS.v1, 'video', [media('a', 0, 300, 0, { assetRef: assetRef(1) }), media('b', 300, 600, 0, { assetRef: assetRef(2) })])]);
  const [a, b] = twoFiles.tracks[0].items as Parameters<ReturnType<typeof sourceMatcher>>[0][];

  it('matches a clip only to the source of its own file', () => {
    const m = sourceMatcher({ sources: [{ sourceIndex: 0, projectFileId: FILE_A }, { sourceIndex: 1, projectFileId: FILE_B }] }, twoFiles);
    expect([m(a, 0), m(a, 1), m(b, 0), m(b, 1)]).toEqual([true, false, false, true]);
    // A source the project no longer has matches nothing.
    expect(sourceMatcher({ sources: [{ sourceIndex: 0, projectFileId: null }] }, twoFiles)(a, 0)).toBe(false);
  });

  it('trusts a source-less (legacy) analysis only on a one-file timeline', () => {
    expect(sourceMatcher({ sources: [] }, twoFiles)(a, 0)).toBe(false);
    const oneFile = doc([track(TRACKS.v1, 'video', [media('a', 0, 300, 0, { assetRef: assetRef(1) }), media('c', 300, 600, 900, { assetRef: assetRef(1) })])]);
    const m = sourceMatcher({ sources: [] }, oneFile);
    expect(m(oneFile.tracks[0].items[1] as typeof a, 0)).toBe(true);
    expect(sourceMatcher(null, oneFile)(a, 0)).toBe(false);
  });
});
