#!/usr/bin/env node
// Block verification pipeline (MIT, see ../LICENSE). Per block: typecheck, still render of the preview
// composition, non-blank check, and the platform layout lint, all inside the sandbox runtime image
// with the production isolation flags.
//
//   node scripts/verify.mjs [--image reelbolt-sandbox-runtime:local] [--block <id>] [--determinism]   (host: starts the container)
//
// Determinism check: for every block that requires gsap (or every selected block with --determinism) the same
// frame is rendered twice in separate `remotion still` invocations and the PNG bytes (sha256) must match.
//
// Host mode also runs the offline catalog checks (hashes, ids, paths, requires from
// sandbox/template/package.json and package-catalog.json) before any container starts.
// The in-container half is `--in-container` (internal).
import {spawnSync} from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import {BLOCKS_ROOT, TEMPLATE_DIR, loadAvailablePackages, readCatalog, validateCatalog} from './lib.mjs';

const arg = (name) => { const i = process.argv.indexOf(name); return i >= 0 ? process.argv[i + 1] : undefined; };
const only = arg('--block');
const forceDeterminism = process.argv.includes('--determinism');
const HEADLESS = '/opt/remotion-template/node_modules/.remotion/headless-shell';

// ---- PNG decode (8-bit RGB/RGBA, non-interlaced) for the non-blank check -----------------------------
export function decodePng(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let pos = 8, width = 0, height = 0, channels = 0; const idat = [];
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos), type = buf.toString('ascii', pos + 4, pos + 8), data = buf.subarray(pos + 8, pos + 8 + len);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0); height = data.readUInt32BE(4);
      if (data[8] !== 8 || data[12] !== 0) throw new Error('unsupported PNG (need 8-bit, non-interlaced)');
      channels = {2: 3, 6: 4}[data[9]];
      if (!channels) throw new Error('unsupported PNG colour type');
    } else if (type === 'IDAT') idat.push(data);
    else if (type === 'IEND') break;
    pos += 12 + len;
  }
  const raw = zlib.inflateSync(Buffer.concat(idat)); const stride = width * channels;
  const out = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride;
    for (let x = 0; x < stride; x++) {
      const a = x >= channels ? out[dst + x - channels] : 0, b = y > 0 ? out[dst - stride + x] : 0, c = x >= channels && y > 0 ? out[dst - stride + x - channels] : 0;
      let v = raw[src + x];
      if (f === 1) v += a; else if (f === 2) v += b; else if (f === 3) v += (a + b) >> 1;
      else if (f === 4) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      out[dst + x] = v & 255;
    }
  }
  return {width, height, channels, data: out};
}

/** Non-blank: the image has more than one distinct colour bucket and measurable variance. */
export function nonBlank({data, channels}) {
  const buckets = new Set(); let n = 0, sum = 0, sumSq = 0;
  for (let i = 0; i < data.length; i += channels * 7) {
    const lum = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
    n++; sum += lum; sumSq += lum * lum;
    buckets.add(`${data[i] >> 4},${data[i + 1] >> 4},${data[i + 2] >> 4}`);
  }
  const variance = sumSq / n - (sum / n) ** 2;
  return {ok: buckets.size > 2 && variance > 4, distinctColours: buckets.size, variance: Math.round(variance)};
}

const run = (cmd, args, opts = {}) => spawnSync(cmd, args, {encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, ...opts});

function inContainer() {
  const catalog = JSON.parse(fs.readFileSync('/blocks/catalog.json', 'utf8'));
  const blocks = catalog.blocks.filter((b) => !only || b.id === only);
  const ws = '/tmp/ws', tpl = '/opt/remotion-template';
  fs.mkdirSync(path.join(ws, 'src', 'blocks', '_previews'), {recursive: true});
  for (const f of ['package.json', 'tsconfig.json']) fs.copyFileSync(path.join(tpl, f), path.join(ws, f));
  fs.cpSync(path.join(tpl, 'reelbolt'), path.join(ws, 'reelbolt'), {recursive: true});
  fs.symlinkSync(path.join(tpl, 'node_modules'), path.join(ws, 'node_modules'));
  for (const b of blocks) {
    for (const f of b.files) {
      const dst = path.join(ws, 'src', 'blocks', f.path);
      fs.mkdirSync(path.dirname(dst), {recursive: true});
      fs.copyFileSync(path.join('/blocks/src', f.path), dst);
    }
    fs.copyFileSync(path.join('/blocks', b.previewComposition.file), path.join(ws, 'src', 'blocks', '_previews', `${b.id}.tsx`));
  }
  const ident = (id) => id.replace(/-(\w)/g, (_, c) => c.toUpperCase()).replace(/^\w/, (c) => 'P' + c);
  fs.writeFileSync(path.join(ws, 'src', 'index.ts'), `import React from 'react';
import {Composition, registerRoot} from 'remotion';
${blocks.map((b) => `import {Preview as ${ident(b.id)}} from './blocks/_previews/${b.id}.tsx';`).join('\n')}
const Root: React.FC = () => React.createElement(React.Fragment, null,
${blocks.map((b) => `  React.createElement(Composition, {id: '${b.previewComposition.compositionId}', component: ${ident(b.id)}, durationInFrames: ${b.previewComposition.durationInFrames}, fps: ${b.previewComposition.fps}, width: ${b.previewComposition.width}, height: ${b.previewComposition.height}}),
  React.createElement(Composition, {id: '${b.previewComposition.compositionId}-vertical', component: ${ident(b.id)}, durationInFrames: ${b.previewComposition.durationInFrames}, fps: ${b.previewComposition.fps}, width: 1080, height: 1920})`).join(',\n')});
registerRoot(Root);
`);
  const env = {...process.env, HOME: '/tmp', CI: '1'};
  const results = [];
  const tsc = run('node', ['node_modules/typescript/bin/tsc', '--noEmit', '-p', '.'], {cwd: ws, env});
  results.push({check: 'typecheck', ok: tsc.status === 0, detail: tsc.status === 0 ? '' : (tsc.stdout + tsc.stderr).slice(0, 2000)});
  if (tsc.status === 0) {
    fs.mkdirSync(path.join(ws, 'out'), {recursive: true});
    for (const b of blocks) {
      const pc = b.previewComposition;
      for (const frame of pc.frames) {
        const png = path.join(ws, 'out', `${b.id}-${frame}.png`);
        const r = run('node', ['node_modules/@remotion/cli/remotion-cli.js', 'still', 'src/index.ts', pc.compositionId, png, `--frame=${frame}`, `--chromium-executable=${HEADLESS}`], {cwd: ws, env, timeout: 240000});
        if (r.status !== 0 || !fs.existsSync(png)) { results.push({check: `render ${b.id}@${frame}`, ok: false, detail: (r.stdout + r.stderr).slice(-1500)}); continue; }
        if (fs.existsSync('/out')) fs.copyFileSync(png, path.join('/out', path.basename(png))); // optional: REELBOLT_VERIFY_OUT mounts a host dir
        const nb = nonBlank(decodePng(fs.readFileSync(png)));
        results.push({check: `render+non-blank ${b.id}@${frame}`, ok: nb.ok, detail: JSON.stringify(nb)});
      }
      if (forceDeterminism || b.requires.some((r) => r.name === 'gsap')) {
        // A mid-animation frame (about 40% of the preview) and the last declared (settled) frame.
        const dfs = [...new Set([Math.min(pc.durationInFrames - 1, Math.round(pc.durationInFrames * 0.4)), pc.frames[pc.frames.length - 1]])];
        for (const df of dfs) {
        const hashes = [];
        for (const n of [1, 2]) {
          const dpng = path.join(ws, 'out', `${b.id}-det${n}-${df}.png`);
          const dr = run('node', ['node_modules/@remotion/cli/remotion-cli.js', 'still', 'src/index.ts', pc.compositionId, dpng, `--frame=${df}`, `--chromium-executable=${HEADLESS}`], {cwd: ws, env, timeout: 240000});
          hashes.push(dr.status === 0 && fs.existsSync(dpng) ? crypto.createHash('sha256').update(fs.readFileSync(dpng)).digest('hex') : null);
        }
        results.push({check: `determinism ${b.id}@${df}`, ok: !!hashes[0] && hashes[0] === hashes[1], detail: hashes.map((h) => h ? h.slice(0, 12) : 'render-failed').join(' vs ')});
        }
      }
      // Vertical (9:16) check: the same preview component in a 1080x1920 composition, one still + layout lint.
      {
        const vid = `${pc.compositionId}-vertical`, vf = pc.frames[Math.floor(pc.frames.length / 2)];
        const vpng = path.join(ws, 'out', `${b.id}-vertical-${vf}.png`);
        const vr = run('node', ['node_modules/@remotion/cli/remotion-cli.js', 'still', 'src/index.ts', vid, vpng, `--frame=${vf}`, `--chromium-executable=${HEADLESS}`], {cwd: ws, env, timeout: 240000});
        if (vr.status !== 0 || !fs.existsSync(vpng)) results.push({check: `render vertical ${b.id}@${vf}`, ok: false, detail: (vr.stdout + vr.stderr).slice(-1500)});
        else {
          if (fs.existsSync('/out')) fs.copyFileSync(vpng, path.join('/out', path.basename(vpng)));
          const vl = run('node', [path.join(ws, 'reelbolt', 'check-layout.mjs'), `--composition=${vid}`, `--frames=${vf}`, '--min-text-px=12', '--output-scale=1'], {cwd: ws, env, timeout: 240000});
          const vline = (vl.stdout || '').split('\n').find((l) => l.startsWith('@@REELBOLT_LAYOUT@@'));
          let vi = null;
          try { vi = JSON.parse(vline.slice('@@REELBOLT_LAYOUT@@'.length)); } catch { /* reported below */ }
          const vlist = vi && (vi.issues ?? vi.findings ?? []);
          results.push({check: `layout vertical ${b.id}`, ok: !!vi && vlist.length === 0, detail: vi ? JSON.stringify(vlist).slice(0, 800) : (vl.stdout + vl.stderr).slice(-800)});
        }
      }
      const lint = run('node', [path.join(ws, 'reelbolt', 'check-layout.mjs'), `--composition=${pc.compositionId}`, `--frames=${pc.frames.join(',')}`, '--min-text-px=12', '--output-scale=1'], {cwd: ws, env, timeout: 240000});
      const line = (lint.stdout || '').split('\n').find((l) => l.startsWith('@@REELBOLT_LAYOUT@@'));
      let issues = null;
      try { issues = JSON.parse(line.slice('@@REELBOLT_LAYOUT@@'.length)); } catch { /* reported below */ }
      const list = issues && (issues.issues ?? issues.findings ?? []);
      results.push({check: `layout ${b.id}`, ok: !!issues && list.length === 0, detail: issues ? JSON.stringify(list).slice(0, 800) : (lint.stdout + lint.stderr).slice(-800)});
    }
  }
  console.log('@@VERIFY@@' + JSON.stringify(results));
  process.exit(results.every((r) => r.ok) ? 0 : 1);
}

function host() {
  const out = process.env.REELBOLT_VERIFY_OUT;
  if (out !== undefined && (out === '' || out.includes(':') || !path.isAbsolute(out))) { console.error('REELBOLT_VERIFY_OUT must be a non-empty absolute path without ":"'); process.exit(1); }
  const catalog = readCatalog();
  const problems = validateCatalog(catalog, loadAvailablePackages());
  if (problems.length) { console.error(problems.join('\n')); process.exit(1); }
  console.log('catalog checks ok');
  const image = arg('--image') ?? process.env.REELBOLT_SANDBOX_IMAGE ?? 'reelbolt-sandbox-runtime:local';
  const name = `reelbolt-blocks-verify-${process.pid}`;
  const args = ['run', '--rm', '--name', name,
    '--network', 'none', '--read-only', '--tmpfs', '/tmp:rw,nosuid,nodev,size=256m',
    '--memory', '2g', '--cpus', '2', '--pids-limit', '512', '--security-opt', 'no-new-privileges', '--cap-drop', 'ALL', '--user', 'node',
    '-v', `${BLOCKS_ROOT}:/blocks:ro`, ...(out ? ['-v', `${out}:/out`] : []), '--entrypoint', 'node', image, '/blocks/scripts/verify.mjs', '--in-container', ...(only ? ['--block', only] : []), ...(forceDeterminism ? ['--determinism'] : [])];
  const stop = () => spawnSync('docker', ['stop', '-t', '2', name], {stdio: 'ignore'});
  process.on('SIGINT', () => { stop(); process.exit(130); });
  const r = spawnSync('docker', args, {encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, timeout: 900000});
  if (r.error) stop();
  const line = (r.stdout || '').split('\n').find((l) => l.startsWith('@@VERIFY@@'));
  if (!line) { console.error((r.stdout || '') + (r.stderr || '')); process.exit(1); }
  const results = JSON.parse(line.slice('@@VERIFY@@'.length));
  for (const x of results) console.log(`${x.ok ? 'PASS' : 'FAIL'} ${x.check} ${x.detail}`);
  process.exit(results.every((x) => x.ok) ? 0 : 1);
}

if (process.argv.includes('--in-container')) inContainer();
else if (import.meta.url === `file://${process.argv[1]}`) host();
