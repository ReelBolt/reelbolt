import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { BrowserContext } from '@playwright/test';

/**
 * Private artifact directory for one mobile-nav run. The config calls this first and publishes the
 * path in MOBILE_NAV_LOGS, which the workers inherit, so config and specs agree. Without an
 * override the directory is a fresh mkdtemp (mode 0700): never a fixed, predictable path that
 * another local user could pre-create. An override is created with mode 0700 as well; one that already exists must be a real directory
 * (not a symlink) owned by the current user, and is chmod 0700, otherwise the run refuses to start.
 */
export function logsDir(): string {
  const set = process.env.MOBILE_NAV_LOGS;
  if (set) {
    if (existsSync(set)) {
      const st = lstatSync(set);
      if (!st.isDirectory()) throw new Error(`MOBILE_NAV_LOGS must be a directory (not a file or symlink): ${set}`);
      if (typeof process.getuid === 'function' && st.uid !== process.getuid()) {
        throw new Error(`MOBILE_NAV_LOGS is not owned by the current user: ${set}`);
      }
      chmodSync(set, 0o700);
      return set;
    }
    mkdirSync(set, { recursive: true, mode: 0o700 });
    return set;
  }
  const dir = mkdtempSync(join(tmpdir(), 'reelbolt-e2e-'));
  chmodSync(dir, 0o700);
  process.env.MOBILE_NAV_LOGS = dir;
  return dir;
}

/** Save a signed-in browser state, readable by this user only. */
export async function saveAuthState(ctx: BrowserContext, path: string): Promise<void> {
  await ctx.storageState({ path });
  chmodSync(path, 0o600);
}

/** Remove every auth-state file of the run (config globalTeardown). */
export function removeAuthStates(dir: string): void {
  if (!existsSync(dir)) return;
  for (const f of readdirSync(dir)) if (/^auth-state.*\.json$/.test(f)) rmSync(join(dir, f), { force: true });
}
