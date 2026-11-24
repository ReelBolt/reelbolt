// Build-time fetch of the runner's published latest.json -> lib/runner-latest.generated.json.
// Run by `build` and `build:pages`. Fails closed: a fetch error or invalid data writes
// `{ "release": null }`, so /runner stays in its "not available yet" state. Set
// RUNNER_DOWNLOADS_REQUIRED=true (after launch) to make the build fail instead.
// RUNNER_LATEST_URL may point at http://localhost (a test fixture) or the production host only.
import { writeFileSync } from 'node:fs';
import { parseRunnerLatest, resolveLatestUrl } from '../lib/runner-latest.ts';

const out = new URL('../lib/runner-latest.generated.json', import.meta.url);
const required = process.env.RUNNER_DOWNLOADS_REQUIRED === 'true';

async function load() {
  const url = resolveLatestUrl(process.env.RUNNER_LATEST_URL);
  const res = await fetch(url, { signal: AbortSignal.timeout(15000), redirect: 'error' });
  if (!res.ok) throw new Error(`${url} answered ${res.status}`);
  const release = parseRunnerLatest(await res.json());
  if (!release) throw new Error(`${url} is not a valid latest.json with installers`);
  return release;
}

let release = null;
try {
  release = await load();
  console.log(`runner downloads: ${release.version}, ${release.artifacts.length} installer(s)`);
} catch (err) {
  const msg = err instanceof Error ? err.message : String(err);
  if (required) {
    console.error(`runner downloads: REQUIRED but unavailable: ${msg}`);
    process.exit(1);
  }
  console.warn(`runner downloads: none (${msg}); /runner ships its not-available state`);
}
writeFileSync(out, JSON.stringify({ release }, null, 2) + '\n');
