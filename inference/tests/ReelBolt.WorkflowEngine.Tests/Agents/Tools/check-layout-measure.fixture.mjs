// Runs the SHIPPED check-layout.mjs measureLayout over a fixture document with a KNOWN overflow and
// prints what it measured as JSON. check-layout.mjs itself is a page-evaluate payload — it reads
// document / getComputedStyle / Range / HTMLElement, which the browser supplies — so this driver
// supplies exactly those globals and nothing else. It proves the real measuring code runs and finds
// the overflow, without a browser or a Remotion bundle.
//
// Usage: node check-layout-measure.fixture.mjs <absolute path to check-layout.mjs> [logos|texts]
// With `logos` the fixture is a set of logo images on assorted backgrounds (see below) instead.

import { pathToFileURL } from 'node:url';

const scriptPath = process.argv[2];
if (!scriptPath) throw new Error('usage: node check-layout-measure.fixture.mjs <check-layout.mjs>');
const { measureLayout } = await import(pathToFileURL(scriptPath).href);

const mode = process.argv[3] ?? 'text';
const FRAME_WIDTH = 1000;
const FRAME_HEIGHT = 1000;

// The fixture: one block element whose text (200px wide) does not fit its box (100px wide) with
// overflow-x hidden. That is `overflow`; the box also starts at x=0, inside the outer 5% margin,
// so it is also `outside_safe_area`. Font 16px with a 8px floor is not `too_small`.
const style = (over = {}) => ({
  display: 'block',
  visibility: 'visible',
  opacity: '1',
  overflowX: 'visible',
  overflowY: 'visible',
  fontSize: '16px',
  backgroundColor: 'transparent',
  backgroundImage: 'none',
  borderTopWidth: '0px',
  borderRightWidth: '0px',
  borderBottomWidth: '0px',
  borderLeftWidth: '0px',
  borderTopStyle: 'none',
  borderRightStyle: 'none',
  borderBottomStyle: 'none',
  borderLeftStyle: 'none',
  ...over,
});

class HTMLElement {}
globalThis.HTMLElement = HTMLElement;

if (mode === 'logos') {
  runLogoFixture();
  process.exit(0);
}
if (mode === 'texts') {
  runTextContrastFixture();
  process.exit(0);
}

const textNode = { nodeType: 3, textContent: 'Overflowing headline text' };
const box = Object.assign(new HTMLElement(), {
  nodeType: 1,
  tagName: 'DIV',
  id: 'box',
  className: '',
  childNodes: [textNode],
  textContent: 'Overflowing headline text',
  offsetWidth: 100,
  offsetHeight: 50,
  clientWidth: 100,
  clientHeight: 50,
  scrollWidth: 200,
  scrollHeight: 50,
  getBoundingClientRect: () => ({ left: 0, top: 0, right: 100, bottom: 50, width: 100, height: 50 }),
});
textNode.parentElement = box;

const body = Object.assign(new HTMLElement(), {
  nodeType: 1,
  tagName: 'BODY',
  id: '',
  className: '',
  childNodes: [box],
  textContent: '',
  parentElement: null,
  querySelectorAll: () => [box],
  getBoundingClientRect: () => ({ left: 0, top: 0, right: FRAME_WIDTH, bottom: FRAME_HEIGHT, width: FRAME_WIDTH, height: FRAME_HEIGHT }),
});
box.parentElement = body;

const styles = new Map([[box, style({ overflowX: 'hidden' })]]);
globalThis.document = {
  body,
  documentElement: body,
  createRange: () => ({
    selectNodeContents: () => {},
    getClientRects: () => [{ left: 0, top: 0, right: 200, bottom: 20, width: 200, height: 20 }],
  }),
};
globalThis.getComputedStyle = (el) => styles.get(el) ?? style();

const result = measureLayout({
  width: FRAME_WIDTH,
  height: FRAME_HEIGHT,
  minTextPx: 8,
  outputScale: 1,
  safeMargin: 0.05,
});
process.stdout.write(JSON.stringify(result));

// ── logo contrast fixture ─────────────────────────────────────────────────
// Each case is an <img> (fake pixels: uniform ink colour plus transparent pixels) inside a parent
// that carries the background under test. The fake canvas returns the image's pixels.
function runLogoFixture() {
  const INK = {
    black: { r: 0, g: 0, b: 0, a: 255 },
    violet: { r: 126, g: 111, b: 255, a: 255 },
    white: { r: 250, g: 250, b: 250, a: 255 },
    clear: { r: 0, g: 0, b: 0, a: 0 },
  };
  const make = (props) => Object.assign(new HTMLElement(), { nodeType: 1, className: '', id: '', childNodes: [], children: [], textContent: '', ...props });
  const styles = new Map();
  const dark = 'rgb(10, 10, 20)';
  const white = 'rgb(255, 255, 255)';
  const cases = [
    { name: 'a-black-on-dark', ink: INK.black, bg: { backgroundColor: dark }, issue: true },
    { name: 'b-black-on-white', ink: INK.black, bg: { backgroundColor: white }, issue: false },
    { name: 'c1-violet-on-dark', ink: INK.violet, bg: { backgroundColor: dark }, issue: false },
    { name: 'c2-violet-on-white', ink: INK.violet, bg: { backgroundColor: white }, issue: false },
    { name: 'd-light-on-white', ink: INK.white, bg: { backgroundColor: white }, issue: true },
    { name: 'e1-black-on-dark-gradient', ink: INK.black, bg: { backgroundImage: 'linear-gradient(rgb(10, 10, 20), rgb(30, 30, 60))' }, issue: true },
    { name: 'e2-black-on-light-gradient', ink: INK.black, bg: { backgroundImage: 'linear-gradient(rgb(240, 240, 240), rgb(255, 255, 255))' }, issue: false },
    { name: 'f-black-on-plate-in-dark-parent', ink: INK.black, bg: { backgroundColor: dark }, plate: white, issue: false },
    { name: 'g-black-on-url-background', ink: INK.black, bg: { backgroundColor: dark, backgroundImage: 'url("bg.png")' }, issue: false },
    { name: 'h-photo-like', ink: INK.black, bg: { backgroundColor: dark }, natural: 2000, issue: false },
    { name: 'i-transparent-only', ink: INK.clear, bg: { backgroundColor: dark }, issue: false },
    { name: 'j-black-on-semi-transparent-over-dark', ink: INK.black, bg: { backgroundColor: 'rgba(255, 255, 255, 0.1)' }, root: dark, issue: true },
    { name: 'k-no-opaque-background', ink: INK.black, bg: {}, issue: false },
    // Sibling stacking: the plate/video is NOT an ancestor but is painted under the logo.
    { name: 'l-black-on-sibling-light-plate', ink: INK.black, bg: { backgroundColor: dark }, sibling: { style: { backgroundColor: white } }, issue: false },
    { name: 'm-black-on-sibling-video', ink: INK.black, bg: { backgroundColor: dark }, sibling: { tag: 'VIDEO', style: {} }, issue: false },
    { name: 'n-black-on-sibling-dark-plate', ink: INK.black, bg: { backgroundColor: white }, sibling: { style: { backgroundColor: dark } }, issue: true },
    { name: 'o-screenshot-1280x720', ink: INK.black, bg: { backgroundColor: dark }, natural: [1280, 720], issue: false },
    { name: 'p-800x600-60pct-of-frame', ink: INK.black, bg: { backgroundColor: dark }, natural: [800, 600], big: true, issue: false },
    { name: 'q-small-mark-5pct', ink: INK.black, bg: { backgroundColor: dark }, big: 'five', issue: true },
    { name: 'r-points-disagree', ink: INK.black, bg: { backgroundColor: dark }, split: true, issue: false },
  ];
  const rootDark = make({ tagName: 'DIV', parentElement: null });
  styles.set(rootDark, style({ backgroundColor: dark }));
  const bodyEl = make({
    tagName: 'BODY',
    parentElement: null,
    querySelectorAll: () => imgs,
    getBoundingClientRect: () => ({ left: 0, top: 0, right: FRAME_WIDTH, bottom: FRAME_HEIGHT, width: FRAME_WIDTH, height: FRAME_HEIGHT }),
  });
  // Cases without an opaque background end at the body, which paints nothing.
  rootDark.parentElement = null;
  const children = [];
  const imgs = [];
  cases.forEach((c, i) => {
    const parent = make({ tagName: 'DIV', getBoundingClientRect: () => ({ left: 100, top: 100 + i * 60, right: 300, bottom: 150 + i * 60, width: 200, height: 50 }) });
    styles.set(parent, style(c.bg));
    let holder = parent;
    if (c.plate) {
      holder = make({ tagName: 'DIV', parentElement: parent, getBoundingClientRect: parent.getBoundingClientRect });
      styles.set(holder, style({ backgroundColor: c.plate }));
    }
    const [nw, nh] = Array.isArray(c.natural) ? c.natural : [c.natural ?? 64, c.natural ?? 64];
    const col = i >= 12 ? 400 : 0, row = i % 12;
    let rectOf = () => ({ left: 120 + col, top: 105 + row * 60, right: 168 + col, bottom: 153 + row * 60, width: 48, height: 48 });
    if (c.big === true) { const w = Math.sqrt(0.6 * FRAME_WIDTH * FRAME_HEIGHT * 4 / 3), h = w * 0.75; rectOf = () => ({ left: 10, top: 10, right: 10 + w, bottom: 10 + h, width: w, height: h }); }
    if (c.big === 'five') { const w = Math.sqrt(0.05 * FRAME_WIDTH * FRAME_HEIGHT); rectOf = () => ({ left: 10, top: 10, right: 10 + w, bottom: 10 + w, width: w, height: w }); }
    const img = make({
      tagName: 'IMG',
      complete: true,
      naturalWidth: nw,
      naturalHeight: nh,
      currentSrc: 'https://example.invalid/assets/' + c.name + '.png?token=secret#frag',
      parentElement: holder,
      pixels: [c.ink, c.ink, INK.clear, c.ink],
      getBoundingClientRect: () => rectOf(),
    });
    img.rectOf = rectOf;
    styles.set(img, style());
    parent.parentElement = c.root ? rootDark : bodyEl;
    children.push(parent);
    imgs.push(img);
    if (c.sibling) {
      const sib = make({ tagName: c.sibling.tag ?? 'DIV', parentElement: bodyEl, getBoundingClientRect: rectOf });
      styles.set(sib, style(c.sibling.style));
      img.stack = [img, sib, parent, bodyEl];
    }
    if (c.split) {
      const lightPlate = make({ tagName: 'DIV', parentElement: bodyEl });
      styles.set(lightPlate, style({ backgroundColor: white }));
      img.stackAt = (x, y) => (x < (rectOf().left + rectOf().right) / 2 ? [img, lightPlate, bodyEl] : [img, parent, bodyEl]);
    }
  });
  globalThis.document = {
    body: bodyEl,
    documentElement: bodyEl,
    createRange: () => ({ selectNodeContents: () => {}, getClientRects: () => [] }),
    elementsFromPoint: (x, y) => {
      const img = imgs.slice().sort((a, b) => a.rectOf().width - b.rectOf().width).find((m) => { const r = m.rectOf(); return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom; });
      if (!img) return [];
      if (img.stackAt) return img.stackAt(x, y);
      if (img.stack) return img.stack;
      const chain = [img];
      for (let n = img.parentElement; n; n = n.parentElement) chain.push(n);
      return chain;
    },
    createElement: () => {
      let drawn = null;
      return {
        width: 0,
        height: 0,
        getContext: () => ({
          drawImage: (image) => { drawn = image; },
          getImageData: () => {
            if (drawn.tainted) throw new Error('tainted');
            const px = drawn.pixels;
            const out = new Uint8ClampedArray(px.length * 4);
            px.forEach((p, k) => out.set([p.r, p.g, p.b, p.a], k * 4));
            return { data: out };
          },
        }),
      };
    },
  };
  globalThis.getComputedStyle = (el) => styles.get(el) ?? style();
  const result = measureLayout({ width: FRAME_WIDTH, height: FRAME_HEIGHT, minTextPx: 8, outputScale: 1, safeMargin: 0.05 });
  process.stdout.write(JSON.stringify({ cases: cases.map((c) => ({ name: c.name, expected: c.issue })), result }));
}

// ── text contrast fixture ─────────────────────────────────────────────────
// Each case is one text element (own computed colour / size) inside a parent that carries the
// background under test; `stack` overrides what elementsFromPoint reports (topmost first).
function runTextContrastFixture() {
  const make = (props) => Object.assign(new HTMLElement(), { nodeType: 1, className: '', id: '', childNodes: [], children: [], textContent: '', ...props });
  const styles = new Map();
  const dark = 'rgb(10, 10, 20)';
  const white = 'rgb(255, 255, 255)';
  const bodyEl = make({
    tagName: 'BODY',
    parentElement: null,
    getBoundingClientRect: () => ({ left: 0, top: 0, right: FRAME_WIDTH, bottom: FRAME_HEIGHT, width: FRAME_WIDTH, height: FRAME_HEIGHT }),
  });
  styles.set(bodyEl, style());
  const cases = [
    { name: 'a-dark-grey-on-dark', css: { color: 'rgb(45, 45, 55)' }, bg: { backgroundColor: dark }, issue: true },
    { name: 'b-light-on-light', css: { color: 'rgb(235, 235, 235)' }, bg: { backgroundColor: white }, issue: true },
    { name: 'c-white-on-dark', css: { color: 'rgb(255, 255, 255)' }, bg: { backgroundColor: dark }, issue: false },
    { name: 'd1-grey-small', css: { color: 'rgb(140, 140, 140)' }, bg: { backgroundColor: white }, issue: true },
    { name: 'd2-grey-large-30px', css: { color: 'rgb(140, 140, 140)', fontSize: '30px' }, bg: { backgroundColor: white }, issue: false },
    { name: 'd3-grey-bold-20px', css: { color: 'rgb(140, 140, 140)', fontSize: '20px', fontWeight: '700' }, bg: { backgroundColor: white }, issue: false },
    { name: 'd4-grey-regular-20px', css: { color: 'rgb(140, 140, 140)', fontSize: '20px', fontWeight: '400' }, bg: { backgroundColor: white }, issue: true },
    { name: 'e1-translucent-overlay-over-dark', css: { color: 'rgb(60, 60, 70)' }, bg: { backgroundColor: 'rgba(255, 255, 255, 0.1)' }, root: dark, issue: true },
    { name: 'e2-translucent-dark-overlay-over-white', css: { color: 'rgb(255, 255, 255)' }, bg: { backgroundColor: 'rgba(0, 0, 0, 0.7)' }, root: white, issue: false },
    { name: 'f1-dark-text-dark-gradient', css: { color: 'rgb(30, 30, 40)' }, bg: { backgroundImage: 'linear-gradient(rgb(10, 10, 20), rgb(40, 40, 70))' }, issue: true },
    { name: 'f2-dark-text-light-gradient', css: { color: 'rgb(30, 30, 40)' }, bg: { backgroundImage: 'linear-gradient(rgb(240, 240, 240), rgb(255, 255, 255))' }, issue: false },
    { name: 'g-video-backed-unknown', css: { color: 'rgb(45, 45, 55)' }, bg: { backgroundColor: dark }, video: true, issue: false },
    { name: 'h-text-shadow-skipped', css: { color: 'rgb(45, 45, 55)', textShadow: 'rgb(255, 255, 255) 0px 0px 4px' }, bg: { backgroundColor: dark }, issue: false },
    { name: 'i-text-stroke-skipped', css: { color: 'rgb(45, 45, 55)', webkitTextStrokeWidth: '2px' }, bg: { backgroundColor: dark }, issue: false },
    { name: 'j-transparent-colour-skipped', css: { color: 'rgba(0, 0, 0, 0)' }, bg: { backgroundColor: dark }, issue: false },
    { name: 'k-translucent-text-fades-into-dark', css: { color: 'rgba(255, 255, 255, 0.12)' }, bg: { backgroundColor: dark }, issue: true },
    { name: 'l-own-background-light-plate', css: { color: 'rgb(235, 235, 235)', backgroundColor: 'rgb(20, 20, 20)' }, bg: { backgroundColor: white }, issue: false },
  ];
  const rootDark = make({ tagName: 'DIV', parentElement: null });
  styles.set(rootDark, style({ backgroundColor: dark }));
  const rootWhite = make({ tagName: 'DIV', parentElement: null });
  styles.set(rootWhite, style({ backgroundColor: white }));
  const els = [];
  cases.forEach((c, i) => {
    const parent = make({ tagName: 'DIV', getBoundingClientRect: () => ({ left: 90, top: 90 + i * 40, right: 410, bottom: 120 + i * 40, width: 320, height: 30 }) });
    styles.set(parent, style(c.bg));
    parent.parentElement = c.root === dark ? rootDark : c.root === white ? rootWhite : bodyEl;
    const textNode = { nodeType: 3, textContent: c.name, rect: { left: 100, top: 95 + i * 40, right: 300, bottom: 115 + i * 40, width: 200, height: 20 } };
    const el = make({
      tagName: 'DIV',
      childNodes: [textNode],
      textContent: c.name,
      offsetWidth: 300, offsetHeight: 30, clientWidth: 300, clientHeight: 30, scrollWidth: 300, scrollHeight: 30,
      parentElement: parent,
      getBoundingClientRect: () => ({ left: 95, top: 92 + i * 40, right: 400, bottom: 118 + i * 40, width: 305, height: 26 }),
    });
    textNode.parentElement = el;
    styles.set(el, style({ fontWeight: '400', textShadow: 'none', webkitTextStrokeWidth: '0px', backgroundClip: 'border-box', ...c.css }));
    el.stack = [el];
    if (c.video) {
      const video = make({ tagName: 'VIDEO', parentElement: bodyEl });
      styles.set(video, style());
      el.stack.push(video);
    }
    for (let n = parent; n; n = n.parentElement) el.stack.push(n);
    el.caseName = c.name;
    els.push(el);
  });
  bodyEl.querySelectorAll = () => els;
  const ranges = [];
  globalThis.document = {
    body: bodyEl,
    documentElement: bodyEl,
    createRange: () => {
      const range = { node: null, selectNodeContents(n) { range.node = n; }, getClientRects: () => (range.node?.rect ? [range.node.rect] : []) };
      ranges.push(range);
      return range;
    },
    elementsFromPoint: (x, y) => {
      const el = els.find((e) => { const r = e.childNodes[0].rect; return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom; });
      return el ? el.stack : [];
    },
  };
  globalThis.getComputedStyle = (el) => styles.get(el) ?? style();
  const result = measureLayout({ width: FRAME_WIDTH, height: FRAME_HEIGHT, minTextPx: 8, outputScale: 1, safeMargin: 0.05 });
  process.stdout.write(JSON.stringify({ cases: cases.map((c) => ({ name: c.name, expected: c.issue })), result }));
}
