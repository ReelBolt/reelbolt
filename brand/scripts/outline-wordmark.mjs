// Outlines "ReelBolt" in Chakra Petch Bold (OFL, brand/fonts/) to SVG paths, so no <text>
// element and no installed font is ever needed. Writes brand/wordmark.svg,
// brand/lockup-horizontal.svg and brand/lockup-horizontal-mono.svg.
// Run: `node brand/scripts/outline-wordmark.mjs` (needs `npm install` in brand/).
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import opentype from 'opentype.js';

const brandDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const TEXT = 'ReelBolt';
const TRACKING = 12; // font units (1000/em): a hair of positive tracking for a display weight

const buf = await readFile(path.join(brandDir, 'fonts', 'ChakraPetch-Bold.ttf'));
const font = opentype.parse(buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength));
const upm = font.unitsPerEm;
const SIZE = 100; // px per em for the master; the viewBox is derived from the real bounds

const scale = SIZE / upm;
const glyphs = font.stringToGlyphs(TEXT);
let x = 0;
const commands = [];
glyphs.forEach((g, i) => {
  const p = g.getPath(x * scale, 0, SIZE);
  commands.push(...p.commands);
  x += g.advanceWidth;
  if (i < glyphs.length - 1) x += font.getKerningValue(g, glyphs[i + 1]) + TRACKING;
});
// opentype.js 2.0.0 roundDecimal() returns NaN for coordinates whose fractional part is below
// 1e-6 (e.g. 182.00000000000003), so snap the float noise away before serialising.
const snap = (n) => (typeof n === 'number' ? Math.round(n * 1e4) / 1e4 : n);
const path0 = new opentype.Path();
path0.commands = commands.map((c) => Object.fromEntries(Object.entries(c).map(([k, v]) => [k, snap(v)])));
const bb = path0.getBoundingBox();
const capHeight = font.tables.os2.sCapHeight * scale;

const r = (n) => Number(n.toFixed(2));
const d = path0.toPathData(2);
const w = r(bb.x2 - bb.x1);
const h = r(bb.y2 - bb.y1);
const dx = r(-bb.x1);
const dy = r(-bb.y1);

const wordmark = `<svg viewBox="0 0 ${w} ${h}" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="ReelBolt">
  <path transform="translate(${dx} ${dy})" fill="currentColor" d="${d}" />
</svg>
`;

// Lockup: the 120-unit mark box on the left, wordmark cap-height = CAP units, vertically
// centred on the mark's optical centre (y = 60), 22 units of gap.
const CAP = 46;
const k = CAP / capHeight;
const GAP = 22;
const tx = r(120 + GAP - bb.x1 * k);
const ty = r(60 + CAP / 2); // baseline y (glyph coords are y-down around baseline 0)
const totalW = r(120 + GAP + w * k);

const markBody = (R, B, mask) => `  <mask id="cut" maskUnits="userSpaceOnUse" x="0" y="0" width="120" height="120">
    <rect width="120" height="120" fill="#fff" />
    <path d="M68 12 L28 68 H54 L46 108 L92 48 H64 Z" fill="#000" stroke="#000" stroke-width="5" stroke-linejoin="round" transform="translate(66 80) scale(-.68 .68) translate(-60 -60)" />
  </mask>
  <path d="M36 96V24H62a21 21 0 0 1 0 42H36" fill="none" stroke="${R}" stroke-width="14" stroke-linejoin="round" mask="url(#cut)" />
  <path d="M68 12 L28 68 H54 L46 108 L92 48 H64 Z" fill="${B}" transform="translate(66 80) scale(-.68 .68) translate(-60 -60)" />`;

const lockup = (R, B, text) => `<svg viewBox="0 0 ${totalW} 120" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="ReelBolt">
${markBody(R, B)}
  <path transform="translate(${tx} ${ty}) scale(${r(k)})" fill="${text}" d="${d}" />
</svg>
`;

await writeFile(path.join(brandDir, 'wordmark.svg'), wordmark);
await writeFile(path.join(brandDir, 'lockup-horizontal.svg'), lockup('#C4B5FD', '#A855F7', '#F5F3F8'));
await writeFile(path.join(brandDir, 'lockup-horizontal-mono.svg'), lockup('currentColor', 'currentColor', 'currentColor'));
console.log(`wordmark ${w}x${h}, lockup ${totalW}x120`);
