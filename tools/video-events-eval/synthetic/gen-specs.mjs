// Writes specs/*.json (hand-designed timelines; colours/layouts differ per clip).
// Deterministic: the only randomness is a seeded PRNG used for small jitters.
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const W = 960, H = 540, FPS = 30;
const mulberry = (a) => () => { a |= 0; a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };

const rect = (id, name, x, y, w, h, color, o = {}) => ({ kind: 'rect', id, name, x, y, w, h, color, ...o });
const circ = (id, name, x, y, d, color, o = {}) => ({ kind: 'circle', id, name, x, y, w: d, h: d, color, ...o });
const text = (id, name, t, x, y, w, h, color, o = {}) => ({ kind: 'text', id, name, text: t, x, y, w, h, color, ...o });
const mv = (start, end, x, y) => ({ start, end, x, y });
const iv = (i, out, o = {}) => ({ in: i, ...(out != null ? { out } : {}), ...o });
const cursor = (id, x, y, vis, moves, clicks, o = {}) => ({ kind: 'cursor', id, name: 'cursor', x, y, w: 24, h: 24, vis, moves, clicks, ...o });
const ball = (id, name, x, y, d, color, release, floor, o = {}) => ({ kind: 'ball', id, name, x, y, w: d, h: d, color, release, floor, vis: [iv(0)], ...o });
const typed = (id, name, t, x, y, color, start, cpf, o = {}) => ({ kind: 'typed', id, name, text: t, x, y, w: t.length * 13, h: 28, color, cpf, vis: [iv(start, o.out ?? null, { fade: 0 })], ...o });

const clips = [];
const add = (c) => clips.push({ fps: FPS, width: W, height: H, ...c });

// ---------------- ANIMATION (voice-over) ----------------
add({
  clip: 'syn-anim-01', videoType: 'animation', frames: 360, theme: { bg: '#0b1d3a' },
  scenes: [
    { from: 0, bg: '#0b1d3a', bgB: '#12315e', deco: 'grid', accent: '#5ad1c8' },
    { from: 200, trans: 'cut', bg: '#e8742a', bgB: '#f2a65a', deco: 'stripes', accent: '#ffffff' },
  ],
  elements: [
    text('t1', 'title', 'Meet Bolt', 70, 60, 420, 60, '#ffffff', { scene: 0, skip: ['text_out'], vis: [iv(20, 120, { fade: 8 })] }),
    rect('card', 'card', 80, 200, 150, 100, '#5ad1c8', { scene: 0, label: 'CARD', textColor: '#0b1d3a', skip: ['disappear', 'move_stop'], vis: [iv(40, 190, { fade: 6 })], moves: [mv(60, 100, 520, 200)] }),
    ball('ball', 'ball', 700, 80, 60, '#ffd166', 140, 480, { scene: 0, vx: 0, e: 0.4, skip: ['move_start', 'move_stop'] }),
    
    { kind: 'char', id: 'ch', name: 'robot', x: 120, y: 280, w: 120, h: 190, color: '#0b1d3a', color2: '#fff3e0', scene: 1, skip: ['move_stop'], vis: [iv(215, null, { fade: 6 })], moves: [mv(222, 262, 560, 280)], hits: [{ kind: 'shake', frame: 280 }] },
    text('t2', 'done label', 'Done!', 420, 90, 260, 70, '#ffffff', { scene: 1, quiet: true, vis: [iv(300, 340, { fade: 8 })] }),
  ],
  speech: [{ start: 20, end: 100 }, { start: 232, end: 330 }],
});

add({
  clip: 'syn-anim-02', videoType: 'animation', frames: 420, theme: { bg: '#f4ead5' },
  scenes: [
    { from: 0, bg: '#f4ead5', bgB: '#fbe3b8', deco: 'dots', accent: '#c9822b', decoOpacity: 0.25 },
    { from: 150, trans: 'wipe', dur: 24, bg: '#c9f0dc', bgB: '#9fe0c0', deco: 'rings', accent: '#2f9e6e', decoOpacity: 0.2 },
    { from: 290, trans: 'fade', dur: 30, bg: '#d9cdfa', bgB: '#b9a6f2', deco: 'stripes', accent: '#6b4fd1', decoOpacity: 0.1 },
  ],
  elements: [
    text('t1', 'sale title', 'Spring Sale', 90, 50, 520, 70, '#5a3510', { scene: 0, skip: ['text_out'], vis: [iv(15, 140, { fade: 10 })] }),
    circ('sun', 'sun', 120, 250, 110, '#f5a623', { scene: 0, skip: ['disappear', 'move_start', 'move_stop'], vis: [iv(30, 145, { fade: 8 })], moves: [mv(50, 110, 700, 160)] }),
    ball('b1', 'ball', 330, 120, 56, '#d9534f', 60, 470, { scene: 0, e: 0.6, quiet: true }),
    { kind: 'char', id: 'ch', name: 'shopper', x: 100, y: 260, w: 110, h: 180, color: '#2f9e6e', color2: '#1d6b4a', scene: 1, skip: ['appear'], vis: [iv(170, null, { fade: 6 })], moves: [mv(185, 230, 480, 260), mv(238, 270, 480, 360)] },
    text('t2', 'thanks', 'Thanks!', 300, 200, 380, 80, '#3b2a8a', { scene: 2, skip: ['text_out'], vis: [iv(305, 395, { fade: 10 })] }),
    rect('stamp', 'stamp', 380, 330, 200, 90, '#6b4fd1', { scene: 2, label: 'SOLD', skip: ['appear'], vis: [iv(322, null, { fade: 0 })], hits: [{ kind: 'pop', frame: 322, amp: 0.5 }] }),
  ],
  speech: [{ start: 15, end: 95 }, { start: 300, end: 385 }],
});

add({
  clip: 'syn-anim-03', videoType: 'animation', frames: 300, theme: { bg: '#0f3d2e' },
  scenes: [
    { from: 0, bg: '#0f3d2e', bgB: '#1e6b4f', deco: 'dots', accent: '#b6f2c9', decoOpacity: 0.18 },
    { from: 210, trans: 'cut', bg: '#4a0f1a', bgB: '#7a1f2b', deco: 'rings', accent: '#ff9aa8', decoOpacity: 0.2 },
  ],
  camera: [
    { start: 40, end: 90, x: -120, y: -40, s: 1.5 },
    { start: 130, end: 190, x: 140, y: -20, s: 1.5 },
  ],
  elements: [
    rect('a', 'panel a', 160, 160, 200, 140, '#b6f2c9', { scene: 0, label: 'A', textColor: '#0f3d2e', vis: [iv(20)] }),
    rect('b', 'dim panel', 600, 330, 220, 120, '#1a5a43', { scene: 0, peak: 0.45, vis: [iv(100, 200, { fade: 12 })] }),
    ball('ball', 'ball', 450, 60, 54, '#ffe27a', 150, 480, { scene: 0, e: 0.5, quiet: true }),
    text('t', 'alert title', 'Alert', 330, 250, 300, 80, '#ffd6dc', { scene: 1, skip: ['text_out'], vis: [iv(216, 280, { fade: 8 })] }),
    rect('bar', 'bar', 200, 400, 560, 40, '#ff9aa8', { scene: 1, skip: ['appear'], vis: [iv(226, null, { fade: 6 })], hits: [{ kind: 'shake', frame: 240, amp: 20 }] }),
  ],
  speech: [{ start: 10, end: 70 }, { start: 218, end: 285 }],
});

add({
  clip: 'syn-anim-04', videoType: 'animation', frames: 480, theme: { bg: '#2a0a3a' },
  scenes: [
    { from: 0, bg: '#2a0a3a', bgB: '#5a1a78', deco: 'stripes', accent: '#ffe14d', decoOpacity: 0.08 },
    { from: 240, trans: 'fade', dur: 20, bg: '#ffe14d', bgB: '#ffc933', deco: 'grid', accent: '#2a0a3a', decoOpacity: 0.1 },
    { from: 380, trans: 'cut', bg: '#101820', bgB: '#1f3345', deco: 'dots', accent: '#ff3d9a', decoOpacity: 0.2 },
  ],
  elements: [
    rect('r1', 'magenta box', 80, 160, 130, 130, '#ff3d9a', { scene: 0, skip: ['appear', 'disappear'], vis: [iv(14, 150, { fade: 4 })], hits: [{ kind: 'shake', frame: 100 }, { kind: 'shake', frame: 108, amp: 10 }] }),
    rect('r2', 'yellow box', 300, 260, 130, 130, '#ffe14d', { scene: 0, textColor: '#2a0a3a', label: 'GO', skip: ['appear', 'disappear'], vis: [iv(60, 200, { fade: 4 })], moves: [mv(70, 90, 520, 260)] }),
    rect('banner', 'banner', 220, 30, 520, 70, '#00e0b8', { scene: 0, label: 'NEW ARRIVAL', textColor: '#2a0a3a', skip: ['disappear'], vis: [iv(30, 220, { slide: [0, -140], fade: 14, fadeOut: 14 })] }),
    ball('ball', 'ball', 760, 120, 50, '#ffffff', 130, 500, { scene: 0, e: 0.62, quiet: true }),
    { kind: 'char', id: 'ch', name: 'dancer', x: 160, y: 250, w: 100, h: 170, color: '#2a0a3a', color2: '#ff3d9a', scene: 1, skip: ['appear', 'disappear', 'move_stop'], vis: [iv(256, 370, { fade: 5 })], moves: [mv(268, 300, 640, 250)], hits: [{ kind: 'pop', frame: 330 }] },
    text('t', 'closing text', 'Fin', 380, 180, 200, 120, '#ff3d9a', { scene: 2, fontSize: 96, skip: ['text_out'], vis: [iv(385, 450, { fade: 6 })] }),
  ],
  speech: [{ start: 12, end: 105 }, { start: 262, end: 372 }],
});

// ---------------- SCREEN (no audio) ----------------
const chromeLight = { bar: '#dfe3e8', tabOn: '#ffffff', tabOff: '#c9ced6', url: '#ffffff', text: '#222' };
const chromeDark = { bar: '#1b1f27', tabOn: '#2b313c', tabOff: '#14171d', url: '#2b313c', text: '#d8dee9' };
const AREA = [0, 70, 960, 470];
const rows = (n, p) => Array.from({ length: n }, (_, i) => `${p} ${String(i + 1).padStart(2, '0')}`);

add({
  clip: 'syn-scr-01', videoType: 'screen', frames: 360, theme: { bg: '#dfe3e8' }, area: AREA, chrome: chromeLight,
  scenes: [
    { from: 0, tab: 'Dashboard', url: 'https://app.example.test/dash', bg: '#ffffff' },
    { from: 280, trans: 'cut', tab: 'Settings', url: 'https://app.example.test/settings', bg: '#eef2f7' },
  ],
  elements: [
    text('lab', 'email label', 'Email', 80, 110, 120, 30, '#444', { scene: 0, fontSize: 22, weight: 600, vis: [iv(0)] }),
    rect('inp', 'input', 80, 150, 420, 46, '#f4f6f8', { scene: 0, border: '2px solid #b8c0cc', radius: 8, vis: [iv(0)] }),
    typed('ty', 'typed email', 'hello@example.com', 94, 160, '#111', 30, 3, { scene: 0, fontSize: 22 }),
    rect('btn', 'submit button', 520, 150, 150, 46, '#2b7cff', { scene: 0, label: 'Send', skip: ['hit'], vis: [iv(0)], hits: [{ kind: 'press', frame: 100 }] }),
    rect('toast', 'toast', 700, 90, 230, 56, '#222', { scene: 0, label: 'Saved', fontSize: 20, vis: [iv(104, 190, { slide: [260, 0], fade: 10 })] }),
    { kind: 'list', id: 'ls', name: 'message list', x: 80, y: 240, w: 600, h: 400, rows: rows(14, 'Message'), rowH: 50, color: '#333', clip: [80, 240, 600, 270], scene: 0, vis: [iv(0)], moves: [mv(200, 260, 80, 90)] },
    rect('modal', 'dialog', 300, 190, 360, 200, '#ffffff', { scene: 1, border: '2px solid #2b7cff', label: 'Saved settings', textColor: '#222', fontSize: 24, vis: [iv(300, 345, { fade: 6 })] }),
    cursor('cur', 800, 400, [iv(0)], [mv(12, 30, 90, 175), mv(60, 88, 595, 175), mv(110, 135, 780, 440)], [100], { color: '#fff', skip: ['move_stop'] }),
  ],
});

add({
  clip: 'syn-scr-02', videoType: 'screen', frames: 300, theme: { bg: '#1b1f27' }, area: AREA, chrome: chromeDark,
  scenes: [
    { from: 0, tab: 'main.py', url: 'editor://project/main.py', bg: '#1e222a' },
    { from: 190, trans: 'cut', tab: 'notes.md', url: 'editor://project/notes.md', bg: '#272c36' },
    { from: 250, trans: 'cut', tab: 'run', url: 'editor://project/run', bg: '#0e1116' },
  ],
  elements: [
    rect('side', 'sidebar', 0, 70, 180, 470, '#161a20', { scene: 0, vis: [iv(0)] }),
    typed('l1', 'code line one', 'def main():', 200, 100, '#82aaff', 20, 2, { scene: 0, mono: true }),
    typed('l2', 'code line two', '    print("ok")', 200, 135, '#c3e88d', 52, 2, { scene: 0, mono: true }),
    rect('pop', 'autocomplete popup', 330, 170, 260, 110, '#2f3542', { scene: 0, border: '1px solid #555', label: 'print()', fontSize: 20, vis: [iv(96, 150, { fade: 4 })] }),
    typed('n1', 'notes text', '# Notes', 200, 100, '#ffcb6b', 194, 4, { scene: 1, mono: true }),
    text('ok', 'run result', 'Process finished', 200, 120, 400, 30, '#c3e88d', { scene: 2, fontSize: 24, vis: [iv(258, null, { fade: 3 })] }),
    cursor('cur', 700, 450, [iv(0)], [mv(6, 24, 215, 112), mv(160, 178, 130, 74)], [90, 100, 182], { color: '#fff', color2: '#ffcb6b', skip: ['move_stop'] }),
  ],
});

add({
  clip: 'syn-scr-03', videoType: 'screen', frames: 480, theme: { bg: '#cfd8e8' }, area: AREA, chrome: chromeLight,
  scenes: [
    { from: 0, tab: 'Inbox', url: 'https://mail.example.test/inbox', bg: '#f3f7fd' },
    { from: 240, trans: 'wipe', dur: 18, tab: 'Orders', url: 'https://mail.example.test/orders', bg: '#fdf6ec' },
  ],
  elements: [
    { kind: 'list', id: 'ls', name: 'inbox list', x: 60, y: 100, w: 700, h: 900, rows: rows(24, 'Subject line'), rowH: 54, color: '#2b3a55', clip: [60, 100, 700, 400], scene: 0, vis: [iv(0)], skip: ['move_stop'], moves: [mv(40, 100, 60, -200), mv(130, 160, 60, -420)] },
    rect('tip', 'low contrast tooltip', 600, 400, 220, 50, '#c8d3e6', { scene: 0, peak: 0.45, label: 'Hint', textColor: '#2b3a55', vis: [iv(170, 220, { fade: 14 })] }),
    rect('toast', 'toast', 640, 90, 280, 56, '#2b3a55', { scene: 0, label: 'New mail', vis: [iv(185, 232, { slide: [0, -200], fade: 8 })] }),
    text('h', 'orders heading', 'Orders', 70, 100, 300, 50, '#7a4a10', { scene: 1, fontSize: 40, vis: [iv(262, null, { fade: 6 })] }),
    rect('chip', 'status chip', 70, 180, 160, 50, '#e8a33d', { scene: 1, label: 'Shipped', skip: ['disappear'], vis: [iv(300, 420, { fade: 0 })], hits: [{ kind: 'pop', frame: 330 }] }),
    typed('ty', 'search typing', 'order 4471', 300, 190, '#7a4a10', 360, 3, { scene: 1, fontSize: 26, quiet: true }),
    cursor('cur', 800, 450, [iv(0)], [mv(20, 40, 400, 300), mv(200, 225, 300, 210)], [228], { color: '#fff', skip: ['move_stop'] }),
  ],
});

add({
  clip: 'syn-scr-04', videoType: 'screen', frames: 400, theme: { bg: '#14231a' }, area: AREA, chrome: { bar: '#0f1a13', tabOn: '#1f3326', tabOff: '#0b130e', url: '#1f3326', text: '#b9f6ca' },
  scenes: [{ from: 0, tab: 'terminal', url: 'term://local', bg: '#0b130e', bgB: '#0f1d14' }],
  elements: [
    typed('c1', 'first command', '$ make build', 40, 100, '#b9f6ca', 15, 3, { scene: 0, mono: true, fontSize: 24 }),
    text('o1', 'output line one', 'compiling ...', 40, 140, 300, 28, '#6fbf8a', { scene: 0, fontSize: 22, vis: [iv(60, null, { fade: 0 })] }),
    text('o2', 'output line two', 'linking ...', 40, 170, 300, 28, '#6fbf8a', { scene: 0, fontSize: 22, vis: [iv(66, null, { fade: 0 })] }),
    text('o3', 'output line three', 'done', 40, 200, 300, 28, '#ffffff', { scene: 0, fontSize: 22, vis: [iv(72, 160, { fade: 0 })] }),
    typed('c2', 'second command', '$ make test', 40, 240, '#b9f6ca', 170, 3, { scene: 0, mono: true, fontSize: 24 }),
    rect('badge', 'pass badge', 700, 110, 180, 60, '#1fa85a', { scene: 0, label: 'PASS', skip: ['disappear'], vis: [iv(250, 330, { fade: 8 })], hits: [{ kind: 'pop', frame: 250, amp: 0.3 }] }),
    rect('ghost', 'faint hint', 700, 300, 200, 50, '#17301f', { scene: 0, peak: 0.5, label: 'idle', fontSize: 18, skip: ['disappear'], vis: [iv(280, 360, { fade: 20 })] }),
    cursor('cur', 850, 480, [iv(0)], [mv(120, 150, 780, 140), mv(300, 330, 760, 330)], [150, 340], { color: '#fff', color2: '#b9f6ca', skip: ['move_stop'] }),
  ],
});

// ---------------- SILENT (sparse, long idle stretches) ----------------
add({
  clip: 'syn-sil-01', videoType: 'silent', frames: 540, theme: { bg: '#1c2430' },
  scenes: [{ from: 0, bg: '#1c2430', bgB: '#26323f', deco: 'dots', accent: '#8fa6bd', decoOpacity: 0.08 }], drift: true,
  elements: [
    text('lab', 'faint label', 'quiet harbour', 260, 220, 460, 60, '#9fb3c8', { peak: 0.55, fontSize: 52, scene: 0, vis: [iv(150, 500, { fade: 50, fadeOut: 40 })] }),
    circ('dot', 'lone dot', 120, 420, 36, '#d9a441', { scene: 0, vis: [iv(0)], moves: [mv(400, 470, 780, 420)] }),
  ],
});

add({
  clip: 'syn-sil-02', videoType: 'silent', frames: 600, theme: { bg: '#2b1b12' },
  scenes: [
    { from: 0, bg: '#2b1b12', bgB: '#3d281a', deco: 'grid', accent: '#caa27a', decoOpacity: 0.1 },
    { from: 480, trans: 'fade', dur: 60, bg: '#1a2b24', bgB: '#244036', deco: 'grid', accent: '#7ac2a0', decoOpacity: 0.1 },
  ],
  camera: [{ start: 200, end: 420, x: -150, y: 20, s: 1.12 }], drift: true,
  elements: [
    rect('card', 'amber card', 300, 190, 240, 150, '#caa27a', { scene: 0, label: 'Archive', textColor: '#2b1b12', vis: [iv(90, null, { fade: 10 })] }),
    circ('o', 'small orb', 700, 120, 44, '#e6c9a8', { scene: 0, peak: 0.6, vis: [iv(0)] }),
    text('t', 'closing caption', 'later', 360, 400, 240, 50, '#bfe6d2', { scene: 1, fontSize: 44, vis: [iv(520, null, { fade: 40 })] }),
  ],
});

add({
  clip: 'syn-sil-03', videoType: 'silent', frames: 450, theme: { bg: '#efe9df' },
  scenes: [{ from: 0, bg: '#efe9df', bgB: '#e3dccd', deco: 'stripes', accent: '#b9ad97', decoOpacity: 0.12 }], drift: true,
  elements: [
    rect('box', 'slow box', 200, 200, 220, 140, '#8a7f6a', { scene: 0, peak: 0.6, vis: [iv(100, null, { fade: 60 })], hits: [{ kind: 'pop', frame: 330, amp: 0.15 }], moves: [mv(380, 440, 520, 200)] }),
    circ('c', 'pale circle', 700, 380, 80, '#c9bfa8', { scene: 0, vis: [iv(0)] }),
  ],
});

add({
  clip: 'syn-sil-04', videoType: 'silent', frames: 570, theme: { bg: '#1f3b3a' },
  scenes: [
    { from: 0, bg: '#1f3b3a', bgB: '#244b49', deco: 'rings', accent: '#6fd0c4', decoOpacity: 0.07 },
    { from: 440, trans: 'cut', bg: '#27484a', bgB: '#2d5557', deco: 'rings', accent: '#6fd0c4', decoOpacity: 0.07 },
  ], drift: true,
  elements: [
    circ('c', 'teal circle', 140, 300, 90, '#6fd0c4', { scene: 0, vis: [iv(60, null, { fade: 30 })], moves: [mv(200, 330, 620, 300)] }),
    text('t', 'slow caption', 'still waters', 280, 120, 440, 60, '#c8f0ea', { scene: 0, fontSize: 50, vis: [iv(400, 530, { fade: 30 })] }),
  ],
});

// ---------------- SPEECH / MUSIC (audio is the point; the picture is a static scene) ----------------
// speech: talking-head-like static picture + the synthetic voice-over; labels are speech_start/end only and
// expect.noBeats says the beat detector must stay silent. music: static picture + steady kick and chord pad;
// labels are the kick onsets (beat), one per beat from `first` seconds.
const kicks = (bpm, first, frames) => {
  const out = [];
  for (let t = first; Math.round(t * FPS) < frames - 8; t += 60 / bpm) out.push(Math.round(t * FPS));
  return out;
};
add({ clip: 'syn-speech-01', videoType: 'speech', frames: 720, theme: { bg: '#2b2f3a' }, scenes: [], elements: [], expect: { noBeats: true },
  speech: [{ start: 20, end: 150 }, { start: 200, end: 330 }, { start: 380, end: 520 }, { start: 570, end: 700 }] });
add({ clip: 'syn-speech-02', videoType: 'speech', frames: 720, theme: { bg: '#3a2f2b' }, scenes: [], elements: [], expect: { noBeats: true },
  speech: [{ start: 40, end: 120 }, { start: 150, end: 290 }, { start: 340, end: 420 }, { start: 470, end: 600 }, { start: 640, end: 710 }] });
add({ clip: 'syn-mus-01', videoType: 'music', frames: 720, theme: { bg: '#241f3d' }, scenes: [], elements: [],
  audio: { kind: 'music', bpm: 120, first: 0.4, chord: [220, 277.18, 329.63] }, beats: kicks(120, 0.4, 720) });
add({ clip: 'syn-mus-02', videoType: 'music', frames: 720, theme: { bg: '#1f3d33' }, scenes: [], elements: [],
  audio: { kind: 'music', bpm: 128, first: 0.7, chord: [196, 246.94, 293.66, 392] }, beats: kicks(128, 0.7, 720) });

mkdirSync(join(here, 'specs'), { recursive: true });
void mulberry; // reserved for jitter; timelines are explicit so labels stay hand-auditable
for (const c of clips) writeFileSync(join(here, 'specs', `${c.clip}.json`), JSON.stringify(c, null, 1) + '\n');
console.log(`wrote ${clips.length} specs`);
