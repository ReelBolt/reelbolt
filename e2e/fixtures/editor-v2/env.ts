import * as fs from 'fs';
import * as path from 'path';

/** Everything the suite writes lives here (never in the repository): traces carry the session cookie. */
export const ARTIFACTS = '/tmp/editor-remediation';
export const READONLY = process.env.EDITOR_E2E_READONLY === '1';
/** The kept audit seed project on the test box, used by the read-only smoke. */
export const READONLY_PROJECT = process.env.EDITOR_E2E_READONLY_PROJECT ?? 'audit-seed-20261012-2202';
export const MEDIA_DIR = process.env.EDITOR_E2E_MEDIA_DIR ?? path.join(ARTIFACTS, 'media');

/** True for a loopback base URL (localhost, 127.0.0.0/8, ::1): the only targets the write suite may touch. */
export function isLoopback(baseURL: string): boolean {
  const host = new URL(baseURL).hostname.replace(/^\[|\]$/g, '');
  return host === 'localhost' || /^127(\.\d{1,3}){3}$/.test(host) || host === '::1';
}

/**
 * TLS errors are ignored ONLY for plain http:// or a loopback host (the local stack). A deployed https://
 * target such as the read-only smoke box keeps full certificate validation.
 */
export function ignoreHTTPSErrorsFor(baseURL: string): boolean {
  return new URL(baseURL).protocol === 'http:' || isLoopback(baseURL);
}

/** The write suite creates and deletes projects: refuse anything but a loopback base URL unless read-only. */
export function assertWriteTargetAllowed(baseURL: string, readonly: boolean = READONLY): void {
  if (readonly || isLoopback(baseURL)) return;
  throw new Error(
    `editor-v2: refusing to run the write suite against ${new URL(baseURL).origin}. ` +
    'It creates and deletes projects, so it only runs against the local stack (localhost or 127.0.0.1). ' +
    'For a deployed box set EDITOR_E2E_READONLY=1 to run the read-only smoke instead.',
  );
}

export function storageStatePath(baseURL: string): string {
  const host = new URL(baseURL).host.replace(/[^a-zA-Z0-9.-]+/g, '_');
  return path.join(ARTIFACTS, `storage-${host}.json`);
}

/**
 * Credentials come ONLY from EDITOR_E2E_EMAIL / EDITOR_E2E_PASSWORD. For the local isolated stack, and
 * only for a localhost base URL, the generated admin in /tmp/editor-remediation/stack.env is the fallback.
 * Neither value is ever logged or written anywhere by the suite.
 */
export function credentials(baseURL: string): { email: string; password: string } {
  let email = process.env.EDITOR_E2E_EMAIL;
  let password = process.env.EDITOR_E2E_PASSWORD;
  const host = new URL(baseURL).hostname;
  if ((!email || !password) && (host === 'localhost' || host === '127.0.0.1')) {
    const envFile = path.join(ARTIFACTS, 'stack.env');
    if (fs.existsSync(envFile)) {
      const vars = Object.fromEntries(
        fs.readFileSync(envFile, 'utf8').split('\n')
          .filter((l) => /^[A-Z_]+=/.test(l))
          .map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]),
      );
      email ??= vars.ADMIN_EMAIL;
      password ??= vars.ADMIN_PASSWORD;
    }
  }
  if (!email || !password) throw new Error('EDITOR_E2E_EMAIL / EDITOR_E2E_PASSWORD are required (see playwright.editor-v2.config.ts)');
  return { email, password };
}
