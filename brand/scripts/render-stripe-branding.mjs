// Renders the two rasters Stripe Checkout and the customer portal show for ReelBolt.
//
// Stripe takes a logo and an icon as raster images (PNG or JPG), either uploaded as a File or fetched
// from a public URL (`branding_settings[logo][url]`, `branding_settings[icon][url]` on a Checkout Session).
// The site serves /brand/*, so these two files are the URLs the billing config points at:
//   https://reelbolt.ai/brand/stripe-logo.png   horizontal lockup, full colour, transparent background
//   https://reelbolt.ai/brand/stripe-icon.png   the app icon (mark on #2E1065), 512 x 512
// and one file that is NOT used by the per-session config, only uploaded by hand in the Stripe Dashboard
// (account branding, shown on light surfaces: invoices, receipts, emails, the customer portal):
//   site/public/brand/stripe-logo-light.png     horizontal lockup in one colour (#0B0B12), transparent
//
// The artwork is never redrawn: the lockup is brand/lockup-horizontal.svg and the icon brand/app-icon.svg,
// rasterized as they are. The full-colour lockup is for dark surfaces (its wordmark is #F5F3F8), which is
// why the Checkout page background is the `paper` token (#0B0B12). See docs/brand.md, "Stripe Checkout".
//
// Run:  node brand/scripts/render-stripe-branding.mjs
// Requires `sharp` (cd brand && npm install).
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import sharp from 'sharp';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const outDir = path.join(repoRoot, 'site', 'public', 'brand');

/** Icon: square, 512 px, the app icon exactly as shipped. */
const ICON_SIZE = 512;
/** Logo: 3x the 360 x 96 box Checkout draws it in, so it stays sharp on high-density screens. */
const LOGO_WIDTH = 1080;
const LOGO_HEIGHT = 288;

await mkdir(outDir, { recursive: true });

/** docs/brand.md "Clear space": an empty margin of 25 % of the mark's height on every side of the lockup. */
async function withClearSpace(trimmedPng) {
  const { height } = await sharp(trimmedPng).metadata();
  const pad = Math.round(height * 0.25);
  return sharp(trimmedPng)
    .extend({ top: pad, bottom: pad, left: pad, right: pad, background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .png()
    .toBuffer();
}

const iconSvg = await readFile(path.join(repoRoot, 'brand', 'app-icon.svg'));
const icon = await sharp(iconSvg, { density: 600 })
  .resize(ICON_SIZE, ICON_SIZE, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
  .png({ compressionLevel: 9 })
  .toBuffer();
await writeFile(path.join(outDir, 'stripe-icon.png'), icon);

const lockupSvg = await readFile(path.join(repoRoot, 'brand', 'lockup-horizontal.svg'));
// Drop the lockup's own transparent margin, add the brand's 25 % clear space around the ink, then
// contain the result in the output box.
const ink = await sharp(lockupSvg, { density: 1200 }).trim({ threshold: 1 }).png().toBuffer();
const logo = await sharp(await withClearSpace(ink))
  .resize(LOGO_WIDTH, LOGO_HEIGHT, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
  .png({ compressionLevel: 9 })
  .toBuffer();
await writeFile(path.join(outDir, 'stripe-logo.png'), logo);

// Light surfaces: the mono lockup in the `paper` token (docs/brand.md: black on light backgrounds).
const monoSvg = (await readFile(path.join(repoRoot, 'brand', 'lockup-horizontal-mono.svg'), 'utf8')).replaceAll('currentColor', '#0B0B12');
const monoInk = await sharp(Buffer.from(monoSvg), { density: 1200 }).trim({ threshold: 1 }).png().toBuffer();
const logoLight = await sharp(await withClearSpace(monoInk))
  .resize(LOGO_WIDTH, LOGO_HEIGHT, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
  .png({ compressionLevel: 9 })
  .toBuffer();
await writeFile(path.join(outDir, 'stripe-logo-light.png'), logoLight);
console.log(`stripe-logo-light.png ${LOGO_WIDTH}x${LOGO_HEIGHT} ${logoLight.length} bytes`);

const iconMeta = await sharp(icon).metadata();
const logoMeta = await sharp(logo).metadata();
console.log(`stripe-icon.png ${iconMeta.width}x${iconMeta.height} ${icon.length} bytes`);
console.log(`stripe-logo.png ${logoMeta.width}x${logoMeta.height} ${logo.length} bytes`);
