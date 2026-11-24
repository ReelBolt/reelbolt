// Renders the trial-watermark raster from the shipped brand mark (WP B8b).
//
// ffmpeg's `overlay` filter takes a BITMAP, and brand/ ships SVG only, so the engine cannot use the
// mark directly. This produces `inference/assets/watermark.png` — a single-colour (white) mark on a
// transparent field — which the WorkflowEngine image COPYs to /app/assets/ and
// `VideoEditing:WatermarkAssetPath` points at.
//
// White, not the brand's dark ink: a watermark is composited over arbitrary footage, and the mark is
// the only thing that has to read against both bright and dark frames. Shape and proportions are the
// shipped mark's, unchanged — only `currentColor` is resolved, because an SVG left unresolved
// rasterizes as black.
//
// Run:  node brand/scripts/render-watermark.mjs
// Requires `sharp` (resolved from web/node_modules, already a devDependency there).
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import sharp from 'sharp';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const sourceMark = path.join(repoRoot, 'brand', 'mark-mono.svg');
const outDir = path.join(repoRoot, 'inference', 'assets');
const outFile = path.join(outDir, 'watermark.png');

/** Rendered width/height of the watermark raster, in pixels. */
const SIZE = 512;

/** The mark's single colour, resolved for compositing over video. */
const INK = '#FFFFFF';

const svg = await readFile(sourceMark, 'utf8');
if (!svg.includes('currentColor')) {
  throw new Error(`${sourceMark} no longer uses currentColor; this script's recolouring is now wrong.`);
}
// Only the resolved colour changes; every geometry attribute is the shipped mark's.
const recoloured = svg.replaceAll('currentColor', INK);

await mkdir(outDir, { recursive: true });
const png = await sharp(Buffer.from(recoloured), { density: 384 })
  .resize(SIZE, SIZE, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
  .png({ compressionLevel: 9 })
  .toBuffer();
await writeFile(outFile, png);
console.log(`wrote ${path.relative(repoRoot, outFile)} (${SIZE}x${SIZE}, ${png.length} bytes)`);

