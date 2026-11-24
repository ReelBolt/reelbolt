// @vitest-environment node
/**
 * The pages the legal re-acceptance gate exempts (web/proxy.ts `legalGateExempt`) must exist in a
 * checkout, not just on the disk of whoever wrote them (S-22). The repo-root .gitignore ignores every
 * `data/` directory, which silently kept app/(app)/settings/data out of git: the proxy let a session
 * behind on the Terms through to /settings/data, and every deployed build answered with a 404, so the
 * only way to export your data or delete your account without accepting led nowhere.
 *
 * Keep this list in step with `legalGateExempt` in web/proxy.ts.
 */
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const webRoot = path.resolve(__dirname, '..');

const exemptPages: Array<[route: string, page: string]> = [
  ['/accept-terms', 'app/(auth)/accept-terms/page.tsx'],
  ['/settings/data', 'app/(app)/settings/data/page.tsx'],
];

/** `git check-ignore` exits 0 when the path IS ignored, 1 when it is not. */
function isGitIgnored(relativePath: string): boolean {
  try {
    execFileSync('git', ['check-ignore', '--quiet', '--no-index', relativePath], { cwd: webRoot, stdio: 'ignore' });
    return true;
  } catch (err) {
    const status = (err as { status?: number | null }).status;
    if (status === 1) return false;
    throw err;
  }
}

describe('legal-gate exits have a page in every checkout', () => {
  it.each(exemptPages)('%s has its page file', (_route, page) => {
    expect(existsSync(path.join(webRoot, page))).toBe(true);
  });

  it.each(exemptPages)('%s is not git-ignored, so it ships', (_route, page) => {
    expect(isGitIgnored(page)).toBe(false);
  });
});
