#!/usr/bin/env node
// ReelBolt layout lint (first-party; shipped in the sandbox-runtime image at
// /opt/remotion-template/reelbolt/check-layout.mjs and run by the engine's CheckLayout tool as
// `node /opt/remotion-template/reelbolt/check-layout.mjs --composition=<id> --frames=<n,n,...>
// --min-text-px=<px> --output-scale=<s>` with /workspace as its working directory).
//
// It bundles the workspace's Remotion project with the workspace's own @remotion/cli (so
// remotion.config.ts applies exactly as it does for `remotion still`), opens Remotion's
// chrome-headless-shell, and for every requested frame renders a throwaway still and — while the
// frame is still on the page — measures every element that holds text directly:
//
//   overflow           the element's own box cannot hold its text (scrollWidth/Height > client + 1
//                      with overflow not visible, or the text spills out of a box that paints a
//                      background or border)
//   clipped            an ancestor with overflow other than visible cuts part of the text off
//   outside_safe_area  visible text reaches into the outer 5% of the frame
//   logo_low_contrast  a loaded, same-origin, mark-sized <img>/svg <image> whose mean ink colour (alpha-
//                      weighted, transparent pixels ignored) has WCAG contrast < 3.0 against the colour
//                      painted behind it (painted stack under the logo at the centre and 4 inset points, via
//                      elementsFromPoint, composited; a gradient counts as the mean of its stops). Only images
//                      <=1024px per side, <=25% of the frame area, <=8:1, and not an opaque picture wider than 40%
//                      of the frame are checked. A point under a video/img/canvas/url() image is unknown; an issue
//                      needs >=3 known points and ALL known points failing. Unknown/unreadable is never an issue
//   text_low_contrast  visible text whose colour (computed `color` x effective opacity) has WCAG contrast < 4.5
//                      (< 3.0 for large text: >= 24px, or >= 18.66px at weight >= 700) against the colour painted
//                      behind it (the element's own background included; 5 points in the text box, same never-guess
//                      rule as logo_low_contrast). Text with a text-shadow, a text stroke, background-clip:text or a
//                      transparent colour is skipped
//   too_small          computed font-size x the element's own transform scale x the output scale
//                      (final output height / composition height) is below --min-text-px
//
// Everything printed for the engine is ONE line: the marker below followed by JSON. The
// composition's own code runs in the page exactly as it does for a render, inside the sandbox; this
// script adds no network access and writes only out/layout-lint-bundle, which it deletes.

import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const MARKER = '@@REELBOLT_LAYOUT@@';
export const MAX_ISSUES = 40;
export const MAX_FRAMES = 12;
export const SAFE_MARGIN = 0.05;
const BUNDLE_DIR = 'out/layout-lint-bundle';
const KIND_ORDER = ['overflow', 'clipped', 'too_small', 'text_low_contrast', 'outside_safe_area', 'logo_low_contrast'];

/**
 * Runs IN THE PAGE (serialised by page.evaluate, so it must not close over anything). Measures the
 * DOM as it is now and returns the issues of this one frame.
 */
export function measureLayout(options) {
  const { width, height, minTextPx, outputScale, safeMargin } = options;
  const SKIP = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'TITLE', 'HEAD', 'META', 'LINK']);
  const round = (n) => Math.round(n * 10) / 10;
  const boxOf = (r) => ({ x: round(r.left), y: round(r.top), w: round(r.width), h: round(r.height) });

  const describe = (el) => {
    const parts = [];
    let node = el;
    while (node && node.nodeType === 1 && node !== document.body && parts.length < 4) {
      let part = node.tagName.toLowerCase();
      if (node.id) {
        part += '#' + node.id;
      } else {
        const classes = typeof node.className === 'string' ? node.className.trim().split(/\s+/).filter(Boolean) : [];
        if (classes.length > 0) part += '.' + classes.slice(0, 2).join('.');
        const parent = node.parentElement;
        if (parent) {
          const same = Array.from(parent.children).filter((c) => c.tagName === node.tagName);
          if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(node) + 1) + ')';
        }
      }
      parts.unshift(part);
      node = node.parentElement;
    }
    return parts.join(' > ').slice(0, 160);
  };

  const effectiveOpacity = (el) => {
    let opacity = 1;
    for (let node = el; node && node.nodeType === 1; node = node.parentElement) {
      opacity *= parseFloat(getComputedStyle(node).opacity || '1');
      if (opacity < 0.05) return opacity;
    }
    return opacity;
  };

  const textRectOf = (textNodes) => {
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
    for (const t of textNodes) {
      const range = document.createRange();
      range.selectNodeContents(t);
      for (const r of Array.from(range.getClientRects())) {
        if (r.width <= 0 || r.height <= 0) continue;
        left = Math.min(left, r.left);
        top = Math.min(top, r.top);
        right = Math.max(right, r.right);
        bottom = Math.max(bottom, r.bottom);
      }
    }
    if (!Number.isFinite(left)) return null;
    return { left, top, right, bottom, width: right - left, height: bottom - top };
  };

  const ownScale = (el, rect) => {
    if (typeof el.getScreenCTM === 'function' && !(el instanceof HTMLElement)) {
      const m = el.getScreenCTM();
      return m ? Math.sqrt(Math.abs(m.a * m.d - m.b * m.c)) || 1 : 1;
    }
    const sx = el.offsetWidth > 0 ? rect.width / el.offsetWidth : NaN;
    const sy = el.offsetHeight > 0 ? rect.height / el.offsetHeight : NaN;
    const s = Math.min(Number.isFinite(sx) ? sx : Infinity, Number.isFinite(sy) ? sy : Infinity);
    // offsetWidth/Height are whole pixels: a ratio within 3% of 1 is rounding, not a transform.
    return Number.isFinite(s) && s > 0 && Math.abs(s - 1) > 0.03 ? s : 1;
  };

  const paintsBox = (cs) => {
    const bg = cs.backgroundColor;
    const hasBg = (bg && bg !== 'transparent' && !/rgba\([^)]*,\s*0\)$/.test(bg)) || (cs.backgroundImage && cs.backgroundImage !== 'none');
    const hasBorder = ['Top', 'Right', 'Bottom', 'Left'].some((s) => parseFloat(cs['border' + s + 'Width']) > 0 && cs['border' + s + 'Style'] !== 'none');
    return hasBg || hasBorder;
  };

  const clips = (v) => v !== 'visible';
  const safe = { left: width * safeMargin, top: height * safeMargin, right: width * (1 - safeMargin), bottom: height * (1 - safeMargin) };
  const issues = [];
  const textCandidates = [];
  let scanned = 0;

  for (const el of Array.from(document.body.querySelectorAll('*'))) {
    if (SKIP.has(el.tagName)) continue;
    const textNodes = Array.from(el.childNodes).filter((n) => n.nodeType === 3 && n.textContent.trim().length > 0);
    if (textNodes.length === 0) continue;

    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.visibility === 'collapse' || cs.display === 'none') continue;
    const text = textRectOf(textNodes);
    if (!text) continue;
    // Entirely off the frame (sliding in, parked off-screen): not visible at this frame.
    if (text.right <= 0 || text.bottom <= 0 || text.left >= width || text.top >= height) continue;
    if (effectiveOpacity(el) < 0.05) continue;

    scanned++;
    const label = textNodes.map((n) => n.textContent).join(' ').replace(/\s+/g, ' ').trim().slice(0, 80);
    const elRect = el.getBoundingClientRect();
    const scale = ownScale(el, elRect);
    const fontPx = parseFloat(cs.fontSize) * scale;
    const onScreenPx = fontPx * outputScale;
    const base = { text: label, path: describe(el), box: boxOf(text), fontPx: round(fontPx), onScreenPx: round(onScreenPx) };

    const blockish = el instanceof HTMLElement && cs.display !== 'inline' && (el.clientWidth > 0 || el.clientHeight > 0);
    if (blockish) {
      const overX = el.scrollWidth > el.clientWidth + 1 && clips(cs.overflowX);
      const overY = el.scrollHeight > el.clientHeight + 1 && clips(cs.overflowY);
      const spills = !overX && !overY && paintsBox(cs) &&
        (text.left < elRect.left - 1 || text.right > elRect.right + 1 || text.top < elRect.top - 1 || text.bottom > elRect.bottom + 1);
      if (overX || overY || spills) {
        issues.push({ ...base, kind: 'overflow', detail: spills ? 'text spills out of its box' : 'text does not fit its box (' + (overX ? 'width' : 'height') + ')' });
      }
    }

    // An inline element (a <span> in a fixed-size <div>) is laid out by its first block ancestor:
    // that box cutting the text off is the text not fitting its box, i.e. `overflow`.
    let ownBox = !blockish;
    for (let anc = el.parentElement; anc && anc !== document.body && anc !== document.documentElement; anc = anc.parentElement) {
      const acs = getComputedStyle(anc);
      const layoutBox = ownBox && acs.display !== 'inline';
      if (acs.display !== 'inline') ownBox = false;
      const cx = clips(acs.overflowX), cy = clips(acs.overflowY);
      if (!cx && !cy) continue;
      const a = anc.getBoundingClientRect();
      // The composition's own frame clips everything; outside_safe_area covers that edge.
      if (a.left <= 0.5 && a.top <= 0.5 && a.right >= width - 0.5 && a.bottom >= height - 0.5) continue;
      const cutX = cx && (text.left < a.left - 1 || text.right > a.right + 1);
      const cutY = cy && (text.top < a.top - 1 || text.bottom > a.bottom + 1);
      if (cutX || cutY) {
        issues.push(layoutBox && !issues.some((i) => i.kind === 'overflow' && i.path === base.path)
          ? { ...base, kind: 'overflow', detail: 'text does not fit its box ' + describe(anc) + ' (' + (cutX ? 'width' : 'height') + ')' }
          : { ...base, kind: 'clipped', detail: 'cut off by ' + describe(anc) });
        break;
      }
    }

    if (text.left < safe.left || text.top < safe.top || text.right > safe.right || text.bottom > safe.bottom) {
      issues.push({ ...base, kind: 'outside_safe_area', detail: 'text reaches the outer ' + Math.round(safeMargin * 100) + '% of the frame' });
    }

    if (onScreenPx < minTextPx) {
      issues.push({ ...base, kind: 'too_small', detail: round(onScreenPx) + 'px on screen, minimum ' + round(minTextPx) + 'px' });
    }

    textCandidates.push({ el, cs, text, base });
  }

  // ── logo contrast ─────────────────────────────────────────────────────────
  // Every visible, loaded, same-origin mark-sized image: its mean ink colour against the colour behind it.
  const parseColor = (str) => {
    const m = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/.exec((str || '').trim());
    if (!m) return null;
    let a = 1;
    if (m[4] !== undefined) a = m[4].endsWith('%') ? parseFloat(m[4]) / 100 : parseFloat(m[4]);
    return { r: parseFloat(m[1]), g: parseFloat(m[2]), b: parseFloat(m[3]), a: Math.max(0, Math.min(1, a)) };
  };
  const lin = (c) => { const v = c / 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
  const luminanceOf = (c) => 0.2126 * lin(c.r) + 0.7152 * lin(c.g) + 0.0722 * lin(c.b);

  // Colour painted behind the logo at one point, or null when it cannot be known (never guessed).
  // `below` is the painted stack under the logo, front to back (the logo and anything above it removed).
  const backgroundFromStack = (below) => {
    const layers = []; // front to back
    let opaque = false;
    for (const node of below) {
      if (!node || node.nodeType !== 1) continue;
      if (node.tagName === 'VIDEO' || node.tagName === 'IMG' || node.tagName === 'CANVAS' || node.tagName === 'image') return null;
      const ncs = getComputedStyle(node);
      const bgImage = ncs.backgroundImage;
      if (bgImage && bgImage !== 'none') {
        if (!/gradient\(/.test(bgImage) || /url\(/.test(bgImage)) return null;
        const stops = (bgImage.match(/rgba?\([^)]*\)/g) || []).map(parseColor);
        if (stops.length === 0 || stops.some((c) => !c)) return null;
        const n = stops.length;
        const mean = { r: 0, g: 0, b: 0, a: 0 };
        for (const c of stops) { mean.r += c.r / n; mean.g += c.g / n; mean.b += c.b / n; mean.a += c.a / n; }
        layers.push(mean);
        if (mean.a >= 0.99) { opaque = true; break; }
      }
      const bg = ncs.backgroundColor;
      if (bg && bg !== 'transparent') {
        const c = parseColor(bg);
        if (!c) return null;
        if (c.a > 0) {
          layers.push(c);
          if (c.a >= 0.99) { opaque = true; break; }
        }
      }
    }
    if (!opaque) return null;
    let out = layers[layers.length - 1];
    for (let i = layers.length - 2; i >= 0; i--) {
      const f = layers[i];
      out = { r: f.r * f.a + out.r * (1 - f.a), g: f.g * f.a + out.g * (1 - f.a), b: f.b * f.a + out.b * (1 - f.a), a: 1 };
    }
    return out;
  };

  // The elements under the logo at (x, y): from document.elementsFromPoint (topmost first), everything after
  // the logo itself; the html/body fallback is appended when the stack did not already end with them.
  const stackBelow = (el, x, y) => {
    let list = [];
    try { list = (document.elementsFromPoint ? document.elementsFromPoint(x, y) : []) || []; } catch { list = []; }
    const idx = list.indexOf(el);
    let below;
    if (idx >= 0) below = list.slice(idx + 1);
    else {
      below = [];
      for (let n = el.parentElement; n && n.nodeType === 1; n = n.parentElement) below.push(n);
    }
    for (const root of [document.documentElement, document.body]) if (root && !below.includes(root)) below.push(root);
    return below;
  };

  const fileNameOf = (src) => {
    if (!src || /^data:/i.test(src)) return 'inline-image';
    try {
      const last = new URL(src, typeof location !== 'undefined' ? location.href : 'http://localhost/').pathname.split('/').filter(Boolean).pop() || 'image';
      return decodeURIComponent(last).slice(0, 120);
    } catch {
      return 'image';
    }
  };

  // ── text contrast ─────────────────────────────────────────────────────────
  // Visible text: its colour (with the effective opacity) against the colour painted behind it, 5 points in the text box.
  const TEXT_MIN_CONTRAST = 4.5;
  const TEXT_MIN_CONTRAST_LARGE = 3;
  const TEXT_MIN_KNOWN_POINTS = 3;
  const TEXT_MAX_ISSUES = 10;
  const textStackBelow = (el, x, y) => {
    let list = [];
    try { list = (document.elementsFromPoint ? document.elementsFromPoint(x, y) : []) || []; } catch { list = []; }
    const idx = list.indexOf(el);
    const below = [];
    if (idx >= 0) below.push(...list.slice(idx));
    else for (let n = el; n && n.nodeType === 1; n = n.parentElement) below.push(n);
    for (const root of [document.documentElement, document.body]) if (root && !below.includes(root)) below.push(root);
    return below;
  };
  let textIssues = 0;
  for (const { el, cs, text, base } of textCandidates) {
    if (textIssues >= TEXT_MAX_ISSUES) break;
    try {
      if (cs.textShadow && cs.textShadow !== 'none') continue;
      if (parseFloat(cs.webkitTextStrokeWidth) > 0) continue;
      if (cs.backgroundClip === 'text' || cs.webkitBackgroundClip === 'text') continue;
      const fg = parseColor(cs.color);
      if (!fg) continue;
      const alpha = fg.a * Math.min(1, Math.max(0, effectiveOpacity(el)));
      if (alpha <= 0.05) continue;
      const fontCss = parseFloat(cs.fontSize);
      const weight = parseInt(cs.fontWeight, 10) || 400;
      const large = fontCss >= 24 || (fontCss >= 18.66 && weight >= 700);
      const required = large ? TEXT_MIN_CONTRAST_LARGE : TEXT_MIN_CONTRAST;
      const w = text.width, h = text.height;
      const xs = [text.left + w * 0.5, text.left + w * 0.2, text.right - w * 0.2, text.left + w * 0.2, text.right - w * 0.2];
      const ys = [text.top + h * 0.5, text.top + h * 0.2, text.top + h * 0.2, text.bottom - h * 0.2, text.bottom - h * 0.2];
      let known = 0, passing = 0, worst = null;
      for (let k = 0; k < 5; k++) {
        const back = backgroundFromStack(textStackBelow(el, xs[k], ys[k]));
        if (!back) continue;
        known++;
        const shown = {
          r: fg.r * alpha + back.r * (1 - alpha),
          g: fg.g * alpha + back.g * (1 - alpha),
          b: fg.b * alpha + back.b * (1 - alpha),
        };
        const fl = luminanceOf(shown), bl = luminanceOf(back);
        const contrast = (Math.max(fl, bl) + 0.05) / (Math.min(fl, bl) + 0.05);
        if (contrast >= required) passing++;
        if (!worst || contrast < worst.contrast) worst = { contrast, shown, back };
      }
      if (known < TEXT_MIN_KNOWN_POINTS || passing > 0 || !worst) continue;
      const rgb = (c) => 'rgb(' + Math.round(c.r) + ', ' + Math.round(c.g) + ', ' + Math.round(c.b) + ')';
      issues.push({
        kind: 'text_low_contrast',
        text: base.text,
        path: base.path,
        box: base.box,
        textContrast: round(worst.contrast),
        requiredContrast: required,
        textColor: rgb(worst.shown),
        backgroundColor: rgb(worst.back),
        detail: 'contrast ' + round(worst.contrast) + ':1, needs ' + required + ':1' + (large ? ' (large text)' : ''),
      });
      textIssues++;
    } catch {
      continue; // text that cannot be measured is skipped, never reported
    }
  }

  const LOGO_MIN_BOX = 8;
  const LOGO_MAX_NATURAL = 1024;
  const LOGO_SAMPLE = 64;
  const LOGO_MIN_ALPHA = 32;
  const LOGO_MIN_CONTRAST = 3;
  const LOGO_MAX_FRAME_SHARE = 0.25;
  const LOGO_MAX_ASPECT = 8;
  const LOGO_OPAQUE_BIG_WIDTH = 0.4;
  const LOGO_MIN_KNOWN_POINTS = 3;
  for (const el of Array.from(document.body.querySelectorAll('*'))) {
    const tag = el.tagName;
    if (tag !== 'IMG' && tag !== 'image') continue;
    try {
      const cs = getComputedStyle(el);
      if (cs.visibility === 'hidden' || cs.visibility === 'collapse' || cs.display === 'none') continue;
      if (effectiveOpacity(el) < 0.05) continue;
      const rect = el.getBoundingClientRect();
      if (rect.width < LOGO_MIN_BOX || rect.height < LOGO_MIN_BOX) continue;
      if (rect.right <= 0 || rect.bottom <= 0 || rect.left >= width || rect.top >= height) continue;

      let source = el;
      let src = tag === 'IMG' ? (el.currentSrc || el.src) : ((el.href && el.href.baseVal) || el.getAttribute('href') || '');
      if (tag === 'image') {
        source = new Image();
        source.src = src;
      }
      if (!source.complete || !(source.naturalWidth > 0) || !(source.naturalHeight > 0)) continue;
      // Mark-sized only (photos and screenshots are not logos).
      if (source.naturalWidth > LOGO_MAX_NATURAL || source.naturalHeight > LOGO_MAX_NATURAL) continue;
      if ((rect.width * rect.height) / (width * height) > LOGO_MAX_FRAME_SHARE) continue;
      if (Math.max(rect.width, rect.height) / Math.min(rect.width, rect.height) > LOGO_MAX_ASPECT) continue;

      const scaleDown = Math.min(1, LOGO_SAMPLE / Math.max(source.naturalWidth, source.naturalHeight));
      const cw = Math.max(1, Math.round(source.naturalWidth * scaleDown));
      const ch = Math.max(1, Math.round(source.naturalHeight * scaleDown));
      let data;
      try {
        const canvas = document.createElement('canvas');
        canvas.width = cw;
        canvas.height = ch;
        const ctx = canvas.getContext('2d');
        ctx.drawImage(source, 0, 0, cw, ch);
        data = ctx.getImageData(0, 0, cw, ch).data;
      } catch {
        continue; // tainted or failed canvas: unreadable, never an issue
      }
      let sr = 0, sg = 0, sb = 0, sa = 0, anyTransparent = false;
      for (let i = 0; i + 3 < data.length; i += 4) {
        const a = data[i + 3];
        if (a < 255) anyTransparent = true;
        if (a < LOGO_MIN_ALPHA) continue;
        sr += data[i] * a; sg += data[i + 1] * a; sb += data[i + 2] * a; sa += a;
      }
      if (sa === 0) continue;
      const ink = { r: sr / sa, g: sg / sa, b: sb / sa, a: 1 };
      // A fully opaque image over 40% of the frame width is a picture, not a mark.
      if (!anyTransparent && rect.width > LOGO_OPAQUE_BIG_WIDTH * width) continue;
      const inkL = luminanceOf(ink);
      const xs = [rect.left + rect.width * 0.5, rect.left + rect.width * 0.2, rect.right - rect.width * 0.2, rect.left + rect.width * 0.2, rect.right - rect.width * 0.2];
      const ys = [rect.top + rect.height * 0.5, rect.top + rect.height * 0.2, rect.top + rect.height * 0.2, rect.bottom - rect.height * 0.2, rect.bottom - rect.height * 0.2];
      let known = 0;
      let passing = 0;
      let worst = null;
      for (let k = 0; k < 5; k++) {
        const back = backgroundFromStack(stackBelow(el, xs[k], ys[k]));
        if (!back) continue;
        known++;
        const backL = luminanceOf(back);
        const contrast = (Math.max(inkL, backL) + 0.05) / (Math.min(inkL, backL) + 0.05);
        if (contrast >= LOGO_MIN_CONTRAST) passing++;
        else if (!worst || contrast < worst.contrast) worst = { contrast, backL };
      }
      // Never guess: enough known points, and every known one must fail.
      if (known < LOGO_MIN_KNOWN_POINTS || passing > 0 || !worst) continue;
      const contrast = worst.contrast;
      const backL = worst.backL;
      issues.push({
        kind: 'logo_low_contrast',
        text: fileNameOf(src),
        path: describe(el),
        box: boxOf(rect),
        logoContrast: round(contrast),
        logoLuminance: Math.round(inkL * 1000) / 1000,
        backgroundLuminance: Math.round(backL * 1000) / 1000,
        src: fileNameOf(src),
      });
    } catch {
      continue; // a logo that cannot be measured is skipped, never reported
    }
  }

  return { scanned, issues };
}

/** Folds per-frame issues into one capped, ordered list; the same element and kind at several frames becomes one issue. */
export function mergeIssues(perFrame, maxIssues = MAX_ISSUES) {
  const byKey = new Map();
  for (const { frame, issues } of perFrame) {
    for (const issue of issues) {
      const key = issue.kind + '\u0000' + issue.path + '\u0000' + issue.text;
      const seen = byKey.get(key);
      if (seen) {
        if (!seen.alsoAtFrames.includes(frame)) seen.alsoAtFrames.push(frame);
      } else {
        byKey.set(key, { frame, ...issue, alsoAtFrames: [] });
      }
    }
  }
  const all = [...byKey.values()].sort((a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind) || a.frame - b.frame);
  const counts = {};
  for (const kind of KIND_ORDER) counts[kind] = all.filter((i) => i.kind === kind).length;
  const issues = all.slice(0, maxIssues).map((i) => (i.alsoAtFrames.length > 0 ? i : (({ alsoAtFrames, ...rest }) => rest)(i)));
  return { issuesTotal: all.length, counts, truncated: all.length > maxIssues, issues };
}

export function parseArgs(argv) {
  const out = {};
  for (const arg of argv) {
    const m = /^--([a-z-]+)=(.*)$/.exec(arg);
    if (!m) throw new Error('unexpected argument ' + JSON.stringify(arg).slice(0, 60));
    out[m[1]] = m[2];
  }
  const composition = out['composition'] ?? '';
  if (!/^[A-Za-z0-9_-]+$/.test(composition)) throw new Error('--composition must be a composition id');
  const frames = (out['frames'] ?? '').split(',').filter(Boolean).map(Number);
  if (frames.length === 0 || frames.length > MAX_FRAMES || frames.some((f) => !Number.isInteger(f) || f < 0)) {
    throw new Error('--frames must be 1 to ' + MAX_FRAMES + ' non-negative integers');
  }
  const minTextPx = Number(out['min-text-px'] ?? '24');
  const outputScale = Number(out['output-scale'] ?? '1');
  if (!(minTextPx >= 0 && minTextPx <= 500)) throw new Error('--min-text-px must be 0 to 500');
  if (!(outputScale > 0 && outputScale <= 16)) throw new Error('--output-scale must be above 0 and at most 16');
  return { composition, frames, minTextPx, outputScale };
}

function emit(result) {
  process.stdout.write('\n' + MARKER + JSON.stringify(result) + '\n');
}

async function main() {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (err) {
    emit({ ok: false, stage: 'arguments', error: String(err.message ?? err).slice(0, 500) });
    process.exitCode = 2;
    return;
  }

  const cwd = process.cwd();
  const req = createRequire(path.join(cwd, 'package.json'));
  const bundleDir = path.join(cwd, BUNDLE_DIR);
  let browser = null;
  let stage = 'bundle';
  try {
    const renderer = req('@remotion/renderer');
    const cliDir = path.dirname(req.resolve('@remotion/cli/package.json'));
    fs.rmSync(bundleDir, { recursive: true, force: true });
    const bundled = spawnSync(process.execPath, [path.join(cliDir, 'remotion-cli.js'), 'bundle', '--out-dir=' + bundleDir, '--log=error'], {
      cwd,
      encoding: 'utf8',
      maxBuffer: 16 * 1024 * 1024,
    });
    if (bundled.status !== 0) {
      throw new Error(((bundled.stderr || '') + '\n' + (bundled.stdout || '')).trim().split('\n').slice(-15).join('\n') || 'remotion bundle failed');
    }

    stage = 'browser';
    const browserExecutable = path.join(cwd, 'node_modules/.remotion/headless-shell');
    browser = await renderer.openBrowser('chrome', { browserExecutable, chromeMode: 'headless-shell', logLevel: 'error' });

    stage = 'composition';
    const composition = await renderer.selectComposition({
      serveUrl: bundleDir, id: args.composition, inputProps: {}, puppeteerInstance: browser,
      browserExecutable, chromeMode: 'headless-shell', logLevel: 'error',
    });

    // renderStill closes its page when the frame is done; measure just before that, while the
    // rendered frame is still on the page.
    let pending = null;
    const newPage = browser.newPage.bind(browser);
    browser.newPage = async (opts) => {
      const page = await newPage(opts);
      const close = page.close.bind(page);
      page.close = async (closeOpts) => {
        if (pending && !pending.done) {
          pending.done = true;
          try {
            pending.result = await page.evaluate(measureLayout, pending.options);
          } catch (err) {
            pending.error = String(err?.message ?? err).slice(0, 300);
          }
        }
        return close(closeOpts);
      };
      return page;
    };

    stage = 'frames';
    const frames = args.frames.filter((f) => f < composition.durationInFrames);
    const perFrame = [];
    const failed = [];
    for (const frame of frames) {
      pending = { done: false, options: { width: composition.width, height: composition.height, minTextPx: args.minTextPx, outputScale: args.outputScale, safeMargin: SAFE_MARGIN } };
      try {
        await renderer.renderStill({
          serveUrl: bundleDir, composition, frame, output: null, imageFormat: 'jpeg', jpegQuality: 10, scale: 0.1,
          inputProps: {}, puppeteerInstance: browser, browserExecutable, chromeMode: 'headless-shell', logLevel: 'error',
          onBrowserLog: () => undefined,
        });
      } catch (err) {
        failed.push({ frame, error: String(err?.message ?? err).split('\n')[0].slice(0, 300) });
        continue;
      }
      if (pending.result) perFrame.push({ frame, ...pending.result });
      else failed.push({ frame, error: pending.error ?? 'the frame could not be measured' });
    }

    const merged = mergeIssues(perFrame);
    emit({
      ok: failed.length === 0,
      compositionId: composition.id,
      width: composition.width,
      height: composition.height,
      outputScale: args.outputScale,
      minTextPx: args.minTextPx,
      frames: perFrame.map((f) => f.frame),
      textElements: perFrame.reduce((n, f) => n + f.scanned, 0),
      failedFrames: failed.length > 0 ? failed : undefined,
      ...merged,
    });
  } catch (err) {
    emit({ ok: false, stage, error: String(err?.message ?? err).slice(0, 1500) });
  } finally {
    if (browser) await browser.close({ silent: true }).catch(() => undefined);
    fs.rmSync(bundleDir, { recursive: true, force: true });
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  await main();
}
