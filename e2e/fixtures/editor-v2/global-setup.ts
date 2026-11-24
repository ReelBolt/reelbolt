import { request, type FullConfig } from '@playwright/test';
import * as fs from 'fs';
import { ARTIFACTS, READONLY, assertWriteTargetAllowed, credentials, ignoreHTTPSErrorsFor, storageStatePath } from './env';

/**
 * Logs in ONCE through the real API (`POST /api/v1/auth/token`; nginx allows 5 logins a minute per IP)
 * and writes the session to /tmp/editor-remediation/ (mode 600) for every worker and project.
 *
 * A fresh local admin is created with `mustChangePassword`; on the local stack that is answered by
 * "changing" the password to the same value, then the served Terms are accepted. Against a deployed box
 * (EDITOR_E2E_READONLY=1) nothing is written: a session that would need either step stops the run.
 */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const baseURL = config.projects[0].use.baseURL as string;
  // Before anything else (no request, no file): the write suite only ever targets the local stack.
  assertWriteTargetAllowed(baseURL);
  fs.mkdirSync(ARTIFACTS, { recursive: true, mode: 0o700 });
  process.env.EDITOR_E2E_RUN ??= new Date().toISOString().replace(/[-:T]/g, '').slice(0, 12);

  const { email, password } = credentials(baseURL);
  const api = await request.newContext({ baseURL, ignoreHTTPSErrors: ignoreHTTPSErrorsFor(baseURL) });
  try {
    let res = await api.post('/api/v1/auth/token', { data: { email, password } });
    for (let i = 0; i < 6 && res.status() === 429; i++) {
      await new Promise((r) => setTimeout(r, 15_000));
      res = await api.post('/api/v1/auth/token', { data: { email, password } });
    }
    if (!res.ok()) throw new Error(`editor-v2 login failed: HTTP ${res.status()}`);
    const body = (await res.json()) as { mustChangePassword?: boolean };

    if (body.mustChangePassword) {
      if (READONLY) throw new Error('editor-v2 login: the account must change its password; read-only mode writes nothing, stopping');
      const changed = await api.post('/api/v1/auth/change-password', { data: { currentPassword: password, newPassword: password } });
      if (!changed.ok()) throw new Error(`editor-v2 change-password failed: HTTP ${changed.status()}`);
    }

    if (!READONLY) {
      // Accept the served Terms (idempotent; a 409 names the served versions). A fresh admin may list
      // projects before accepting, while the dashboard still redirects every page to /accept-terms.
      let terms = await api.post('/api/v1/auth/accept-terms', { data: { acceptTerms: true, termsVersion: '?', privacyVersion: '?' } });
      if (terms.status() === 409) {
        const cur = (await terms.json()) as { currentTermsVersion: string; currentPrivacyVersion: string };
        terms = await api.post('/api/v1/auth/accept-terms', {
          data: { acceptTerms: true, termsVersion: cur.currentTermsVersion, privacyVersion: cur.currentPrivacyVersion },
        });
      }
      if (!terms.ok()) throw new Error(`editor-v2 accept-terms failed: HTTP ${terms.status()}`);
    }

    const probe = await api.get('/api/v1/projects');
    if (!probe.ok()) throw new Error(`editor-v2 session unusable: GET /api/v1/projects -> HTTP ${probe.status()}`);

    const file = storageStatePath(baseURL);
    await api.storageState({ path: file });
    fs.chmodSync(file, 0o600);
  } finally {
    await api.dispose();
  }
}
