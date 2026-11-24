// Runs after `docusaurus build` (npm `postbuild`). The public documentation site publishes the
// user guide and nothing else: the developer docs stay in the repository and are read only by the
// platform assistant (docs/platform-docs-search.md). This fails the build if that ever regresses.
//
// It checks, in the build output, the guide's own Markdown, and the Docusaurus configuration:
//   1. every page Docusaurus rendered comes from docs/user-guide or from a *known* version snapshot
//      under docs-site/versioned_docs/version-<entry>/, where <entry> is in versions.json;
//   2. every file in a known snapshot matches the inventory captured when that version was cut
//      (docs-site/versioned_inventory.json: one SHA-256 per file), so a stray internal doc copied
//      into a snapshot fails instead of being published; a snapshot holds only pages, `_category_.json`
//      and the raster images its pages show;
//   3. the docs plugin still reads `../docs/user-guide`, so `docs:version` cannot snapshot the
//      developer-docs root, and the translations still come from `../docs/user-guide-i18n`;
//   4. there is no `developer` route, file or sitemap entry;
//   5. no page links to a developer route or into the repository's source tree;
//   6. no guide, translation or snapshot page uses a `pathname://` link, the escape hatch that skips
//      Docusaurus's broken-link check (the old links to the developer docs were written that way);
//   7. the translation tree (docs/user-guide-i18n) holds only registered locales and the files Docusaurus
//      reads (pages, theme JSON), and every translated snapshot matches its inventory
//      (`i18n.<locale>.files` in versioned_inventory.json) like the English one. The build output of
//      every language is checked by points 4 and 5 (the build folder holds all of them).
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const buildDir = path.join(root, 'build');
const docsMeta = path.join(root, '.docusaurus', 'docusaurus-plugin-content-docs');
const versionedRoot = path.join(root, 'versioned_docs');
const versionsFile = path.join(root, 'versions.json');
const inventoryFile = path.join(root, 'versioned_inventory.json');
const USER_GUIDE_SOURCE = '@site/../docs/user-guide/';
const I18N_PATH = '../docs/user-guide-i18n';
const i18nRoot = path.resolve(root, I18N_PATH);
const registry = JSON.parse(fs.readFileSync(path.join(root, 'locales.json'), 'utf8'));
const translatedLocales = registry.locales.map((locale) => locale.code).filter((code) => code !== registry.defaultLocale);
const VERSION_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
// A snapshot may hold the raster screenshots its pages show (docs/user-guide/basics/img/, first cut in 0.3.0-alpha).
// Raster only: an SVG can carry script, and any other file type is still refused as a non-page file.
const SNAPSHOT_IMAGE = /\.(webp|png|jpe?g|gif|avif)$/i;

const problems = [];

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    return entry.isDirectory() ? [full, ...walk(full)] : [full];
  });
}

function sha256(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

function toPosix(p) {
  return p.split(path.sep).join('/');
}

// Pure so the self-test below can exercise it without touching the real tree.
function validateVersionSnapshots({ versionedDocsDir, versionNames, inventory, publishedSources }) {
  const found = [];
  if (!fs.existsSync(versionedDocsDir)) {
    for (const version of versionNames) found.push(`version ${version} has no snapshot directory`);
    return found;
  }

  const expectedDirs = new Set(versionNames.map((version) => `version-${version}`));
  for (const entry of fs.readdirSync(versionedDocsDir, { withFileTypes: true })) {
    if (entry.isDirectory() && !expectedDirs.has(entry.name)) {
      found.push(`versioned_docs has a directory that is not in versions.json: ${entry.name}`);
    }
  }

  for (const version of versionNames) {
    const dir = path.join(versionedDocsDir, `version-${version}`);
    if (!fs.existsSync(dir)) {
      found.push(`version ${version} is in versions.json but versioned_docs/version-${version} is missing`);
      continue;
    }
    const recorded = inventory[version]?.files;
    if (!recorded || typeof recorded !== 'object') {
      found.push(`version ${version} has no entry in versioned_inventory.json`);
      continue;
    }
    const actual = new Map(
      walk(dir)
        .filter((file) => fs.statSync(file).isFile() && !file.endsWith('.DS_Store'))
        .map((file) => [toPosix(path.relative(dir, file)), file]),
    );
    for (const [relative, file] of actual) {
      if (!(relative in recorded)) {
        found.push(`version ${version} contains a file that was not in its cut inventory: ${relative}`);
        continue;
      }
      if (sha256(file) !== recorded[relative]) {
        found.push(`version ${version} file changed after the cut: ${relative}`);
      }
      const isCategory = path.basename(relative) === '_category_.json';
      const isDoc = relative.endsWith('.md') || relative.endsWith('.mdx');
      const isImage = SNAPSHOT_IMAGE.test(relative);
      if (!isCategory && !isDoc && !isImage) {
        found.push(`version ${version} contains a file that is not a doc page or a page image: ${relative}`);
      }
      if (isDoc) {
        const source = `@site/versioned_docs/version-${version}/${relative}`;
        // The English page is replaced in a translated build by its translation, when the snapshot has one.
        const replacedBy = translatedLocales.map(
          (code) => `@site/${I18N_PATH}/${code}/docusaurus-plugin-content-docs/version-${version}/${relative}`,
        );
        if (![source, ...replacedBy].some((candidate) => publishedSources.has(candidate))) {
          found.push(`version ${version} file is not a published doc page: ${relative}`);
        }
      }
    }
    for (const relative of Object.keys(recorded)) {
      if (!actual.has(relative)) found.push(`version ${version} is missing an inventoried file: ${relative}`);
    }
  }
  return found;
}

// The translation tree: docs/user-guide-i18n/<locale>/{code.json, docusaurus-theme-classic/*.json,
// docusaurus-plugin-content-docs/{current,version-<v>}/** , current.json, version-<v>.json}. Pure for the self-test.
function validateTranslationTree({ dir, locales, versionNames, inventory }) {
  const found = [];
  if (!fs.existsSync(dir)) return found;
  const isDoc = (file) => file.endsWith('.md') || file.endsWith('.mdx') || path.basename(file) === '_category_.json';
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === '.DS_Store') continue;
    if (!entry.isDirectory() || !locales.includes(entry.name)) {
      found.push(`${path.basename(dir)} holds ${entry.name}, which is not a registered locale folder (locales.json)`);
      continue;
    }
    const localeDir = path.join(dir, entry.name);
    const pluginDir = path.join(localeDir, 'docusaurus-plugin-content-docs');
    const versionDirs = new Set(versionNames.map((version) => `version-${version}`));
    const versionFiles = new Set(versionNames.map((version) => `version-${version}.json`));
    for (const item of fs.readdirSync(localeDir, { withFileTypes: true })) {
      if (item.name === '.DS_Store') continue;
      const ok =
        (item.isFile() && item.name === 'code.json') ||
        (item.isDirectory() && (item.name === 'docusaurus-theme-classic' || item.name === 'docusaurus-plugin-content-docs'));
      if (!ok) found.push(`${entry.name}/${item.name} is not part of the translation layout`);
    }
    const themeDir = path.join(localeDir, 'docusaurus-theme-classic');
    if (fs.existsSync(themeDir)) {
      for (const file of walk(themeDir).filter((f) => !f.endsWith('.DS_Store'))) {
        if (!file.endsWith('.json')) found.push(`${entry.name}: ${toPosix(path.relative(localeDir, file))} is not a JSON translation file`);
      }
    }
    if (!fs.existsSync(pluginDir)) continue;
    for (const item of fs.readdirSync(pluginDir, { withFileTypes: true })) {
      if (item.name === '.DS_Store') continue;
      const okDir = item.isDirectory() && (item.name === 'current' || versionDirs.has(item.name));
      const okFile = item.isFile() && (item.name === 'current.json' || versionFiles.has(item.name));
      if (!okDir && !okFile) {
        found.push(`${entry.name}/docusaurus-plugin-content-docs/${item.name} is neither current, a known version nor its label file`);
      }
    }
    for (const sub of ['current', ...versionNames.map((version) => `version-${version}`)]) {
      const subDir = path.join(pluginDir, sub);
      if (!fs.existsSync(subDir)) continue;
      const actual = new Map(
        walk(subDir)
          .filter((file) => fs.statSync(file).isFile() && !file.endsWith('.DS_Store'))
          .map((file) => [toPosix(path.relative(subDir, file)), file]),
      );
      for (const [relative, file] of actual) {
        if (!isDoc(file)) found.push(`translations ${entry.name}/${sub} contain a file that is not a doc page: ${relative}`);
      }
      if (sub === 'current') continue;
      const version = sub.slice('version-'.length);
      const recorded = inventory[version]?.i18n?.[entry.name]?.files;
      if (!recorded) {
        found.push(`translated snapshot ${entry.name}/${sub} has no entry in versioned_inventory.json (i18n.${entry.name})`);
        continue;
      }
      for (const [relative, file] of actual) {
        if (!(relative in recorded)) found.push(`translated snapshot ${entry.name}/${sub} has a file that was not in its cut inventory: ${relative}`);
        else if (sha256(file) !== recorded[relative]) found.push(`translated snapshot ${entry.name}/${sub} file changed after the cut: ${relative}`);
      }
      for (const relative of Object.keys(recorded)) {
        if (!actual.has(relative)) found.push(`translated snapshot ${entry.name}/${sub} is missing an inventoried file: ${relative}`);
      }
    }
  }
  for (const version of versionNames) {
    for (const code of Object.keys(inventory[version]?.i18n || {})) {
      const snapshot = path.join(dir, code, 'docusaurus-plugin-content-docs', `version-${version}`);
      if (!fs.existsSync(snapshot)) found.push(`versioned_inventory.json lists a translated snapshot that is missing: ${code} version ${version}`);
    }
  }
  return found;
}

function translationSelfTest() {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'docs-guard-i18n-'));
  const plugin = path.join(tmp, 'it', 'docusaurus-plugin-content-docs');
  fs.mkdirSync(path.join(plugin, 'version-1.0.0'), { recursive: true });
  fs.mkdirSync(path.join(plugin, 'current'), { recursive: true });
  fs.writeFileSync(path.join(plugin, 'version-1.0.0', 'ok.md'), '# ok\n');
  fs.writeFileSync(path.join(plugin, 'version-1.0.0', 'stray.md'), '# stray\n');
  fs.writeFileSync(path.join(plugin, 'current', 'logo.png'), 'x');
  fs.mkdirSync(path.join(tmp, 'xx'));
  const inventory = { '1.0.0': { files: {}, i18n: { it: { files: { 'ok.md': sha256(path.join(plugin, 'version-1.0.0', 'ok.md')) } } } } };
  const found = validateTranslationTree({ dir: tmp, locales: ['it'], versionNames: ['1.0.0'], inventory }).join('\n');
  fs.rmSync(tmp, { recursive: true, force: true });
  for (const needle of ['stray.md', 'logo.png', 'xx']) {
    if (!found.includes(needle)) {
      problems.push(`the translation-tree check failed its own self-test (no finding for ${needle}): ${found}`);
    }
  }
}

function selfTest() {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'docs-guard-'));
  const dir = path.join(tmp, 'version-1.0.0');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'ok.md'), '# ok\n');
  fs.writeFileSync(path.join(dir, '_category_.json'), '{}\n');
  fs.writeFileSync(path.join(dir, 'internal.md'), '# secret\n');
  fs.mkdirSync(path.join(dir, 'img'));
  fs.writeFileSync(path.join(dir, 'img', 'shot.webp'), 'x');
  fs.writeFileSync(path.join(dir, 'img', 'notes.txt'), 'x');
  const inventory = {
    '1.0.0': {
      files: {
        'ok.md': sha256(path.join(dir, 'ok.md')),
        '_category_.json': sha256(path.join(dir, '_category_.json')),
        'img/shot.webp': sha256(path.join(dir, 'img', 'shot.webp')),
        'img/notes.txt': sha256(path.join(dir, 'img', 'notes.txt')),
      },
    },
  };
  const publishedSources = new Set(['@site/versioned_docs/version-1.0.0/ok.md']);
  const found = validateVersionSnapshots({
    versionedDocsDir: tmp,
    versionNames: ['1.0.0'],
    inventory,
    publishedSources,
  });
  fs.rmSync(tmp, { recursive: true, force: true });
  // Exactly two findings: the uninventoried page and the inventoried non-image file; the image passes.
  if (
    found.length !== 2 ||
    !found.some((line) => line.includes('internal.md')) ||
    !found.some((line) => line.includes('img/notes.txt')) ||
    found.some((line) => line.includes('shot.webp'))
  ) {
    problems.push(`the version-snapshot inventory check failed its own self-test: ${JSON.stringify(found)}`);
  }
}

if (!fs.existsSync(buildDir)) {
  console.error('assert-user-guide-only: no build/ folder; run `docusaurus build` first.');
  process.exit(1);
}

// 0. The docs plugin must still read the user guide, or `docs:version` would snapshot something else.
try {
  const require = createRequire(import.meta.url);
  const config = require(path.join(root, 'docusaurus.config.js'));
  const classic = (config.presets || []).find((preset) => Array.isArray(preset) && preset[0] === 'classic');
  const docsPath = classic?.[1]?.docs?.path;
  if (docsPath !== '../docs/user-guide') {
    problems.push(`the docs plugin path is ${JSON.stringify(docsPath)}, expected "../docs/user-guide"`);
  }
  if (config.i18n?.path !== I18N_PATH) {
    problems.push(`i18n.path is ${JSON.stringify(config.i18n?.path)}, expected ${JSON.stringify(I18N_PATH)}`);
  }
} catch (error) {
  problems.push(`could not read docusaurus.config.js to check the docs path: ${error.message}`);
}

// 1. Gather the sources Docusaurus actually published, and the known versions.
const publishedSources = new Set();
if (!fs.existsSync(docsMeta)) {
  problems.push(`${path.relative(root, docsMeta)} is missing, so the page sources cannot be checked`);
} else {
  const instances = fs.readdirSync(docsMeta).filter((name) => name !== '.DS_Store');
  if (instances.length !== 1 || instances[0] !== 'default') {
    problems.push(`expected only the default docs plugin instance, found: ${instances.join(', ')}`);
  }
  for (const file of walk(docsMeta).filter((f) => f.endsWith('.json'))) {
    const source = JSON.parse(fs.readFileSync(file, 'utf8')).source;
    if (typeof source === 'string') publishedSources.add(source);
  }
}

let versionNames = [];
if (fs.existsSync(versionsFile)) {
  try {
    const parsed = JSON.parse(fs.readFileSync(versionsFile, 'utf8'));
    if (!Array.isArray(parsed)) throw new Error('not an array');
    for (const version of parsed) {
      if (typeof version !== 'string' || !VERSION_NAME.test(version)) {
        problems.push(`versions.json has an invalid version name: ${JSON.stringify(version)}`);
      }
    }
    versionNames = parsed.filter((version) => typeof version === 'string' && VERSION_NAME.test(version));
  } catch (error) {
    problems.push(`versions.json is unreadable: ${error.message}`);
  }
}

const versionedPrefixes = versionNames.map((version) => `@site/versioned_docs/version-${version}/`);
// A translated page is published from docs/user-guide-i18n/<locale>/docusaurus-plugin-content-docs/{current,version-<v>}/.
const translatedPrefixes = translatedLocales.flatMap((code) =>
  ['current', ...versionNames.map((version) => `version-${version}`)].map(
    (sub) => `@site/${I18N_PATH}/${code}/docusaurus-plugin-content-docs/${sub}/`,
  ),
);
const allowedSources = [USER_GUIDE_SOURCE, ...versionedPrefixes, ...translatedPrefixes];
for (const source of publishedSources) {
  if (!allowedSources.some((allowed) => source.startsWith(allowed))) {
    problems.push(`a published page comes from outside the user guide: ${source}`);
  }
}

// 2. Every snapshot must match the inventory captured when it was cut.
let inventory = {};
if (versionNames.length > 0) {
  if (!fs.existsSync(inventoryFile)) {
    problems.push('versioned_inventory.json is missing, so snapshots cannot be checked');
  } else {
    try {
      inventory = JSON.parse(fs.readFileSync(inventoryFile, 'utf8'));
    } catch (error) {
      problems.push(`versioned_inventory.json is unreadable: ${error.message}`);
    }
  }
  selfTest();
  problems.push(
    ...validateVersionSnapshots({ versionedDocsDir: versionedRoot, versionNames, inventory, publishedSources }),
  );
}

// 2b. The translation tree has only registered locales, theme JSON and pages; translated snapshots match their inventory.
translationSelfTest();
problems.push(...validateTranslationTree({ dir: i18nRoot, locales: translatedLocales, versionNames, inventory }));

// 3. No developer route anywhere in the output, nor in the sitemap.
for (const entry of walk(buildDir)) {
  if (path.relative(buildDir, entry).split(path.sep).some((part) => /^developer(\.html)?$/i.test(part))) {
    problems.push(`build output has a developer route: ${path.relative(root, entry)}`);
  }
}
const sitemap = path.join(buildDir, 'sitemap.xml');
if (fs.existsSync(sitemap) && /<loc>[^<]*\/developer(\/|<)/i.test(fs.readFileSync(sitemap, 'utf8'))) {
  problems.push('sitemap.xml lists a /developer URL');
}

// 4. No page links to a developer route or to a file in the repository.
for (const page of walk(buildDir).filter((f) => f.endsWith('.html'))) {
  const html = fs.readFileSync(page, 'utf8');
  for (const [, href] of html.matchAll(/href="([^"]*)"/g)) {
    const toDeveloper = /(^|\/)developer(\/|$|#|\?)/i.test(href);
    const toRepoSource = /^https:\/\/github\.com\/[^/]+\/[^/]+\/(blob|tree|edit)\//.test(href);
    if (toDeveloper || toRepoSource) problems.push(`${path.relative(root, page)} links to ${href}`);
  }
}

// 5. No `pathname://` links in the user guide's Markdown or in a versioned snapshot.
const markdownDirs = [
  path.resolve(root, '..', 'docs', 'user-guide'),
  i18nRoot,
  path.join(root, 'versioned_docs'),
];
for (const dir of markdownDirs) {
  if (!fs.existsSync(dir)) continue;
  for (const file of walk(dir).filter((f) => f.endsWith('.md'))) {
    if (/\]\(\s*pathname:\/\//.test(fs.readFileSync(file, 'utf8'))) {
      problems.push(`${path.relative(path.resolve(root, '..'), file)} uses a pathname:// link`);
    }
  }
}

if (problems.length > 0) {
  console.error('assert-user-guide-only: the documentation site must publish the user guide only.');
  for (const problem of problems) console.error(`  - ${problem}`);
  process.exit(1);
}
console.log(
  `assert-user-guide-only: ok (user guide, ${translatedLocales.join('/') || 'no'} translations and inventoried snapshots only, no developer route, no repository links).`,
);
