// The desktop runner's downloads, fed by the published latest.json and fail-closed.
//
// `scripts/fetch-runner-latest.mjs` (run by `build` and `build:pages`) fetches
// https://downloads.reelbolt.ai/runner/stable/latest.json, validates it strictly
// (`lib/runner-latest.ts`) and writes `runner-latest.generated.json`. Nothing is written by hand.
// When the fetch fails or the data is invalid the generated release is null, the list below is
// empty and `/runner` renders its honest "not available yet" state, noindex, absent from
// `sitemap.xml` and `llms.txt` (decision D25: a page goes live only once what it describes does;
// H5: no invented facts). `RUNNER_DOWNLOADS_REQUIRED=true` makes the build fail instead.
//
// Platforms are decision D26's: Windows x64 and Linux x64/arm64. No macOS key exists.

import generated from './runner-latest.generated.json' with { type: 'json' };
import { parseRunnerLatest, type RunnerArtifact, type RunnerPlatform, type RunnerRelease } from './runner-latest.ts';

export type { RunnerArtifact, RunnerPlatform, RunnerRelease };

// Re-validated here too, so a hand-edited generated file cannot turn a download on.
const stored = (generated as unknown as { release: RunnerRelease | null }).release;

export const runnerRelease: RunnerRelease | null = stored
  ? parseRunnerLatest({ ...stored, artifacts: stored.artifacts.map((a) => ({ ...a, kind: 'installer' })) })
  : null;

export const runnerArtifacts: readonly RunnerArtifact[] = runnerRelease?.artifacts ?? [];

/** Whether `/runner` has anything real to hand out. Drives the page, its indexing and the sitemap. */
export function runnerDownloadAvailable(): boolean {
  return runnerArtifacts.length > 0;
}

/** The artifacts for one platform, so the page can lead with the visitor's own OS. */
export function runnerArtifactsFor(platform: RunnerPlatform): readonly RunnerArtifact[] {
  return runnerArtifacts.filter((artifact) => artifact.platform === platform);
}
