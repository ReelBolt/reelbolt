// LEGACY, UNUSED: nothing deploys this bundle (reelbolt.ai is the `site` container behind nginx).
// Builds the static Cloudflare Pages bundle into out/. The route handler in app/api cannot be
// part of a static export, so it is moved aside for the build (Pages serves functions/api/contact.ts
// instead) and always restored, keeping the docker build's source tree untouched.
import { execSync } from 'node:child_process';
import { existsSync, renameSync, writeFileSync } from 'node:fs';
import { pagesRedirects } from './pages-redirects.mjs';

execSync('node scripts/fetch-runner-latest.mjs', { stdio: 'inherit' });

const api = 'app/api';
const aside = '.api-aside';

if (existsSync(api)) renameSync(api, aside);
try {
  execSync('npx next build', { stdio: 'inherit', env: { ...process.env, STATIC_EXPORT: '1' } });
} finally {
  if (existsSync(aside)) renameSync(aside, api);
}

// `/app/*` goes to the contact page only while signup is off; with signup on the "Start free"
// links must reach the dashboard, never the contact page (scripts/pages-redirects.mjs). The file
// is always written, so a rebuild with the other switch state cannot leave a stale rule behind.
writeFileSync('out/_redirects', pagesRedirects(process.env));
