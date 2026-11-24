// Usage: node generate.mjs [--labels-only] [--no-audio] [clip ...]
// Writes labels + sidecars (committed) and renders videos to ${VEA_DATA:-~/ve-data}/synthetic/<clip>.mp4.
import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { computeEvents } from './src/model.js';

const here = dirname(fileURLToPath(import.meta.url));
const labelDir = resolve(here, '..', 'labelled', 'synthetic');
const dataDir = join(process.env.VEA_DATA || join(homedir(), 've-data'), 'synthetic');
const args = process.argv.slice(2);
const labelsOnly = args.includes('--labels-only');
const wanted = args.filter((a) => !a.startsWith('--'));
const browser = process.env.VEA_BROWSER || '/usr/bin/google-chrome';

const all = readdirSync(join(here, 'specs')).filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5)).sort();
const clips = wanted.length ? wanted : all;

// ---- deterministic synthetic voice-over: band-limited noise, ~4.5 Hz syllable AM, pitch-ish hum ----
function mulberry(a) { return () => { a |= 0; a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
function makeWav(spec, path) {
  const sr = 22050, n = Math.ceil((spec.frames / spec.fps) * sr);
  const buf = new Float32Array(n);
  const rnd = mulberry([...spec.clip].reduce((a, c) => a * 31 + c.charCodeAt(0), 7));
  for (const ph of spec.speech) {
    const s0 = Math.round((ph.start / spec.fps) * sr), s1 = Math.round((ph.end / spec.fps) * sr);
    const rate = 3.8 + rnd() * 1.6, f0 = 110 + rnd() * 90;
    let lp1 = 0, lp2 = 0;
    for (let i = s0; i < s1 && i < n; i++) {
      const t = (i - s0) / sr;
      const white = rnd() * 2 - 1;
      lp1 += 0.45 * (white - lp1); lp2 += 0.04 * (white - lp2);
      const band = lp1 - lp2; // roughly 200 Hz .. 3 kHz
      const syl = Math.max(0, Math.sin(2 * Math.PI * rate * t)) ** 0.7; // syllable pulses
      const hum = 0.5 * Math.sin(2 * Math.PI * f0 * t) + 0.25 * Math.sin(2 * Math.PI * 2 * f0 * t);
      const att = Math.min(1, (i - s0) / (0.008 * sr)), rel = Math.min(1, (s1 - i) / (0.008 * sr));
      buf[i] = (0.9 * band * 1.6 + 0.35 * hum) * (0.15 + 0.85 * syl) * att * rel * 0.45;
    }
  }
  writeWav(buf, sr, path);
}

function writeWav(buf, sr, path) {
  const n = buf.length;
  const out = Buffer.alloc(44 + n * 2);
  out.write('RIFF', 0); out.writeUInt32LE(36 + n * 2, 4); out.write('WAVEfmt ', 8);
  out.writeUInt32LE(16, 16); out.writeUInt16LE(1, 20); out.writeUInt16LE(1, 22);
  out.writeUInt32LE(sr, 24); out.writeUInt32LE(sr * 2, 28); out.writeUInt16LE(2, 32); out.writeUInt16LE(16, 34);
  out.write('data', 36); out.writeUInt32LE(n * 2, 40);
  for (let i = 0; i < n; i++) out.writeInt16LE(Math.round(Math.max(-1, Math.min(1, buf[i])) * 32767), 44 + i * 2);
  writeFileSync(path, out);
}

// ---- deterministic music-like bed: a kick (pitch-dropped sine) on every beat plus a slowly pulsing chord pad ----
function makeMusicWav(spec, path) {
  const sr = 22050, n = Math.ceil((spec.frames / spec.fps) * sr), a = spec.audio;
  const buf = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const t = i / sr;
    let pad = 0; for (const f of a.chord) pad += Math.sin(2 * Math.PI * f * t);
    buf[i] = 0.07 * pad * (0.8 + 0.2 * Math.sin(2 * Math.PI * 0.25 * t)) * Math.min(1, t / 0.05); // 50 ms fade-in: no click at the start
  }
  for (let b = a.first; b < spec.frames / spec.fps - 0.25; b += 60 / a.bpm) {
    const s0 = Math.round(b * sr);
    for (let k = 0; k < sr / 5 && s0 + k < n; k++) {
      const t = k / sr;
      buf[s0 + k] += 0.8 * Math.sin(2 * Math.PI * (50 * t + (35 * (1 - Math.exp(-30 * t))) / 30)) * Math.exp(-18 * t) * Math.min(1, (sr / 5 - k) / (0.02 * sr)); // 20 ms release: the kick does not end on a click
    }
  }
  writeWav(buf, sr, path);
}

mkdirSync(labelDir, { recursive: true });
mkdirSync(dataDir, { recursive: true });
const tmp = join(dataDir, '.tmp'); mkdirSync(tmp, { recursive: true });

for (const clip of clips) {
  const specPath = join(here, 'specs', `${clip}.json`);
  if (!existsSync(specPath)) { console.error(`no spec ${clip}`); process.exit(2); }
  const spec = JSON.parse(readFileSync(specPath, 'utf8'));
  const events = computeEvents(spec);

  writeFileSync(join(labelDir, `${clip}.labels.json`), JSON.stringify({
    clip, videoType: spec.videoType, source: 'synthetic', ...(spec.expect ? { expect: spec.expect } : {}), fps: spec.fps, frames: spec.frames,
    width: spec.width, height: spec.height,
    events: events.map((e) => ({ id: e.id, type: e.type, frame: e.frame, ...(e.region ? { region: e.region } : {}), note: e.note })),
  }, null, 1) + '\n');

  // DeclareRenderEvents shape (RenderEventManifest): names [a-z0-9_-]{1,40}, labels without digits-plus-unit text.
  writeFileSync(join(labelDir, `${clip}.events.json`), JSON.stringify({
    version: 1, fps: spec.fps,
    events: events.map((e) => ({ name: `${e.type}-${e.id}`, frame: e.frame, label: e.type.replace(/_/g, ' ') })),
  }, null, 1) + '\n');
  console.log(`${clip}: ${events.length} events`);
  if (labelsOnly) continue;

  const t0 = Date.now();
  if (spec.videoType === 'speech' || spec.videoType === 'music') {
    // Static picture (no Remotion): a flat background with a head-and-shoulders block, then the audio bed.
    const final = join(dataDir, `${clip}.mp4`), wav = join(tmp, `${clip}.wav`);
    if (spec.videoType === 'speech') makeWav(spec, wav); else makeMusicWav(spec, wav);
    const bg = spec.theme.bg.replace('#', '0x');
    const m = spawnSync('ffmpeg', ['-y', '-v', 'error', '-f', 'lavfi', '-i', `color=c=${bg}:s=${spec.width}x${spec.height}:r=${spec.fps}:d=${spec.frames / spec.fps}`, '-i', wav,
      '-vf', `drawbox=x=380:y=260:w=200:h=280:color=0x8a7f6a:t=fill,drawbox=x=420:y=110:w=120:h=140:color=0xc9b8a0:t=fill`,
      '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '96k', '-shortest', final], { stdio: 'inherit' });
    if (m.status !== 0) process.exit(1);
    console.log(`${clip}: rendered in ${((Date.now() - t0) / 1000).toFixed(0)}s -> ${final}`);
    continue;
  }
  const raw = join(tmp, `${clip}.video.mp4`), final = join(dataDir, `${clip}.mp4`);
  const r = spawnSync('nice', ['-n', '10', 'npx', 'remotion', 'render', 'src/index.ts', 'Main', raw,
    `--props=${specPath}`, '--concurrency=2', `--browser-executable=${browser}`, '--log=error', '--overwrite'],
  { cwd: here, stdio: 'inherit' });
  if (r.status !== 0) { console.error(`render failed: ${clip}`); process.exit(1); }
  if (spec.videoType === 'animation' && !args.includes('--no-audio')) {
    const wav = join(tmp, `${clip}.wav`); makeWav(spec, wav);
    const m = spawnSync('ffmpeg', ['-y', '-v', 'error', '-i', raw, '-i', wav, '-c:v', 'copy', '-c:a', 'aac', '-b:a', '96k', '-shortest', final], { stdio: 'inherit' });
    if (m.status !== 0) process.exit(1);
  } else {
    const m = spawnSync('ffmpeg', ['-y', '-v', 'error', '-i', raw, '-c', 'copy', '-an', final], { stdio: 'inherit' });
    if (m.status !== 0) process.exit(1);
  }
  rmSync(raw, { force: true });
  console.log(`${clip}: rendered in ${((Date.now() - t0) / 1000).toFixed(0)}s -> ${final}`);
}
