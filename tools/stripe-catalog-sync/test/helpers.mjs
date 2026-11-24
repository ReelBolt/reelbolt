import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { main } from '../lib/cli.mjs';

export const REAL_CATALOG = fileURLToPath(new URL('../../../inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json', import.meta.url));
export const TEST_KEY = 'sk_test_' + 'FAKEKEYFORTESTS0123456789';
export const LIVE_KEY = 'rk_live_' + 'FAKEKEYFORTESTS0123456789';

export async function run(args, { fake, env = {}, key = fake?.key ?? TEST_KEY, extra = {} } = {}) {
  let out = '';
  let err = '';
  const withMp = args.some((a) => a === '--managed-payments' || a === '--no-managed-payments' || a.startsWith('--tax-code'));
  const mpArgs = withMp ? args : ['--no-managed-payments', ...args];
  const base = mpArgs.some((a) => a === '--founder' || a.startsWith('--founder-') || a === '--no-founder') ? mpArgs : ['--no-founder', ...mpArgs];
  const code = await main(base, {
    env: { STRIPE_API_KEY: key, STRIPE_API_BASE: 'http://localhost:12111', ...env },
    stdout: (t) => (out += t),
    stderr: (t) => (err += t),
    fetch: fake?.fetch,
    sleep: async () => {},
    now: new Date('2026-10-26T12:00:00Z'),
    ...extra,
  });
  return { code, out, err };
}

export async function tempDir() {
  return mkdtemp(join(tmpdir(), 'stripe-sync-'));
}

export async function writeJson(dir, name, value) {
  const path = join(dir, name);
  await writeFile(path, JSON.stringify(value), 'utf8');
  return path;
}

export async function editedCatalog(dir, edit) {
  const catalog = JSON.parse(await readFile(REAL_CATALOG, 'utf8'));
  edit(catalog);
  return writeJson(dir, 'catalog.json', catalog);
}

export function writesTo(fake, method, pathPrefix) {
  return fake.writes.filter((w) => w.method === method && w.path.startsWith(pathPrefix));
}
