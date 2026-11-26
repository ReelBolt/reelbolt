import { desktopLayoutOnly } from '../../../fixtures/editor-v2/rows';
import { test, expect } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #2 (report.md §3): a session that silently stops saving. The journey: +V, an ordinary edit, +A,
 * reload; every step must be on the server. On origin/master "+V" mints `track-<uuid>`, autosave gets a
 * ProblemDetails 400 and every later edit is re-sent inside the same rejected document.
 * Per-action guards live in guards/reload-survives.spec.ts.
 */
const itemCount = (tl: Timeline) => tl.document.tracks.reduce((n, t) => n + t.items.length, 0);

test('TOP10-02 P4-OPS-07 P8-SEQ-01 +V, an edit and +A all reach the server and survive reload', async ({ freshTimeline, openEditor }) => {
  desktopLayoutOnly('adding a track is not on a phone');
  knownFailure('TOP10-02');
  const ed = await openEditor(await freshTimeline('seeded'));
  const before = await ed.serverTimeline();
  await ed.tap(ed.testId('add-video-track'));
  await ed.seek(75);
  await ed.splitAtPlayhead();
  await ed.tap(ed.testId('add-audio-track'));
  const all = (tl: Timeline) => tl.document.tracks.length === before.document.tracks.length + 2 && itemCount(tl) === itemCount(before) + 1;
  await ed.expectServer(all, '+V, blade and +A are all stored');
  await expect(ed.chip).not.toContainText(/rejected/i);
  await ed.reload();
  expect(all(await ed.serverTimeline()), 'all three edits survive reload').toBe(true);
});
