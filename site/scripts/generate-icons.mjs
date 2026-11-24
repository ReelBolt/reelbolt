// Generates PNG favicons from site/assets/logo.svg into site/public/.
// Run via `npm run icons`. Requires `sharp` (rasterizing) and `png-to-ico` (combining the 16/32
// PNGs into a classic favicon.ico) as devDependencies — see CLAUDE.md-adjacent notes in the
// Phase 3 plan for the ImageResponse fallback if either fails to install/run in a given environment.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import sharp from 'sharp';
import pngToIco from 'png-to-ico';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const siteRoot = path.resolve(__dirname, '..');
const logoPath = path.join(siteRoot, 'assets', 'logo.svg');
// Full-bleed square with the glyph already inside the maskable safe zone (synced from
// brand/app-icon-maskable.svg), so it is rasterised as-is rather than padded here.
const maskableLogoPath = path.join(siteRoot, 'assets', 'logo-maskable.svg');
const publicDir = path.join(siteRoot, 'public');

const targets = [
  { file: 'favicon-16.png', size: 16 },
  { file: 'favicon-32.png', size: 32 },
  { file: 'apple-touch-icon.png', size: 180 },
  { file: 'favicon-192.png', size: 192 },
  { file: 'favicon-512.png', size: 512 },
];

async function main() {
  await mkdir(publicDir, { recursive: true });
  const svg = await readFile(logoPath);

  for (const { file, size } of targets) {
    const outPath = path.join(publicDir, file);
    await sharp(svg, { density: 384 }).resize(size, size).png().toFile(outPath);
    console.log(`wrote ${path.relative(siteRoot, outPath)} (${size}x${size})`);
  }

  const maskableOut = path.join(publicDir, 'favicon-512-maskable.png');
  await sharp(await readFile(maskableLogoPath), { density: 384 }).resize(512, 512).png().toFile(maskableOut);
  console.log(`wrote ${path.relative(siteRoot, maskableOut)} (512x512, maskable)`);

  const icoBuffer = await pngToIco([
    path.join(publicDir, 'favicon-16.png'),
    path.join(publicDir, 'favicon-32.png'),
  ]);
  const icoPath = path.join(publicDir, 'favicon.ico');
  await writeFile(icoPath, icoBuffer);
  console.log(`wrote ${path.relative(siteRoot, icoPath)}`);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
