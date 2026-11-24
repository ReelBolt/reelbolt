// Runs after `docusaurus build` (npm `postbuild`). The dashboard lives at /app on the same origin,
// OUTSIDE this site's baseUrl (/docs/). The navbar and footer must link to /app/, never /docs/app/.
// `pathname://` does not escape baseUrl — Docusaurus's Link appends it — and that is exactly how the
// "Open ReelBolt" and "Dashboard" links used to resolve to /docs/app/. This fails the build if a
// link regresses, so the escape (`autoAddBaseUrl: false`) cannot be dropped silently.
// Checked per language: build/ holds the default locale and build/<code>/ every other one, and each of them
// needs its home page and at least one /app/ link (the dashboard picks its own language).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const buildDir = path.join(root, 'build');
const problems = [];
const registry = JSON.parse(fs.readFileSync(path.join(root, 'locales.json'), 'utf8'));
const otherLocales = registry.locales.map((locale) => locale.code).filter((code) => code !== registry.defaultLocale);

if (!fs.existsSync(buildDir)) {
  console.error('assert-app-link: no build/ folder; run `docusaurus build` first.');
  process.exit(1);
}

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    return entry.isDirectory() ? [full, ...walk(full)] : [full];
  });
}

// Which language a built page belongs to: the first folder under build/ when it is a locale code.
function localeOf(page) {
  const first = path.relative(buildDir, page).split(path.sep)[0];
  return otherLocales.includes(first) ? first : registry.defaultLocale;
}

const foundApp = new Set();
for (const page of walk(buildDir).filter((f) => f.endsWith('.html'))) {
  const html = fs.readFileSync(page, 'utf8');
  if (/href="\/docs\/app(\/|"|#|\?)/.test(html)) {
    problems.push(`${path.relative(root, page)} links to /docs/app instead of /app`);
  }
  if (/\bhref="\/app(\/|"|#|\?)/.test(html)) foundApp.add(localeOf(page));
}

for (const code of registry.locales.map((locale) => locale.code)) {
  const home = code === registry.defaultLocale ? path.join(buildDir, 'index.html') : path.join(buildDir, code, 'index.html');
  if (!fs.existsSync(home)) problems.push(`the ${code} site was not built (${path.relative(root, home)} is missing)`);
  if (!foundApp.has(code)) {
    problems.push(`no built ${code} page contains href="/app"; the Open ReelBolt link is missing`);
  }
}

if (problems.length > 0) {
  console.error('assert-app-link: the dashboard link must resolve outside the docs baseUrl.');
  for (const problem of problems) console.error(`  - ${problem}`);
  process.exit(1);
}
console.log(`assert-app-link: ok (/app, not /docs/app, in ${registry.locales.map((l) => l.code).join(', ')}).`);
