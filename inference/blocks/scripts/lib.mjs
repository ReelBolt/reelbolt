// Shared helpers for the block pipeline scripts (MIT, see ../LICENSE). Node >= 20, no dependencies.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';

export const BLOCKS_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const REPO_ROOT = path.resolve(BLOCKS_ROOT, '..', '..');
export const TEMPLATE_DIR = path.join(REPO_ROOT, 'sandbox', 'template');

// Closed category set. Keep in sync with BlockCategories in the engine (BlockCatalog.cs).
export const CATEGORIES = ['transitions', 'titles', 'captions', 'charts', 'maps', 'effects', 'backgrounds', 'devices', 'shapes'];
export const ID_RE = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
// A file path in the catalog: "<id>/<file>" or "_shared/<file>", segments of [A-Za-z0-9._-], no dot-only segment.
export const SEGMENT_RE = /^(?!\.+$)[A-Za-z0-9][A-Za-z0-9._-]*$/;
export const SHA_RE = /^[0-9a-f]{64}$/;
export const ALLOWED_EXT = new Set(['.tsx', '.ts', '.json', '.css', '.svg']);

export const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');

/** Returns an error string for a bad catalog file path, or null. */
export function checkFilePath(id, p) {
  if (typeof p !== 'string' || p.length === 0 || p.length > 200) return 'path is empty or too long';
  if (p.includes('\\') || p.startsWith('/') || p.includes('\0')) return 'path must be relative with forward slashes';
  const parts = p.split('/');
  if (parts.length < 2) return 'path must be <id>/<file>';
  if (parts[0] !== id && parts[0] !== '_shared') return `path must start with "${id}/" or "_shared/"`;
  if (!parts.slice(1).every((s) => SEGMENT_RE.test(s))) return 'path has an invalid segment';
  if (!ALLOWED_EXT.has(path.extname(p))) return 'extension not allowed';
  return null;
}

/** Package name -> version range from the template package.json and package-catalog.json (when present). */
export function loadAvailablePackages(templateDir = TEMPLATE_DIR) {
  const out = new Map();
  const pkg = JSON.parse(fs.readFileSync(path.join(templateDir, 'package.json'), 'utf8'));
  for (const section of ['dependencies', 'devDependencies']) {
    for (const [name, version] of Object.entries(pkg[section] ?? {})) out.set(name, String(version));
  }
  const catalogPath = path.join(templateDir, 'package-catalog.json');
  if (fs.existsSync(catalogPath)) {
    const raw = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));
    const list = Array.isArray(raw) ? raw : Array.isArray(raw.packages) ? raw.packages : null;
    if (list) {
      for (const e of list) {
        if (typeof e === 'string') out.set(e, out.get(e) ?? '*');
        else if (e && typeof e.name === 'string') out.set(e.name, String(e.version ?? '*'));
      }
    } else {
      const map = raw.packages && typeof raw.packages === 'object' ? raw.packages : raw;
      for (const [name, v] of Object.entries(map)) {
        if (typeof v === 'string') out.set(name, v);
        else if (v && typeof v.version === 'string') out.set(name, v.version);
      }
    }
  }
  return out;
}

export function readCatalog() {
  return JSON.parse(fs.readFileSync(path.join(BLOCKS_ROOT, 'catalog.json'), 'utf8'));
}

/** Validates a catalog object against the files on disk. Returns a list of problems (empty = ok). */
export function validateCatalog(catalog, available = loadAvailablePackages(), root = BLOCKS_ROOT) {
  const problems = [];
  if (catalog.schemaVersion !== 1) problems.push('schemaVersion must be 1');
  const seen = new Set();
  for (const b of catalog.blocks ?? []) {
    const where = `block ${b.id}`;
    if (typeof b.id !== 'string' || !ID_RE.test(b.id) || b.id.length > 64) { problems.push(`${where}: invalid id`); continue; }
    if (seen.has(b.id)) problems.push(`${where}: duplicate id`);
    seen.add(b.id);
    if (!CATEGORIES.includes(b.category)) problems.push(`${where}: unknown category ${b.category}`);
    for (const f of ['title', 'description', 'props', 'example', 'version']) {
      if (typeof b[f] !== 'string' || !b[f].trim()) problems.push(`${where}: missing ${f}`);
    }
    if (!Array.isArray(b.tags)) problems.push(`${where}: tags must be an array`);
    for (const r of b.requires ?? []) {
      if (!available.has(r.name)) problems.push(`${where}: requires "${r.name}", which is in neither package.json nor package-catalog.json`);
    }
    if (!Array.isArray(b.files) || b.files.length === 0 || b.files.length > 24) problems.push(`${where}: a block needs between 1 and 24 files`);
    if (Array.isArray(b.files) && !b.files.some((f) => typeof f.path === 'string' && f.path.startsWith(`${b.id}/`))) problems.push(`${where}: must own at least one file under ${b.id}/`);
    for (const f of b.files ?? []) {
      const err = checkFilePath(b.id, f.path);
      if (err) { problems.push(`${where}: file ${f.path}: ${err}`); continue; }
      const abs = path.join(root, 'src', f.path);
      if (!fs.existsSync(abs)) { problems.push(`${where}: file ${f.path} missing`); continue; }
      let symlink = false;
      for (let cur = root, segs = ['src', ...f.path.split('/')], i = 0; i < segs.length; i++) {
        cur = path.join(cur, segs[i]);
        if (fs.lstatSync(cur).isSymbolicLink()) { symlink = true; break; }
      }
      if (symlink) { problems.push(`${where}: file ${f.path} goes through a symbolic link`); continue; }
      const buf = fs.readFileSync(abs);
      if (buf.length > 256 * 1024) { problems.push(`${where}: file ${f.path} is larger than 256 KiB`); continue; }
      if (!SHA_RE.test(f.sha256) || sha256(buf) !== f.sha256) problems.push(`${where}: file ${f.path} sha256 mismatch (run build-catalog)`);
      const text = buf.toString('utf8');
      if (text.includes('\r') || text.charCodeAt(0) === 0xfeff) problems.push(`${where}: file ${f.path} must be LF without BOM`);
    }
    const pc = b.previewComposition;
    if (!pc || typeof pc.compositionId !== 'string' || !Array.isArray(pc.frames) || !pc.file) problems.push(`${where}: previewComposition incomplete`);
    else if (!fs.existsSync(path.join(root, pc.file))) problems.push(`${where}: preview file ${pc.file} missing`);
  }
  return problems;
}
