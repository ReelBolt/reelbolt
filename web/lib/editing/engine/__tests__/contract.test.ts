/**
 * The shared golden contract corpus, TS side.
 *
 * `web/lib/editing/__fixtures__/contract/*.json` is ONE fixture set run by two runners: this one,
 * against the engine's own rules ({@link assertInvariants}), and the xUnit
 * `EditTimelineContractCorpusTests`, against `EditTimelineValidator` (what the server's PATCH
 * enforces). Each fixture states the contract's verdict (`expected.valid`, and the error path where
 * one is meaningful). A side that still disagrees carries a `knownDrift` marker naming that side and
 * the ticket that fixes it; for that side this runner asserts the drift STILL exists, so the fixing
 * change has to delete the marker (and a document the client accepts but the server refuses can no
 * longer go unnoticed).
 *
 * Fixture shape:
 *   { name, description, document, expected: { valid, errorPath? },
 *     sourceDurations?: Array<{ projectFileId?: string, stepResultId?: string, frames: number }>,
 *     knownDrift?: { side: 'ts' | 'cs', fixedBy } | Array<{ side, fixedBy }> }
 *
 * `sourceDurations` (R01.6, additive) is the known length in frames of the sources the document names; a
 * fixture without it runs with no durations known, so a clip is never refused for its extent.
 */

import fs from 'node:fs';
import path from 'node:path';

import { describe, expect, it } from 'vitest';

import { assertInvariants, EditEngineError, uncheckedMediaClips } from '../apply';
import type { EditTimeline } from '../../types';

type Side = 'ts' | 'cs';
type Drift = { side: Side; fixedBy: string };
type ContractFixture = {
  name: string;
  description: string;
  document: unknown;
  expected: { valid: boolean; errorPath?: string };
  sourceDurations?: { projectFileId?: string; stepResultId?: string; frames: number }[];
  /** T-023: the clip paths whose source length is unknown ("not checked"); both sides must report exactly these. */
  unchecked?: string[];
  /** Segmentation masks: the VideoAnalyze results (and their g{n} tracks) the validation context knows; `projectId` set means another project's, which the same-project lookup never contains. */
  segmentationTracks?: { stepResultId: string; projectId?: string; trackIds: string[] }[];
  knownDrift?: Drift | Drift[];
};

const FIXTURE_DIR = path.resolve(__dirname, '../../__fixtures__/contract');

function loadFixtures(): { file: string; fixture: ContractFixture }[] {
  return fs
    .readdirSync(FIXTURE_DIR)
    .filter((file) => file.endsWith('.json'))
    .sort()
    .map((file) => ({
      file,
      fixture: JSON.parse(fs.readFileSync(path.join(FIXTURE_DIR, file), 'utf8')) as ContractFixture,
    }));
}

function driftsFor(fixture: ContractFixture, side: Side): Drift | undefined {
  const drifts = fixture.knownDrift === undefined ? [] : Array.isArray(fixture.knownDrift) ? fixture.knownDrift : [fixture.knownDrift];
  return drifts.find((drift) => drift.side === side);
}

/** The engine's messages lead with the offending path (`tracks[0].items[2].recordIn must ...`). */
const LEADING_PATH = /^[A-Za-z]+(?:\[\d+\]|\.[A-Za-z]+)*/;

type Verdict = { valid: true } | { valid: false; errorPath: string | undefined; message: string };

function tsVerdict(document: unknown, sourceDurations?: ContractFixture['sourceDurations'], segmentation?: ContractFixture['segmentationTracks']): Verdict {
  try {
    const durations = sourceDurations
      ? Object.fromEntries(sourceDurations.map((d) => [String(d.projectFileId ?? d.stepResultId), d.frames]))
      : undefined;
    const segmentationTracks = segmentation
      ? Object.fromEntries(segmentation.filter((s) => s.projectId === undefined).map((s) => [s.stepResultId, s.trackIds]))
      : undefined;
    assertInvariants(structuredClone(document) as EditTimeline, {
      ...(durations ? { sourceDurations: durations } : {}),
      ...(segmentationTracks ? { segmentationTracks } : {}),
    });
    return { valid: true };
  } catch (error) {
    // Only the engine's own refusal is a verdict; a TypeError on a malformed document is a crash,
    // which the test must surface rather than score as "rejected".
    if (!(error instanceof EditEngineError)) throw error;
    return { valid: false, errorPath: LEADING_PATH.exec(error.message)?.[0], message: error.message };
  }
}

const fixtures = loadFixtures();

describe('contract corpus (TS engine invariants)', () => {
  it('has fixtures to run and every file is well formed', () => {
    expect(fixtures.length).toBeGreaterThanOrEqual(11);
    for (const { file, fixture } of fixtures) {
      expect(fixture.name, file).toBe(file.replace(/\.json$/, ''));
      expect(typeof fixture.description, file).toBe('string');
      expect(typeof fixture.expected?.valid, file).toBe('boolean');
      expect(fixture.document, file).toBeTypeOf('object');
    }
  });

  it.each(fixtures.map(({ fixture }) => [fixture.name, fixture] as const))('%s', (_name, fixture) => {
    const verdict = tsVerdict(fixture.document, fixture.sourceDurations, fixture.segmentationTracks);
    const drift = driftsFor(fixture, 'ts');

    if (drift !== undefined) {
      expect(
        verdict.valid,
        `knownDrift says the TS rules still disagree with the contract (fixed by ${drift.fixedBy}); ` +
          'they now agree, so delete the ts knownDrift marker from the fixture',
      ).not.toBe(fixture.expected.valid);
      return;
    }

    expect(verdict.valid, verdict.valid ? 'TS accepted the document' : `TS refused: ${verdict.message}`).toBe(
      fixture.expected.valid,
    );
    if (!verdict.valid && fixture.expected.errorPath !== undefined && verdict.errorPath !== undefined) {
      expect(verdict.errorPath, verdict.message).toBe(fixture.expected.errorPath);
    }
  });
});

describe('contract corpus: unchecked source lengths (T-023)', () => {
  const withUnchecked = fixtures.filter(({ fixture }) => fixture.unchecked !== undefined);
  it('has at least one fixture', () => expect(withUnchecked.length).toBeGreaterThanOrEqual(1));
  it.each(withUnchecked.map(({ fixture }) => [fixture.name, fixture] as const))('%s', (_name, fixture) => {
    const durations = Object.fromEntries((fixture.sourceDurations ?? []).map((d) => [String(d.projectFileId ?? d.stepResultId), d.frames]));
    expect(uncheckedMediaClips(fixture.document as EditTimeline, durations)).toEqual(fixture.unchecked);
  });
});
