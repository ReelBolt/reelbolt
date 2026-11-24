#!/usr/bin/env node
// Lists user-guide pages whose translation is missing or out of date.
//   node scripts/i18n-docs-stale.mjs [--strict] [--locale it]      (npm run i18n:stale)
// A translated page `docs/user-guide-i18n/<locale>/docusaurus-plugin-content-docs/current/<path>` carries
// `i18n_source_sha` in its front matter (written by scripts/i18n-stamp.mjs): the git blob hash of the English
// page it was translated from. The page is STALE when that hash differs from the English file today,
// UNSTAMPED when it has none, MISSING when the English page has no translation yet (the site then shows the
// English page, so a missing page is a gap, not an error), ORPHANED when its English page is gone.
// Default: print a summary and exit 0 (a warning). --strict exits 1 on anything stale, unstamped or orphaned,
// and on missing pages too. Locales come from locales.json (every locale but the default).
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PAGE = /\.mdx?$/;
// Folders that stay English-only on purpose (the public API reference is developer material, like the legal pages and the blog).
// An untranslated page here is not MISSING; a translation that does exist is still checked for freshness.
export const ENGLISH_ONLY_PREFIXES = ['api/'];
const isEnglishOnly = (relative) => ENGLISH_ONLY_PREFIXES.some((prefix) => relative.startsWith(prefix));

function walk(dir) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

const posix = (p) => p.split(path.sep).join('/');

/** `git hash-object <file>` without spawning git: sha1 of "blob <bytes>\0" + content. */
export function blobHash(buffer) {
  return crypto
    .createHash('sha1')
    .update(`blob ${buffer.length}\0`)
    .update(buffer)
    .digest('hex');
}

/** The `i18n_source_sha` value in a page's front matter, or null. */
export function readStamp(text) {
  const match = /^---\r?\n([\s\S]*?)\r?\n---/.exec(text);
  if (!match) return null;
  const line = match[1].split(/\r?\n/).find((l) => l.startsWith('i18n_source_sha:'));
  return line ? line.slice('i18n_source_sha:'.length).trim().replace(/^["']|["']$/g, '') || null : null;
}

function pagesUnder(dir) {
  return new Map(
    walk(dir)
      .filter((file) => PAGE.test(file))
      .map((file) => [posix(path.relative(dir, file)), file]),
  );
}

/**
 * Compares one locale's translated pages with the English pages.
 * @returns {{locale: string, total: number, translated: number, fresh: string[], stale: string[],
 *   unstamped: string[], missing: string[], orphaned: string[]}}
 */
export function compareLocale({ locale, englishDir, translatedDir }) {
  const english = pagesUnder(englishDir);
  const translated = pagesUnder(translatedDir);
  const result = { locale, total: english.size, translated: translated.size, fresh: [], stale: [], unstamped: [], missing: [], orphaned: [] };
  for (const [relative, file] of english) {
    const mine = translated.get(relative);
    if (!mine) {
      if (!isEnglishOnly(relative)) result.missing.push(relative);
      continue;
    }
    const stamp = readStamp(fs.readFileSync(mine, 'utf8'));
    if (!stamp) result.unstamped.push(relative);
    else if (stamp !== blobHash(fs.readFileSync(file))) result.stale.push(relative);
    else result.fresh.push(relative);
  }
  for (const relative of translated.keys()) if (!english.has(relative)) result.orphaned.push(relative);
  for (const key of ['fresh', 'stale', 'unstamped', 'missing', 'orphaned']) result[key].sort();
  return result;
}

export function format(result) {
  const lines = [
    `i18n:stale [${result.locale}] ${result.fresh.length}/${result.total} pages up to date, ` +
      `${result.stale.length} stale, ${result.unstamped.length} unstamped, ${result.missing.length} missing, ` +
      `${result.orphaned.length} orphaned`,
  ];
  const section = (label, list, hint) => {
    if (list.length === 0) return;
    lines.push(`  ${label} (${hint}):`);
    for (const page of list) lines.push(`    - ${page}`);
  };
  section('STALE', result.stale, 'English changed since the translation was stamped; re-translate, then run scripts/i18n-stamp.mjs');
  section('UNSTAMPED', result.unstamped, 'no i18n_source_sha; run scripts/i18n-stamp.mjs <english> <translation>');
  section('ORPHANED', result.orphaned, 'the English page no longer exists; delete or move the translation');
  section('MISSING', result.missing, 'not translated yet; the site shows the English page');
  return lines.join('\n');
}

/** Exit code: 0 unless --strict and anything is out of line. */
export function exitCode(results, strict) {
  if (!strict) return 0;
  return results.some((r) => r.stale.length || r.unstamped.length || r.orphaned.length || r.missing.length) ? 1 : 0;
}

function main() {
  const args = process.argv.slice(2);
  const strict = args.includes('--strict');
  const only = args.includes('--locale') ? args[args.indexOf('--locale') + 1] : null;
  const registry = JSON.parse(fs.readFileSync(path.join(root, 'locales.json'), 'utf8'));
  const locales = registry.locales.map((l) => l.code).filter((code) => code !== registry.defaultLocale && (!only || code === only));
  const english = path.resolve(root, '..', 'docs', 'user-guide');
  const results = locales.map((locale) =>
    compareLocale({
      locale,
      englishDir: english,
      translatedDir: path.resolve(root, '..', 'docs', 'user-guide-i18n', locale, 'docusaurus-plugin-content-docs', 'current'),
    }),
  );
  for (const result of results) console.log(format(result));
  const code = exitCode(results, strict);
  if (code !== 0) console.error('i18n:stale: --strict and some pages are stale, unstamped, orphaned or missing.');
  process.exit(code);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
