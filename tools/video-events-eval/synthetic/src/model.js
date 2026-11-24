// Pure timeline model shared by the Remotion renderer and the label generator.
// Every label frame is DERIVED by scanning the same functions the renderer draws from,
// so labels are exact by construction.

export const VOCAB = [
  'appear', 'disappear', 'move_start', 'move_stop', 'hit', 'text_in', 'text_out',
  'camera_move_start', 'camera_move_stop', 'transition', 'speech_start', 'speech_end', 'beat',
];

export const MOVE_EPS = 0.5; // px of displacement that counts as "moved" / "at rest"
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
export const smooth = (t) => { t = clamp(t, 0, 1); return t * t * (3 - 2 * t); };

// ---------- visibility ----------
const fadeIn = (v, f) => {
  const fade = v.fade ?? 6;
  return fade <= 0 ? (f >= v.in ? 1 : 0) : clamp((f - v.in) / fade, 0, 1);
};
const fadeOut = (v, f) => {
  if (v.out == null) return 1;
  const fade = v.fadeOut ?? v.fade ?? 6;
  return fade <= 0 ? (f >= v.out ? 0 : 1) : 1 - clamp((f - v.out) / fade, 0, 1);
};
const ivOpacity = (v, f) => (v.slide ? (f >= v.in ? 1 : 0) : fadeIn(v, f) * fadeOut(v, f));
export const opacityAt = (el, f) => {
  let best = 0;
  for (const v of el.vis || []) best = Math.max(best, ivOpacity(v, f));
  return best * (el.peak ?? 1);
};
const slideOffset = (el, f) => {
  let ox = 0, oy = 0;
  for (const v of el.vis || []) {
    if (!v.slide) continue;
    const fade = v.fade ?? 12;
    const pin = clamp((f - v.in) / fade, 0, 1);
    const pout = v.out == null ? 0 : clamp((f - v.out) / (v.fadeOut ?? fade), 0, 1);
    const k = (1 - pin) + pout;
    if (f < v.in || k > 0) { ox = v.slide[0] * k; oy = v.slide[1] * k; }
  }
  return [ox, oy];
};

// ---------- position ----------
export const baseAt = (el, f) => {
  let x = el.x, y = el.y;
  for (const m of el.moves || []) {
    if (f >= m.end) { x = m.x; y = m.y; continue; }
    if (f > m.start) {
      const t = smooth((f - m.start) / (m.end - m.start));
      return [x + (m.x - x) * t, y + (m.y - y) * t];
    }
    break;
  }
  return [x, y];
};

const ballCache = new WeakMap();
export const ballTrack = (el, frames) => {
  if (ballCache.has(el)) return ballCache.get(el);
  const tr = []; const hits = []; let restFrame = null;
  let x = el.x, y = el.y, vy = 0, vx = el.vx ?? 0, rest = false;
  for (let f = 0; f < frames; f++) {
    if (f > el.release && !rest) {
      vy += el.g ?? 1.8; y += vy; x += vx;
      if (y + el.w >= el.floor) {
        y = el.floor - el.w;
        if (vy > 3) { hits.push(f); vy = -vy * (el.e ?? 0.55); }
        else { vy = 0; vx = 0; rest = true; restFrame = f; }
      }
    }
    tr.push([x, y]);
  }
  const out = { tr, hits, restFrame };
  ballCache.set(el, out);
  return out;
};

const shakeOff = (el, f) => {
  let ox = 0;
  for (const h of el.hits || []) {
    if (h.kind === 'shake' && f >= h.frame && f < h.frame + 24) {
      ox += (h.amp ?? 14) * Math.cos((f - h.frame) * 1.7) * Math.exp(-(f - h.frame) / 8);
    }
  }
  return ox;
};
export const scaleAt = (el, f) => {
  let s = 1;
  for (const h of el.hits || []) {
    if (h.kind === 'pop' && f >= h.frame) s += (h.amp ?? 0.4) * Math.exp(-(f - h.frame) / 5);
    if (h.kind === 'press' && f >= h.frame && f < h.frame + 6) s -= 0.08;
  }
  return s;
};
export const posAt = (el, f, frames) => {
  let [x, y] = el.kind === 'ball' ? ballTrack(el, frames).tr[Math.min(f, frames - 1)] : baseAt(el, f);
  const [ox, oy] = slideOffset(el, f);
  return [x + ox + shakeOff(el, f), y + oy];
};
const bbox = (el, f, frames) => {
  const [x, y] = posAt(el, f, frames);
  return [Math.round(x), Math.round(y), el.w, el.h];
};
const clipBox = (b, W, H) => {
  const x0 = Math.max(0, b[0]), y0 = Math.max(0, b[1]);
  const x1 = Math.min(W, b[0] + b[2]), y1 = Math.min(H, b[1] + b[3]);
  return [x0, y0, Math.max(0, x1 - x0), Math.max(0, y1 - y0)];
};

// ---------- camera ----------
export const cameraAt = (spec, f) => {
  let p = [0, 0, 1];
  for (const k of spec.camera || []) {
    if (f >= k.end) { p = [k.x, k.y, k.s]; continue; }
    if (f > k.start) {
      const t = smooth((f - k.start) / (k.end - k.start));
      return [p[0] + (k.x - p[0]) * t, p[1] + (k.y - p[1]) * t, p[2] + (k.s - p[2]) * t];
    }
    break;
  }
  return p;
};
const camDist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], (a[2] - b[2]) * 400);

// ---------- scenes ----------
export const sceneIndexAt = (spec, f) => {
  let k = 0;
  (spec.scenes || []).forEach((s, i) => { if (f >= s.from) k = i; });
  return k;
};
export const sceneProgress = (s, f) => {
  // 0..1 reveal progress of scene layer s (cut: step at from; wipe/fade: centred on from)
  const d = s.dur ?? 0;
  if (s.trans === 'cut' || !s.trans || d <= 0) return f >= s.from ? 1 : 0;
  return clamp((f - (s.from - d / 2)) / d, 0, 1);
};

// ---------- events ----------
export function computeEvents(spec) {
  const W = spec.width, H = spec.height, N = spec.frames;
  const out = [];
  const push = (type, frame, region, note) => {
    if (!(frame >= 0 && frame < N)) throw new Error(`${spec.clip}: ${type} frame ${frame} outside [0,${N})`);
    out.push({ type, frame, region, note });
  };
  const firstFrom = (f0, f1, pred) => {
    for (let f = f0; f <= f1; f++) if (pred(f)) return f;
    throw new Error(`${spec.clip}: scan found nothing in ${f0}..${f1}`);
  };
  const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);

  for (const el of spec.elements) {
    const pe = (type, ...r) => { if (!(el.skip || []).includes(type)) push(type, ...r); }; // el.skip suppresses labels of a type
    if (el.quiet) continue; // decoration / context: drawn but never labelled
    const isText = el.kind === 'text' || el.kind === 'typed';
    const nm = el.name || el.id;
    for (const v of el.vis || []) {
      if (v.in === 0) continue; // present from the first frame: no appear event
      const peak = el.peak ?? 1;
      if (v.slide) {
        const fa = firstFrom(v.in, N - 1, (f) => slideOffset(el, f)[0] === 0 && slideOffset(el, f)[1] === 0);
        pe(isText ? 'text_in' : 'appear', fa, clipBox(bbox(el, fa, N), W, H), `${nm} slides fully into place`);
        if (v.out != null) {
          const fd = firstFrom(v.out, N - 1, (f) => clipBox(bbox(el, f, N), W, H)[2] === 0 || clipBox(bbox(el, f, N), W, H)[3] === 0);
          pe(isText ? 'text_out' : 'disappear', fd, clipBox(bbox(el, v.out, N), W, H), `${nm} slides fully off canvas`);
        }
        continue;
      }
      const fa = firstFrom(v.in, N - 1, (f) => ivOpacity(v, f) * peak >= 0.5 * peak && ivOpacity(v, f) > 0);
      pe(isText ? 'text_in' : 'appear', fa, clipBox(bbox(el, fa, N), W, H), `${nm} reaches half of its peak opacity`);
      if (v.out != null) {
        const fd = firstFrom(v.out, N - 1, (f) => ivOpacity(v, f) < 0.5);
        pe(isText ? 'text_out' : 'disappear', fd, clipBox(bbox(el, v.out, N), W, H), `${nm} drops below half of its peak opacity`);
      }
    }
    if (el.kind === 'ball') {
      const { tr, hits, restFrame } = ballTrack(el, N);
      const ms = firstFrom(el.release, N - 1, (f) => dist(tr[f], tr[el.release]) >= MOVE_EPS);
      pe('move_start', ms, clipBox(bbox(el, ms, N), W, H), `${nm} released and starts falling`);
      for (const h of hits) pe('hit', h, clipBox(bbox(el, h, N), W, H), `${nm} touches the floor`);
      if (restFrame != null) pe('move_stop', restFrame, clipBox(bbox(el, restFrame, N), W, H), `${nm} comes to rest on the floor`);
    }
    for (const m of el.moves || []) {
      const s0 = baseAt(el, m.start), s1 = baseAt(el, m.end);
      const a = firstFrom(m.start, m.end, (f) => dist(baseAt(el, f), s0) >= MOVE_EPS);
      const b = firstFrom(m.start, m.end, (f) => f > a && dist(baseAt(el, f), s1) < MOVE_EPS);
      pe('move_start', a, clipBox(bbox(el, a, N), W, H), `${nm} starts moving`);
      pe('move_stop', b, clipBox(bbox(el, b, N), W, H), `${nm} is at rest again`);
    }
    for (const h of el.hits || []) {
      if (h.kind === 'pop' || h.kind === 'press') {
        if (Math.abs(scaleAt(el, h.frame) - 1) < 0.05) throw new Error(`${spec.clip}: ${el.id} ${h.kind} too subtle`);
      }
      pe('hit', h.frame, clipBox(bbox(el, h.frame, N), W, H), `${nm} ${h.kind === 'shake' ? 'shake begins' : h.kind === 'pop' ? 'scale pop' : 'button press'}`);
    }
    if (el.kind === 'cursor') {
      for (const c of el.clicks || []) pe('hit', c, clipBox([...posAt(el, c, N).map(Math.round), 24, 24], W, H), `${nm} click (ripple starts)`);
    }
  }

  const cams = spec.camera || [];
  for (const k of cams) {
    const s0 = cameraAt(spec, k.start), s1 = cameraAt(spec, k.end);
    const a = firstFrom(k.start, k.end, (f) => camDist(cameraAt(spec, f), s0) >= MOVE_EPS);
    const b = firstFrom(k.start, k.end, (f) => f > a && camDist(cameraAt(spec, f), s1) < MOVE_EPS);
    push('camera_move_start', a, [0, 0, W, H], 'global camera pan/zoom begins');
    push('camera_move_stop', b, [0, 0, W, H], 'global camera pan/zoom at rest');
  }
  (spec.scenes || []).forEach((s, i) => {
    if (i === 0) return;
    const ar = spec.area || [0, 0, W, H];
    push('transition', s.from, ar, `scene ${i} ${s.trans || 'cut'}: ${s.trans === 'cut' || !s.trans ? 'first frame of new scene' : 'midpoint of the blend'}`);
  });
  for (const sp of spec.speech || []) {
    push('speech_start', sp.start, undefined, 'voice-over phrase onset');
    push('speech_end', sp.end, undefined, 'voice-over phrase offset');
  }
  for (const f of spec.beats || []) push('beat', f, undefined, 'kick drum onset');
  out.sort((a, b) => a.frame - b.frame || VOCAB.indexOf(a.type) - VOCAB.indexOf(b.type));
  return out.map((e, i) => ({ id: `ev${i + 1}`, ...e }));
}
