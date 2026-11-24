import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  runnerArtifacts,
  runnerArtifactsFor,
  runnerDownloadAvailable,
  type RunnerPlatform,
} from './runner-downloads.ts';
import { parseRunnerLatest, resolveLatestUrl, runnerFileName, RUNNER_LATEST_URL } from './runner-latest.ts';
import { siteConfig } from './site-config.ts';

// Decision D25: a page goes live only once the thing it describes does. The release list (built
// from the published latest.json) is the only switch and it fails closed. No network here, and no
// test depends on what the last build wrote: the fixture mirrors the document
// runner/cmd/reelbolt-manifest `latest` writes.

const PLATFORMS: readonly RunnerPlatform[] = ['windows-x64', 'linux-x64', 'linux-arm64'];
const HASH = 'a'.repeat(64);
const base = 'https://downloads.reelbolt.ai/runner/stable/latest';

type Doc = Record<string, unknown>;
function fixture(): Doc {
  return {
    channel: 'stable',
    version: '1.2.3',
    publishedAt: '2026-11-06T10:00:00Z',
    artifacts: [
      { platform: 'windows-x64', kind: 'installer', label: 'Windows installer (.msi)', url: `${base}/r.msi`, sha256: HASH, size: 1000, recommended: true },
      { platform: 'windows-x64', kind: 'update-payload', label: 'raw', url: 'http://evil.example/x', sha256: 'zz', size: 1 },
      { platform: 'linux-x64', kind: 'installer', label: 'Linux AppImage', url: `${base}/r.AppImage`, sha256: HASH, size: 2000 },
    ],
  };
}
const first = (d: Doc) => (d.artifacts as Doc[])[0];

test('the download list is the switch, and it agrees with itself', () => {
  assert.equal(runnerDownloadAvailable(), runnerArtifacts.length > 0);
});

test('a valid document yields installers only', () => {
  const r = parseRunnerLatest(fixture());
  assert.ok(r);
  assert.equal(r.version, '1.2.3');
  assert.deepEqual(r.artifacts.map((a) => a.platform), ['windows-x64', 'linux-x64']);
  assert.equal(r.artifacts[0].recommended, true);
});

test('invalid data fails closed (null)', () => {
  const bad = (mutate: (d: Doc) => void) => {
    const d = fixture();
    mutate(d);
    assert.equal(parseRunnerLatest(d), null);
  };
  bad((d) => { first(d).platform = 'macos-arm64'; });
  bad((d) => { first(d).sha256 = 'A'.repeat(64); });
  bad((d) => { first(d).sha256 = 'abc'; });
  bad((d) => { first(d).size = 0; });
  bad((d) => { first(d).size = 1.5; });
  bad((d) => { first(d).label = ' '; });
  bad((d) => { d.version = ''; });
  bad((d) => { d.publishedAt = 'yesterday'; });
  bad((d) => { d.artifacts = []; });
  bad((d) => { d.artifacts = (d.artifacts as unknown[]).filter((_, i) => i === 1); });
  for (const v of [null, 'x', 1, [], {}]) assert.equal(parseRunnerLatest(v), null);
});

test('URLs: only https on the download host, default port, plain file name; nothing throws', () => {
  const withUrl = (url: string) => {
    const d = fixture();
    first(d).url = url;
    return parseRunnerLatest(d);
  };
  for (const bad of [
    'http://downloads.reelbolt.ai/x.msi',
    'https://evil.example/x.msi',
    'https://downloads.reelbolt.ai.evil.example/x.msi',
    'https://downloads.reelbolt.ai@evil.example/x.msi',
    'https://user@downloads.reelbolt.ai/x.msi',
    'https://downloads.reelbolt.ai:8443/x.msi',
    `${base}/a%20b.msi`,
    `${base}/a;rm.msi`,
    `${base}/$(x).msi`,
    `${base}/a'b.msi`,
    `${base}/`,
    'not a url',
  ]) {
    assert.equal(withUrl(bad), null, bad);
  }
  const ok = withUrl(`${base}/ReelBolt-Runner_1.2.3+x.msi`);
  assert.ok(ok);
  assert.equal(runnerFileName(ok.artifacts[0].url), 'ReelBolt-Runner_1.2.3+x.msi');
});

test('RUNNER_LATEST_URL is only honoured for localhost or the production host', () => {
  assert.equal(resolveLatestUrl(undefined), RUNNER_LATEST_URL);
  assert.equal(resolveLatestUrl('http://localhost:8123/latest.json'), 'http://localhost:8123/latest.json');
  assert.equal(resolveLatestUrl(RUNNER_LATEST_URL), RUNNER_LATEST_URL);
  for (const bad of [
    'https://evil.example/latest.json',
    'http://downloads.reelbolt.ai/latest.json',
    'https://localhost/latest.json',
    'http://localhost.evil.example/latest.json',
    'nonsense',
  ]) {
    assert.throws(() => resolveLatestUrl(bad), bad);
  }
});

test('every built artifact is an https download-host URL with a real checksum', () => {
  for (const artifact of runnerArtifacts) {
    assert.match(artifact.url, /^https:\/\/downloads\.reelbolt\.ai\//);
    assert.match(artifact.sha256, /^[0-9a-f]{64}$/);
    assert.ok(PLATFORMS.includes(artifact.platform));
  }
});

test('artifacts partition by platform, and macOS has no bucket', () => {
  const total = PLATFORMS.reduce((n, platform) => n + runnerArtifactsFor(platform).length, 0);
  assert.equal(total, runnerArtifacts.length);
  assert.deepEqual(runnerArtifactsFor('macos-arm64' as RunnerPlatform), []);
});

test('the runner route the footer, sitemap and page share is /runner', () => {
  assert.equal(siteConfig.runnerPath, '/runner');
});
