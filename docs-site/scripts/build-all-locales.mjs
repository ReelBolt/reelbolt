// `npm run build`: builds the documentation site once per locale in locales.json, default locale first.
//   node scripts/build-all-locales.mjs [extra docusaurus build args]
// Why one process per locale: docusaurus.config.js sets the site title, tagline, alpha notice, navbar and
// footer labels per language from DOCUSAURUS_CURRENT_LOCALE (i18n-strings.js). Docusaurus loads the config
// before it sets that variable for the locale it is building and does not evaluate it again, so a single
// `docusaurus build` would print English strings in every language. Setting the variable for each child
// process makes the first (and only) evaluation see the right locale. Output: build/ (default locale, /docs/)
// and build/<code>/ (/docs/<code>/); building a locale only clears its own folder, which is why the default
// locale is built first.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const registry = JSON.parse(fs.readFileSync(path.join(root, 'locales.json'), 'utf8'));
const codes = [registry.defaultLocale, ...registry.locales.map((l) => l.code).filter((c) => c !== registry.defaultLocale)];
const docusaurus = path.join(root, 'node_modules', '.bin', 'docusaurus');

for (const code of codes) {
  console.log(`\nbuild-all-locales: building ${code}`);
  const run = spawnSync(docusaurus, ['build', '--locale', code, ...process.argv.slice(2)], {
    cwd: root,
    stdio: 'inherit',
    env: { ...process.env, DOCUSAURUS_CURRENT_LOCALE: code },
  });
  if (run.status !== 0) {
    console.error(`build-all-locales: the ${code} build failed.`);
    process.exit(run.status ?? 1);
  }
}
