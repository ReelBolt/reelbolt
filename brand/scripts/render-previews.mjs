// Renders brand SVGs to PNG contact sheets for eyeballing (not committed).
// Usage: node brand/scripts/render-previews.mjs <outDir>
import { readFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import sharp from 'sharp';

const brandDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.resolve(process.argv[2] ?? 'brand-previews');
await mkdir(out, { recursive: true });

const render = async (name, bg, width, tint) => {
  let svg = (await readFile(path.join(brandDir, name), 'utf8'));
  if (tint) svg = svg.replaceAll('currentColor', tint);
  const png = await sharp(Buffer.from(svg), { density: 300 }).resize({ width }).png().toBuffer();
  const m = await sharp(png).metadata();
  const pad = 40;
  await sharp({ create: { width: m.width + pad * 2, height: m.height + pad * 2, channels: 4, background: bg } })
    .composite([{ input: png, left: pad, top: pad }])
    .png()
    .toFile(path.join(out, `${name.replace('.svg', '')}-${bg.replace('#', '')}.png`));
};

await render('wordmark.svg', '#0B0B12', 900, '#F5F3F8');
await render('lockup-horizontal.svg', '#0B0B12', 900);
await render('lockup-horizontal-mono.svg', '#FFFFFF', 900, '#0B0B12');
await render('mark.svg', '#0B0B12', 256);
await render('mark.svg', '#FFFFFF', 256);
await render('mark-mono.svg', '#FFFFFF', 256, '#0B0B12');
await render('tray-template.svg', '#FFFFFF', 64);
await render('app-icon-maskable.svg', '#FFFFFF', 256);
await render('wordmark.svg', '#0B0B12', 120, '#F5F3F8');
console.log('previews in', out);
