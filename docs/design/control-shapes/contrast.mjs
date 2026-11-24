#!/usr/bin/env node
/**
 * control-shape-restyle T-001: measured WCAG contrast for the proposal tokens.
 *
 * Plain Node, no dependencies. It reads the declarations out of `web/app/global.css` (the
 * heritage `:root` tokens, then the light or dark scheme block) so the numbers are measured from
 * the same source the preview consumes, not retyped here. `var()` aliases are resolved, because
 * the control tokens alias the app's own colours on purpose.
 *
 * The focus indicator is two-tone: the outer ring is measured against the page, the 2 px halo
 * against the fill and the track. A single colour cannot clear the light page, the mid-gray track
 * and the dark fill at once, hence the split.
 *
 * Thresholds: 3:1 for non-text UI (WCAG 1.4.11) and control boundaries, 4.5:1 for body-sized
 * text. Exits 1 if a required pair fails, so it can gate the PR.
 */

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
// Strip comments: the token comments name tokens with colons, which would otherwise be parsed as
// declarations and could swallow the declaration that follows.
const css = readFileSync(resolve(ROOT, 'web', 'app', 'global.css'), 'utf8').replace(
  /\/\*[\s\S]*?\*\//g,
  '',
);

// Resolved Mantine `--mantine-color-dimmed` in this theme (theme.ts gray[6] / dark[2]); used by
// the preview for disabled switch labels. Kept literal because it is not a --rb-control token.
const DIMMED = { light: '#625d70', dark: '#a79fb4' };

const DECL = /(--[\w-]+)\s*:\s*([^;]+);/g;

function collect(pattern) {
  const out = {};
  for (const block of css.matchAll(pattern)) {
    for (const decl of block[1].matchAll(DECL)) out[decl[1]] = decl[2].trim();
  }
  return out;
}

function themeTokens(scheme) {
  // The heritage `:root` block first, then the scheme block wins.
  const root = collect(/:root\s*\{([^}]*)\}/g);
  const theme = collect(
    new RegExp(`:root\\[data-mantine-color-scheme=['"]${scheme}['"]\\]\\s*\\{([^}]*)\\}`, 'g'),
  );
  return { ...root, ...theme };
}

function resolveToken(tokens, name, depth = 0) {
  if (depth > 10) return null;
  const value = tokens[name];
  if (value === undefined) return null;
  const alias = value.match(/^var\((--[\w-]+)\)$/);
  return alias ? resolveToken(tokens, alias[1], depth + 1) : value;
}

const THEMES = { light: themeTokens('light'), dark: themeTokens('dark') };

function channel(value) {
  const c = value / 255;
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function luminance(hex) {
  const value = hex.replace('#', '');
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(value.slice(i, i + 2), 16));
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function contrast(a, b) {
  const la = luminance(a);
  const lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

function resolveValue(tokens, name) {
  if (name === 'page') return resolveToken(tokens, '--rb-body');
  if (name === 'dimmed') return null;
  return resolveToken(tokens, name);
}

const PAIRS = [
  ['track vs page background', '--rb-control-track-bg', 'page', 3, true],
  ['thumb vs track', '--rb-control-thumb-bg', '--rb-control-track-bg', 3, true],
  ['switch off vs page background', '--rb-control-switch-off-bg', 'page', 3, true],
  ['switch on vs page background', '--rb-control-switch-on-bg', 'page', 3, true],
  ['switch thumb vs off track', '--rb-control-switch-thumb-bg', '--rb-control-switch-off-bg', 3, true],
  ['switch thumb vs on track', '--rb-control-switch-thumb-bg', '--rb-control-switch-on-bg', 3, true],
  ['focus ring (outer) vs page background', '--rb-control-focus', 'page', 3, true],
  ['focus halo vs fill', '--rb-control-focus-halo', '--rb-control-fill-bg', 3, true],
  ['focus halo vs track', '--rb-control-focus-halo', '--rb-control-track-bg', 3, true],
  ['fill vs track (extra)', '--rb-control-fill-bg', '--rb-control-track-bg', 3, false],
  ['thumb rim vs track (extra)', '--rb-control-thumb-rim-color', '--rb-control-track-bg', 3, false],
  ['disabled label vs page background (text, extra)', 'dimmed', 'page', 4.5, false],
  ['disabled track vs page (inactive, extra)', '--rb-control-track-bg-disabled', 'page', 3, false],
  ['disabled thumb vs disabled track (inactive, extra)', '--rb-control-track-bg', '--rb-control-track-bg-disabled', 3, false],
  ['disabled fill vs disabled track (inactive, extra)', '--rb-control-fill-bg-disabled', '--rb-control-track-bg-disabled', 3, false],
];

let failed = false;

for (const [scheme, tokens] of Object.entries(THEMES)) {
  console.log(`\n## ${scheme === 'light' ? 'Light' : 'Dark'}\n`);
  console.log('| Pair | Foreground | Background | Ratio | Required | Result |');
  console.log('| --- | --- | --- | ---: | ---: | --- |');

  for (const [label, fgName, bgName, threshold, required] of PAIRS) {
    const fg = fgName === 'dimmed' ? DIMMED[scheme] : resolveValue(tokens, fgName);
    const bg = resolveValue(tokens, bgName);
    if (!fg || !bg) {
      console.log(`| ${label} | missing token | missing token | n/a | ${threshold}:1 | FAIL |`);
      failed = true;
      continue;
    }
    const ratio = contrast(fg, bg);
    const pass = ratio >= threshold;
    if (!pass && required) failed = true;
    console.log(
      `| ${label} | ${fg} | ${bg} | ${ratio.toFixed(2)}:1 | ${threshold}:1 | ${pass ? 'PASS' : 'FAIL'}${required ? '' : ' (advisory)'} |`,
    );
  }
}

console.log('\nRequired pairs: WCAG 1.4.11 non-text 3:1. The two-tone focus ring measures the');
console.log('outer ring against the page and the halo against the fill and the track.');
console.log('Disabled pairs are advisory (WCAG 1.4.11 exempts inactive components).');
console.log(failed ? 'RESULT: FAIL' : 'RESULT: PASS');

if (failed) process.exit(1);
