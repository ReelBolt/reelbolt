// Renders the transactional-email logo raster from the shipped brand lockup (WP G2b).
//
// Email clients block or half-support SVG, so the Area C HTML mail templates cannot reference
// brand/lockup-horizontal-mono.svg directly. This produces `site/public/brand/email-logo.png`,
// served by /site at https://reelbolt.ai/brand/email-logo.png, and docs/brand.md carries the
// header snippet the templates paste in.
//
// Three deliberate choices:
//   - The MONO lockup, resolved to `#0B0B12` (the `paper` token). The full-colour lockup is for
//     dark surfaces and its wordmark is #F5F3F8, which is invisible on the white card every
//     template draws. docs/brand.md's colour rule: "Black (#000, or #0B0B12) on light backgrounds".
//   - 240x48 pixels, the 2x asset for the 120x24 CSS box the templates declare — the usual
//     email-header size.
//   - The lockup is CONTAINED, never stretched, and the transparent margin inside its own viewBox
//     is trimmed off first so the ink fills the box's height and sits flush at the left edge, lining
//     up with the body copy the way the plain-text "ReelBolt" header it replaces did.
//
// Run:  node brand/scripts/render-email-logo.mjs
// Requires `sharp`. Run `cd brand && npm install` once (brand/package.json declares it).
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import sharp from 'sharp';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const sourceLockup = path.join(repoRoot, 'brand', 'lockup-horizontal-mono.svg');
const outDir = path.join(repoRoot, 'site', 'public', 'brand');
const outFile = path.join(outDir, 'email-logo.png');

/** The raster's pixel size: 2x of the 120x24 box the mail templates declare. */
const WIDTH = 240;
const HEIGHT = 48;

/** The lockup's single colour, resolved for a white email card. */
const INK = '#0B0B12';

const svg = await readFile(sourceLockup, 'utf8');
if (!svg.includes('currentColor')) {
  throw new Error(`${sourceLockup} no longer uses currentColor; this script's recolouring is now wrong.`);
}
// Only the resolved colour changes; every geometry attribute is the shipped lockup's.
const recoloured = svg.replaceAll('currentColor', INK);

// Rasterize large, drop the lockup's own empty margin, then fit what is left into the output box.
const ink = await sharp(Buffer.from(recoloured), { density: 1200 })
  .trim({ threshold: 1 })
  .png()
  .toBuffer();
const inkMeta = await sharp(ink).metadata();

await mkdir(outDir, { recursive: true });
const png = await sharp(ink)
  .resize(WIDTH, HEIGHT, {
    fit: 'contain',
    position: 'west',
    background: { r: 0, g: 0, b: 0, alpha: 0 },
  })
  .png({ compressionLevel: 9 })
  .toBuffer();
await writeFile(outFile, png);

console.log(
  `lockup ink ${inkMeta.width}x${inkMeta.height} -> ${path.relative(repoRoot, outFile)} ` +
    `(${WIDTH}x${HEIGHT}, ${png.length} bytes)`,
);
