// Optimises the raw marketing screenshots into blog assets.
//
//   node site/scripts/optimize-screens.mjs <rawDir> [outDir]
//
// Reads <rawDir>/<name>.png (captured by e2e/scripts/marketing-screenshots.ts) and writes, per screen,
// <name>-640.avif, <name>-640.webp, <name>-1280.avif, <name>-1280.webp (width resized, aspect kept),
// plus manifest.json: [{ name, width, height, alt }] with height at 1280 wide.
// AVIF quality 50, WebP quality 70. Default outDir is site/public/blog/img.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const here = path.dirname(fileURLToPath(import.meta.url));
const rawDir = process.argv[2];
const outDir = process.argv[3] ?? path.resolve(here, '..', 'public', 'blog', 'img');

if (!rawDir) {
  console.error('usage: node site/scripts/optimize-screens.mjs <rawDir> [outDir]');
  process.exit(2);
}

/** One entry per kept screen: raw file stem -> kebab-case blog name and a one-sentence factual alt text. */
const SCREENS = [
  {
    raw: 'dashboard',
    name: 'dashboard',
    alt: 'The ReelBolt dashboard home with a running-now panel, a latest renders panel and a finish-setup checklist.',
  },
  {
    raw: 'project-files',
    name: 'project-files',
    alt: 'The Files tab of the Acme Notes demo project, listing five uploaded source files with sizes and upload times.',
  },
  {
    raw: 'picture-editor',
    name: 'picture-editor',
    alt: 'The thumbnail editor with a logo picture on the canvas and a text layer named Headline in the layers panel.',
  },
  {
    raw: 'assistant',
    name: 'assistant',
    alt: 'The assistant side panel open over the dashboard, showing the welcome state with suggested prompts.',
  },
  {
    raw: 'workflow-builder',
    name: 'workflow-builder',
    alt: 'The workflow builder for a promo video workflow, with its run button, credit estimate line and the first steps on the canvas.',
  },
  {
    raw: 'timeline-editor',
    name: 'timeline-editor',
    alt: 'The timeline tracks of a demo timeline in the timeline editor, with three video clips on track V1 and one audio clip on track A1.',
  },
  {
    raw: 'run-estimate',
    name: 'run-estimate',
    alt: 'The run dialog estimate for a workflow, showing the estimated credits, a rough-estimate note and the credits the workspace has available.',
  },
  {
    raw: 'brand-kit',
    name: 'brand-kit',
    alt: 'The Brand and product tab of a demo project, showing the approved brand colours and typography.',
  },
  {
    raw: 'billing-plans',
    name: 'billing-plans',
    alt: 'The Choose a plan section of the Plan and billing page, with Creator, Pro and Team plan cards.',
  },
];

fs.mkdirSync(outDir, { recursive: true });

const manifest = [];
for (const screen of SCREENS) {
  const input = path.join(rawDir, `${screen.raw}.png`);
  if (!fs.existsSync(input)) {
    console.error(`missing raw screenshot: ${input}`);
    process.exit(1);
  }
  let height1280 = 0;
  for (const width of [640, 1280]) {
    const base = path.join(outDir, `${screen.name}-${width}`);
    const pipeline = () => sharp(input).resize({ width, withoutEnlargement: true });
    await pipeline().avif({ quality: 50 }).toFile(`${base}.avif`);
    await pipeline().webp({ quality: 70 }).toFile(`${base}.webp`);
    if (width === 1280) {
      const meta = await sharp(`${base}.webp`).metadata();
      height1280 = meta.height;
    }
  }
  manifest.push({ name: screen.name, width: 1280, height: height1280, alt: screen.alt });
  const sizes = ['640', '1280'].map((w) => {
    const bytes = fs.statSync(path.join(outDir, `${screen.name}-${w}.avif`)).size;
    return `${w}.avif=${bytes}B`;
  });
  console.log(`${screen.name}: ${sizes.join(' ')}`);
}

fs.writeFileSync(path.join(outDir, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
console.log(`wrote ${manifest.length} screens to ${outDir}`);
