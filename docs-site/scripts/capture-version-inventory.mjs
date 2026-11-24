// Captures the file inventory of a cut docs snapshot (docs-site/versioned_docs/version-<v>).
// `npm run docs:version <version>` runs this immediately after `docusaurus docs:version`, so the
// postbuild guard can prove that a snapshot has not gained, lost or changed a file since the cut.
// Run it by hand only when re-creating a snapshot from scratch.
// Translated snapshots (docs/user-guide-i18n/<locale>/docusaurus-plugin-content-docs/version-<v>/) are recorded
// under `i18n.<locale>.files` of the same version entry; a version with no translated snapshot has no `i18n` key.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const version = process.argv[2];
if (!version || !/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(version)) {
  console.error('capture-version-inventory: pass one version name, e.g. 0.1.0-alpha');
  process.exit(1);
}

const dir = path.join(root, 'versioned_docs', `version-${version}`);
if (!fs.existsSync(dir)) {
  console.error(`capture-version-inventory: ${path.relative(root, dir)} does not exist`);
  process.exit(1);
}

function walk(current) {
  return fs.readdirSync(current, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(current, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

function inventoryOf(snapshotDir) {
  const files = {};
  for (const file of walk(snapshotDir).filter((f) => !f.endsWith('.DS_Store'))) {
    const relative = path.relative(snapshotDir, file).split(path.sep).join('/');
    files[relative] = crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  }
  return Object.fromEntries(Object.entries(files).sort(([a], [b]) => a.localeCompare(b)));
}

const files = inventoryOf(dir);
const i18n = {};
const i18nRoot = path.resolve(root, '..', 'docs', 'user-guide-i18n');
if (fs.existsSync(i18nRoot)) {
  for (const entry of fs.readdirSync(i18nRoot, { withFileTypes: true })) {
    const snapshot = path.join(i18nRoot, entry.name, 'docusaurus-plugin-content-docs', `version-${version}`);
    if (entry.isDirectory() && fs.existsSync(snapshot)) i18n[entry.name] = { files: inventoryOf(snapshot) };
  }
}

const inventoryFile = path.join(root, 'versioned_inventory.json');
const inventory = fs.existsSync(inventoryFile) ? JSON.parse(fs.readFileSync(inventoryFile, 'utf8')) : {};
inventory[version] = { files, ...(Object.keys(i18n).length > 0 ? { i18n } : {}) };
fs.writeFileSync(inventoryFile, `${JSON.stringify(inventory, null, 2)}\n`);
console.log(
  `capture-version-inventory: ${version} (${Object.keys(files).length} files` +
    Object.entries(i18n).map(([locale, { files: f }]) => `, ${locale} ${Object.keys(f).length}`).join('') +
    ')',
);
