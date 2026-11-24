// Cuts a docs version and captures its inventory in one step:
//   npm run docs:version <version>
// The inventory (versioned_inventory.json) is what the postbuild guard compares each snapshot
// against, so a snapshot cannot gain a stray file after the cut. Use docs/docs-process.md §2.
//
// Languages (docs/docs-site.md "Languages"): `docusaurus docs:version` snapshots the English guide into
// versioned_docs/version-<v>/ and, for every other locale that has translated pages, copies
//   docs/user-guide-i18n/<locale>/docusaurus-plugin-content-docs/current/    -> .../version-<v>/
//   docs/user-guide-i18n/<locale>/docusaurus-plugin-content-docs/current.json -> .../version-<v>.json
//   (with its "version.label" set to <v>, not the working copy's translated "Next")
// This script checks that the copy happened for every locale present (and makes it when Docusaurus did not),
// then records the English snapshot AND each translated snapshot in versioned_inventory.json.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const i18nRoot = path.resolve(root, '..', 'docs', 'user-guide-i18n');
const version = process.argv[2];
if (!version || !/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(version)) {
  console.error('docs-version: pass one version name, e.g. 0.1.0-alpha');
  process.exit(1);
}

execFileSync(path.join(root, 'node_modules', '.bin', 'docusaurus'), ['docs:version', version], {
  cwd: root,
  stdio: 'inherit',
});

if (fs.existsSync(i18nRoot)) {
  for (const entry of fs.readdirSync(i18nRoot, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const plugin = path.join(i18nRoot, entry.name, 'docusaurus-plugin-content-docs');
    const current = path.join(plugin, 'current');
    if (!fs.existsSync(current) || fs.readdirSync(current).length === 0) continue; // nothing translated yet
    const snapshot = path.join(plugin, `version-${version}`);
    if (!fs.existsSync(snapshot)) fs.cpSync(current, snapshot, { recursive: true });
    const labels = path.join(plugin, 'current.json');
    const snapshotLabels = path.join(plugin, `version-${version}.json`);
    if (fs.existsSync(labels) && !fs.existsSync(snapshotLabels)) fs.copyFileSync(labels, snapshotLabels);
    // current.json carries the working copy's translated "Next" label (it: "Prossima"); copied as is, the
    // snapshot would be called "Next" in that language's version menu. A cut is labelled with its own name.
    if (fs.existsSync(snapshotLabels)) {
      const translations = JSON.parse(fs.readFileSync(snapshotLabels, 'utf8'));
      translations['version.label'] = { message: version, description: `The label for version ${version}` };
      fs.writeFileSync(snapshotLabels, `${JSON.stringify(translations, null, 2)}\n`);
    }
    console.log(`docs-version: ${entry.name} snapshot ${path.relative(path.resolve(root, '..'), snapshot)}`);
  }
}

execFileSync(process.execPath, [path.join(root, 'scripts', 'capture-version-inventory.mjs'), version], {
  cwd: root,
  stdio: 'inherit',
});
