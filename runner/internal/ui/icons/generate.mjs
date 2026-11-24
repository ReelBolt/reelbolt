#!/usr/bin/env node
// Rasterise the brand's tray mark into the PNGs the shell embeds.
//
// The artwork is NOT drawn here. `brand/tray-template.svg` is the master and
// docs/brand.md owns the rules ("Tray icon: render tray-template.svg at 16, 22
// or 24 px per platform"). This script only picks the two colours the brand
// allows for a one-colour mark — black on a light tray, white on a dark one —
// and writes the sizes Go embeds.
//
// Usage:  npm --prefix runner/internal/ui/icons install   # once
//         node runner/internal/ui/icons/generate.mjs
//
// It is run by hand and its output is committed, because a release build must
// not need npm: E10b builds the Go binaries on native runners with no node
// toolchain in the runner module.
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import sharp from "sharp";

const here = dirname(fileURLToPath(import.meta.url));
const master = join(here, "..", "..", "..", "..", "brand", "tray-template.svg");
const svg = readFileSync(master);
const out = join(here);

// 16 and 24 are the sizes docs/brand.md names; 32 is what Windows asks for on a
// high-DPI taskbar.
const SIZES = [16, 24, 32];

mkdirSync(out, { recursive: true });

for (const size of SIZES) {
  const base = sharp(svg, { density: 384 }).resize(size, size, {
    fit: "contain",
    background: { r: 0, g: 0, b: 0, alpha: 0 },
  });
  const light = await base.clone().png().toBuffer();
  writeFileSync(join(out, `mark-${size}.png`), light);
  // Negating RGB and keeping alpha is the brand's own white-on-dark variant of
  // a one-colour mark, not a recolour of our own invention.
  const dark = await base.clone().negate({ alpha: false }).png().toBuffer();
  writeFileSync(join(out, `mark-dark-${size}.png`), dark);
  console.log(`mark-${size}.png, mark-dark-${size}.png`);
}
