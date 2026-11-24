// Desktop runner icon pack: every OS icon the ReelBolt runner tray app and its installers need,
// generated from the masters in brand/. Nothing in this pack is drawn or exported by hand — see
// docs/brand.md ("Brand artwork has one source"). Area E loads these files; this script owns them.
//
//   node brand/scripts/runner-icons.mjs                 write the pack + icons.manifest.json
//   node brand/scripts/runner-icons.mjs --out <dir>     write somewhere else (used by proofs/tests)
//
// Platforms (decision D26): the runner ships for Windows x64 and Linux x64/arm64. macOS is NOT a
// supported platform, so the macOS half of the G2c plan is deliberately not built here: there is no
// trayTemplate.png / trayTemplate@2x.png and no AppIcon.icns, and nothing below depends on AppKit
// tinting a "*Template" file name. Adding that set later is one entry in the OS table plus an .icns
// writer; the README this script writes into the output directory records what is deferred and why.
//
// Requires the devDependencies of brand/package.json (sharp rasterizes the SVG masters; png-to-ico
// packs the Windows .ico containers). Both are already dependencies of site/ and web/.
import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import sharp from 'sharp';
import pngToIco from 'png-to-ico';

const brandDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(brandDir, '..');
const outFlag = process.argv.indexOf('--out');
const outDir = outFlag === -1
  ? path.join(repoRoot, 'runner', 'assets', 'icons')
  : path.resolve(process.argv[outFlag + 1]);

// Masters, by role. mark.svg is the bare mark (tray); app-icon.svg is the mark on its #2E1065
// rounded square (executable, .desktop, installers).
const MASTERS = { mark: 'mark.svg', appIcon: 'app-icon.svg' };

// The mark's own unit box, from brand/mark.svg's viewBox. Used to convert a target pixel size into
// the librsvg density that rasterizes the master at (at least) 4x the target, so small icons are
// downsampled rather than rendered straight at 16 px.
const MARK_BOX = 120;

// Palette: existing tokens only, from docs/site-design-system.md ("Palette") / docs/brand.md
// ("Colour"). The mark itself is never recoloured — these colour the status badge overlay on top of
// an untouched mark. `danger` is the audited error token (#F87171, "Error text").
const TOKEN = { accent: '#A855F7', accentStrong: '#7C3AED', danger: '#F87171' };

// Tray states, matching the runner's own vocabulary (E6a: idle/working/paused/error/unpaired).
// `idle` is the plain mark and has no entry here. The three coloured states differ in SHAPE as
// well as colour so they stay distinguishable at 16 px and for colour-blind users.
const VARIANTS = {
  busy: { glyph: 'dot', colour: TOKEN.accent },
  paused: { glyph: 'bars', colour: TOKEN.accentStrong },
  error: { glyph: 'dot', colour: TOKEN.danger },
  unpaired: { glyph: 'ring', colour: TOKEN.accentStrong },
};

// Windows tray icons are requested by the shell at 16/20/24/32 (96/120/144/192 dpi on a 96 dpi
// baseline); the executable icon carries the usual 16..256 ladder.
const WINDOWS_TRAY_SIZES = [16, 20, 24, 32];
const WINDOWS_APP_SIZES = [16, 24, 32, 48, 64, 256];
// Linux panels ask for 22 px (GNOME/KDE) or 24 px; the .desktop entry gets a 256/512 pair for the
// application menu and for HiDPI launchers.
const LINUX_TRAY_SIZES = [22, 24];
const LINUX_APP_SIZES = [256, 512];

const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');

const masters = Object.fromEntries(
  await Promise.all(Object.entries(MASTERS).map(async ([role, file]) => [
    role,
    { file, buffer: await readFile(path.join(brandDir, file)) },
  ])),
);

/** The body of brand/mark.svg — everything inside its root <svg> — for re-composition with a badge. */
const MARK_SVG_BODY = masters.mark.buffer.toString('utf8')
  .replace(/^[\s\S]*?<svg[^>]*>/, '')
  .replace(/<\/svg>[\s\S]*$/, '')
  .trim();

/** Rasterizes a master at `size` px, downsample-quality first. */
async function rasterize(master, size) {
  const natural = master.file === MASTERS.appIcon ? 512 : MARK_BOX;
  const buffer = await sharp(master.buffer, { density: (Math.max(size * 4, 512) / natural) * 72 })
    .resize(size, size, { fit: 'contain' })
    .png()
    .toBuffer();
  return buffer;
}

/**
 * Badge geometry, in the mark's own units (the 120-unit box), so a badge can be written into the
 * same SVG document as the mark and rasterized once with it. The badge sits in the bottom-right
 * corner of the box; "gap" is the transparent ring that keeps it readable on any panel background
 * and it is never allowed to fall below one output pixel at the sizes a tray asks for.
 */
function geometry(size) {
  const unit = MARK_BOX / size; // output pixels per mark unit
  return {
    cx: 0.78 * MARK_BOX, // 93.6
    cy: 0.78 * MARK_BOX,
    r: 0.21 * MARK_BOX, // 25.2
    gap: Math.max(1, 0.05 * size) * unit,
  };
}

/**
 * The two fragments one status badge contributes to the composed SVG, both pure geometry added by
 * this script: `glyph` is the badge itself, `punch` is the transparent gap cut out of the mark
 * underneath it so the badge reads on any panel background.
 */
function badgeSvg(variant, size) {
  const { glyph, colour } = VARIANTS[variant];
  const { cx, cy, r, gap } = geometry(size);
  const at = 'cx="' + cx + '" cy="' + cy + '"';

  if (glyph === 'dot') {
    return {
      glyph: '<circle ' + at + ' r="' + r + '" fill="' + colour + '"/>',
      punch: '<circle ' + at + ' r="' + (r + gap) + '" fill="#000"/>',
    };
  }
  if (glyph === 'ring') {
    const stroke = r * 0.5;
    return {
      glyph: '<circle ' + at + ' r="' + r + '" fill="none" stroke="' + colour +
        '" stroke-width="' + stroke + '"/>',
      punch: '<circle ' + at + ' r="' + (r + stroke / 2 + gap) + '" fill="#000"/>',
    };
  }
  // 'bars': the pause glyph — two rounded bars, the shape every transport control uses.
  const w = r * 0.44;
  const h = r * 1.5;
  const between = r * 0.34;
  const left = cx - (2 * w + between) / 2;
  const top = cy - h / 2;
  const bar = (x) => '<rect x="' + x + '" y="' + top + '" width="' + w + '" height="' + h +
    '" rx="' + w / 2 + '" fill="' + colour + '"/>';
  return {
    glyph: bar(left) + bar(left + w + between),
    punch: '<rect x="' + (left - gap) + '" y="' + (top - gap) + '" width="' +
      (2 * w + between + 2 * gap) + '" height="' + (h + 2 * gap) + '" rx="' + (w / 2 + gap) +
      '" fill="#000"/>',
  };
}

/**
 * mark.svg, optionally carrying a status badge. A variant is NOT composited onto a rendered PNG:
 * the mark's own SVG body is re-wrapped in a document that carries the badge's gap mask and glyph,
 * and librsvg rasterizes the whole thing once. Every pixel outside the badge therefore comes from
 * exactly the same rasterization as the plain mark — no premultiply round trip to round it.
 */
async function renderMark(size, variant) {
  if (!variant) return rasterize(masters.mark, size);
  const { glyph, punch } = badgeSvg(variant, size);
  const defs = '<defs><mask id="reelbolt-badge-gap" maskUnits="userSpaceOnUse" x="0" y="0" width="' +
    MARK_BOX + '" height="' + MARK_BOX + '"><rect width="' + MARK_BOX + '" height="' + MARK_BOX +
    '" fill="#fff"/>' + punch + '</mask></defs>';
  const body = MARK_SVG_BODY;
  const svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ' + MARK_BOX + ' ' + MARK_BOX +
    '" width="' + size + '" height="' + size + '">' + defs + '<g mask="url(#reelbolt-badge-gap)">' +
    body + '</g>' + glyph + '</svg>';
  return sharp(Buffer.from(svg), { density: (Math.max(size * 4, 512) / size) * 72 })
    .resize(size, size, { fit: 'contain' })
    .png()
    .toBuffer();
}

/** Reads back an .ico container's directory so the generator can prove what it wrote. */
function icoSizes(buffer) {
  const count = buffer.readUInt16LE(4);
  const sizes = [];
  for (let i = 0; i < count; i++) {
    const entry = 6 + i * 16;
    sizes.push(buffer.readUInt8(entry) === 0 ? 256 : buffer.readUInt8(entry));
  }
  return sizes;
}

const written = [];
const record = async (file, buffer, extra) => {
  await writeFile(path.join(outDir, file), buffer);
  written.push({ file, bytes: buffer.length, sha256: sha256(buffer), ...extra });
};

/** One .ico from a list of (size, png buffer) pairs, then verified by reading its own directory. */
async function writeIco(file, source, entries) {
  const buffer = await pngToIco(entries.map((e) => e.buffer));
  const expected = entries.map((e) => e.size);
  const actual = icoSizes(buffer);
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(file + ': png-to-ico wrote ' + actual.join('/') + ', expected ' + expected.join('/'));
  }
  await record(file, buffer, { format: 'ico', source, sizes: expected, dimensions: expected.join('/') + ' px' });
}

await mkdir(outDir, { recursive: true });

// --- Windows: tray and executable ------------------------------------------------------------
const traySizes = await Promise.all(WINDOWS_TRAY_SIZES.map(async (size) => ({ size, buffer: await renderMark(size) })));
await writeIco('tray.ico', MASTERS.mark, traySizes);
for (const variant of Object.keys(VARIANTS)) {
  const entries = [];
  for (const size of WINDOWS_TRAY_SIZES) entries.push({ size, buffer: await renderMark(size, variant) });
  await writeIco('tray-' + variant + '.ico', MASTERS.mark + ' + ' + variant + ' badge', entries);
}
await writeIco(
  'app.ico',
  MASTERS.appIcon,
  await Promise.all(WINDOWS_APP_SIZES.map(async (size) => ({ size, buffer: await rasterize(masters.appIcon, size) }))),
);

// --- Linux: tray PNGs and the .desktop/launcher PNG pair -------------------------------------
const renderVariantPng = async (variant, size) => (variant ? renderMark(size, variant) : renderMark(size));
for (const size of LINUX_TRAY_SIZES) {
  await record('tray-' + size + '.png', await renderVariantPng(null, size), {
    format: 'png', source: MASTERS.mark, dimensions: size + 'x' + size,
  });
  for (const variant of Object.keys(VARIANTS)) {
    await record('tray-' + variant + '-' + size + '.png', await renderVariantPng(variant, size), {
      format: 'png', source: MASTERS.mark + ' + ' + variant + ' badge', dimensions: size + 'x' + size,
    });
  }
}
for (const size of LINUX_APP_SIZES) {
  await record('reelbolt-runner-' + size + '.png', await rasterize(masters.appIcon, size), {
    format: 'png', source: MASTERS.appIcon, dimensions: size + 'x' + size,
  });
}

// --- Manifest: the provenance brand/scripts/sync.mjs --check verifies later -------------------
const manifest = {
  note: 'Generated from brand/ by brand/scripts/runner-icons.mjs. Never edit these files by hand: ' +
    'edit the master in brand/ and re-run the generator (docs/brand.md).',
  generator: 'brand/scripts/runner-icons.mjs',
  outputDir: path.relative(repoRoot, outDir).split(path.sep).join('/'),
  platforms: ['windows-x64', 'linux-x64', 'linux-arm64'],
  deferred: {
    'macos-apple-silicon': 'Not built: decision D26 drops macOS from the runner. The deferred set is ' +
      'trayTemplate.png (16x16) + trayTemplate@2x.png (32x32) from brand/tray-template.svg, ' +
      'monochrome status variants, and AppIcon.icns. See the README in this directory.',
  },
  sources: Object.fromEntries(Object.values(masters).map((m) => [m.file, sha256(m.buffer)])),
  outputs: written.map(({ file, bytes, sha256: hash, format, source, sizes, dimensions }) =>
    ({ file, bytes, sha256: hash, format, source, ...(sizes ? { sizes } : {}), ...(dimensions ? { dimensions } : {}) })),
};

// Remove files a previous run wrote that this run no longer produces (a dropped variant must not
// linger as a stale icon), and never touch anything this generator did not write.
try {
  const previous = JSON.parse(await readFile(path.join(outDir, 'icons.manifest.json'), 'utf8'));
  const current = new Set(written.map((w) => w.file));
  for (const old of previous.outputs ?? []) {
    if (!current.has(old.file)) {
      await rm(path.join(outDir, old.file), { force: true });
      console.log('removed  ' + old.file + ' (no longer generated)');
    }
  }
} catch {
  // No previous manifest: first run.
}

await writeFile(path.join(outDir, 'icons.manifest.json'), JSON.stringify(manifest, null, 2) + '\n');

const width = Math.max(...written.map((w) => w.file.length));
for (const w of written) {
  console.log('wrote  ' + w.file.padEnd(width) + '  ' + String(w.bytes).padStart(7) + ' B  ' +
    (w.sizes ? 'ico ' + w.sizes.join('/') + ' px' : w.dimensions));
}
const stale = (await readdir(outDir)).filter((f) => f.endsWith('.ico') || f.endsWith('.png'));
const known = new Set(written.map((w) => w.file));
const extra = stale.filter((f) => !known.has(f));
if (extra.length) console.log('note: files in the pack this generator does not own: ' + extra.join(', '));
console.log('\n' + written.length + ' files + icons.manifest.json in ' + path.relative(repoRoot, outDir));
