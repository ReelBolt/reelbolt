// node --test scripts/i18n-docs-stale.test.mjs   (npm run i18n:test)
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { blobHash, compareLocale, exitCode, format, readStamp } from './i18n-docs-stale.mjs';

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), 'i18n-docs-stale.mjs');

function fixture() {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'i18n-stale-'));
  const en = path.join(tmp, 'en');
  const it = path.join(tmp, 'it');
  fs.mkdirSync(path.join(en, 'sub'), { recursive: true });
  fs.mkdirSync(path.join(it, 'sub'), { recursive: true });
  const write = (dir, rel, text) => fs.writeFileSync(path.join(dir, rel), text);
  write(en, 'fresh.md', '# Fresh\n');
  write(en, 'stale.md', '# Stale v2\n');
  write(en, 'plain.md', '# Plain\n');
  write(en, 'missing.md', '# Missing\n');
  write(en, 'sub/nested.mdx', '# Nested\n');
  write(en, '_category_.json', '{}');
  const sha = (text) => blobHash(Buffer.from(text));
  write(it, 'fresh.md', `---\ntitle: Nuova\ni18n_source_sha: ${sha('# Fresh\n')}\n---\n# Nuova\n`);
  write(it, 'stale.md', `---\ni18n_source_sha: ${sha('# Stale v1\n')}\n---\n# Vecchia\n`);
  write(it, 'plain.md', '# Senza timbro\n');
  write(it, 'sub/nested.mdx', `---\ni18n_source_sha: "${sha('# Nested\n')}"\n---\n# Nidificata\n`);
  write(it, 'gone.md', '# Orfana\n');
  return { tmp, en, it };
}

test('blobHash matches git hash-object', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'i18n-hash-'));
  const file = path.join(tmp, 'a.md');
  fs.writeFileSync(file, '# Ciao\nAccènt\n');
  const expected = execFileSync('git', ['hash-object', file]).toString().trim();
  assert.equal(blobHash(fs.readFileSync(file)), expected);
  fs.rmSync(tmp, { recursive: true, force: true });
});

test('readStamp reads the front matter value, quoted or not, and ignores the body', () => {
  assert.equal(readStamp('---\ntitle: x\ni18n_source_sha: abc123\n---\nbody'), 'abc123');
  assert.equal(readStamp('---\ni18n_source_sha: "abc123"\n---\n'), 'abc123');
  assert.equal(readStamp('# no front matter\ni18n_source_sha: abc123\n'), null);
  assert.equal(readStamp('---\ntitle: x\n---\ni18n_source_sha: abc123\n'), null);
});

test('compareLocale sorts every page into fresh, stale, unstamped, missing or orphaned', () => {
  const { tmp, en, it } = fixture();
  const result = compareLocale({ locale: 'it', englishDir: en, translatedDir: it });
  assert.deepEqual(result.fresh, ['fresh.md', 'sub/nested.mdx']);
  assert.deepEqual(result.stale, ['stale.md']);
  assert.deepEqual(result.unstamped, ['plain.md']);
  assert.deepEqual(result.missing, ['missing.md']);
  assert.deepEqual(result.orphaned, ['gone.md']);
  assert.equal(result.total, 5);
  const text = format(result);
  assert.match(text, /2\/5 pages up to date/);
  assert.match(text, /STALE[^\n]*\n\s+- stale\.md/);
  assert.equal(exitCode([result], false), 0);
  assert.equal(exitCode([result], true), 1);
  fs.rmSync(tmp, { recursive: true, force: true });
});

test('a locale with every page fresh passes --strict', () => {
  const { tmp, en, it } = fixture();
  for (const f of ['stale.md', 'plain.md', 'gone.md']) fs.rmSync(path.join(it, f));
  fs.rmSync(path.join(en, 'stale.md'));
  fs.rmSync(path.join(en, 'plain.md'));
  fs.rmSync(path.join(en, 'missing.md'));
  const result = compareLocale({ locale: 'it', englishDir: en, translatedDir: it });
  assert.deepEqual([result.stale, result.unstamped, result.missing, result.orphaned], [[], [], [], []]);
  assert.equal(exitCode([result], true), 0);
  fs.rmSync(tmp, { recursive: true, force: true });
});

test('the command line exits 0 by default (a warning) and prints a summary', () => {
  const run = spawnSync(process.execPath, [script], { encoding: 'utf8' });
  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stdout, /i18n:stale \[it\]/);
});

test('an untranslated page in an English-only folder is not missing', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'i18n-eo-'));
  const en = path.join(root, 'en');
  const it = path.join(root, 'it');
  fs.mkdirSync(path.join(en, 'api'), { recursive: true });
  fs.mkdirSync(it, { recursive: true });
  fs.writeFileSync(path.join(en, 'api', 'index.md'), '# API\n');
  const result = compareLocale({ locale: 'it', englishDir: en, translatedDir: it });
  assert.deepEqual(result.missing, []);
  assert.equal(exitCode([result], true), 0);
});
