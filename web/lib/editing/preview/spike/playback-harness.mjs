/**
 * T-201 — compositor performance-budget harness.  REMOVABLE (see README.md).
 *
 * Runs in a real Chromium page (served by `serve.mjs`, driven by `run-playback.mjs`) and
 * measures the §3.5 scenario: **4 video layers + 6 audio tracks, 1080p proxies, 30 fps**.
 *
 * Contract: `window.runScenario(opts)` returns a plain, JSON-serialisable report. Every
 * number is taken here, in the browser, with `performance.now()` around the real call —
 * the driver only aggregates across runs.
 *
 * ── What is actually being exercised ────────────────────────────────────────────────
 *
 *   * 4 × `VideoDecoder` (WebCodecs), one per layer, each fed from its OWN
 *     `mediabunny.EncodedPacketSink` over its own `Input`. Four independent decoders is
 *     the load under test: 4 × 30 = 120 frames of 1080p H.264 decoded per second.
 *   * 1 × WebGL2 context compositing all four decoded `VideoFrame`s into ONE canvas per
 *     presented frame: 4 texture uploads + 4 blended full-frame draws + 4 marker quads,
 *     `gl.finish()` included inside the timed region.
 *   * 6 × WebAudio tracks: six `AudioBufferSourceNode`s (looping) → per-stem `GainNode`
 *     → master `GainNode` → `AnalyserNode` → destination, all started together and left
 *     running for the whole window.
 *   * A single master clock (`performance.now()`), never the decoder's own timing, so a
 *     decoder that runs fast cannot move the playhead and flatter the result.
 *
 * ── The four numbers, deliberately NOT blended ──────────────────────────────────────
 *
 *   achievedFps / droppedFrames  — deadline accounting against the 30 fps target
 *   decodeLatency                — p50/p95/p99, per layer, submit → decoded output
 *   glComposite                  — p50/p95 of upload and of draw, separately
 *   audio                        — per-stem RMS + clock drift, i.e. "audio stayed master"
 *
 * A dropped frame is a **frame deadline missed without the right frame on screen**: the
 * deadline for frame `i` is `t0 + i/30`, and if the render tick that owns it finds frame
 * `i` still absent, `i` is counted dropped and the newest available frame is re-presented
 * (so `displayedFps` can be 30 while `achievedFps` is not — both are reported).
 *
 * ── Why the four layers read the SAME proxy file ────────────────────────────────────
 *
 * Deliberate, and stated in the report rather than hidden. At a fixed resolution / profile
 * / level / GOP, a decoder's cost is governed by the bitstream's parameters, not by which
 * pictures the bitstream happens to encode; and each layer holds an independent decoder
 * instance, so "4 concurrent 1080p30 H.264 decoders" is exactly what is measured. Four
 * separate 6.5 MB fixtures would cost 26 MB of committed throwaway data to measure the
 * same quantity. The four layers are given distinct tints, distinct blend alphas and
 * distinct marker quads, and `compositeCheck` reads the pixels back, so a layer that
 * silently failed to decode or draw is detected rather than averaged away.
 *
 * ⚠ The §3.5 fallback is NOT a description here — `runScenario` picks the path itself.
 * With WebCodecs present it composites; with `VideoDecoder` made unavailable (see
 * `window.__spikeDisableWebCodecs`) it takes the fallback, a single `<video>` over the
 * proxy, and reports `path: 'fallback-video-element'` plus that player's own numbers.
 */

import * as mediabunny from '/node_modules/mediabunny/dist/modules/src/index.js';

const PROXY_URL = './fixtures/playback/proxy-1080p-36s.mp4';
const STEM_URLS = [1, 2, 3, 4, 5, 6].map((n) => `./fixtures/playback/stem-${n}.m4a`);

const TARGET_FPS = 30;
/** Committed property of `make-playback-fixture.sh`, not something a library reported. */
const SOURCE_DURATION_SEC = 36;
const LAYERS = 4;
/** One forced GL sync every N presented frames — see the comment at the sample site. */
const SYNC_SAMPLE_EVERY = 30;

/**
 * The decoder pool under test. These are the knobs the ticket asks to be *pinned*, so they
 * are stated once here and carried into every metric label.
 */
const POOL = {
  decodersPerLayer: 1,
  lookAheadFrames: 12,
  lookAheadSec: 12 / TARGET_FPS,
  maxDecodedBufferedFrames: 18,
  maxEncodedQueuedChunks: 8,
};

/**
 * The decoder-pool invariant, enforced at CONSTRUCTION rather than documented and hoped for.
 *
 * `BUDGET.md` records the measurement behind this: a pool whose `lookAheadFrames` exceeded
 * `maxDecodedBufferedFrames` produced **0.00 fps, 0 frames presented and 2292 frames evicted
 * unseen — while reporting healthy**. The failure is silent and reads as a slow machine, which is
 * the worst possible shape for a perf bug, and it is why this is a throw rather than a warning.
 *
 * The `+ 2` is the headroom the spike recorded: the pool must hold the whole look-ahead window PLUS
 * the frame being presented and the one being fetched. Exactly meeting the window is not enough,
 * because those two in-flight frames get evicted from under the presenter.
 *
 * Named and exported so the drift test can call it on hand-built pools, and so a caller that
 * deliberately wants to reproduce the failure has to opt in explicitly (see `__spikeSetPool`).
 */
export function assertPoolInvariant(pool) {
  const lookAhead = pool.lookAheadFrames;
  const ceiling = pool.maxDecodedBufferedFrames;
  if (!Number.isFinite(lookAhead) || !Number.isFinite(ceiling)) {
    throw new Error(
      `decoder pool invariant: lookAheadFrames=${lookAhead} maxDecodedBufferedFrames=${ceiling} must both be finite`,
    );
  }
  if (ceiling < lookAhead + 2) {
    throw new Error(
      `decoder pool invariant violated: maxDecodedBufferedFrames (${ceiling}) must be >= ` +
        `lookAheadFrames + 2 (${lookAhead + 2}). Exceeding it produced 0.00 fps with 2292 frames ` +
        `evicted unseen while reporting healthy — see BUDGET.md.`,
    );
  }
  return pool;
}

// The shipped configuration is checked the moment this module loads, so an edit to the constants
// above fails loudly instead of quietly measuring a pool that cannot work.
assertPoolInvariant(POOL);

/** Per-layer identity: what makes four decodes four *visible* layers. */
const LAYER_SPECS = [
  { name: 'V1', tint: [1.0, 0.55, 0.55], alpha: 0.5, marker: [1, 0, 0], corner: [0, 1] },
  { name: 'V2', tint: [0.55, 1.0, 0.55], alpha: 0.5, marker: [0, 1, 0], corner: [1, 1] },
  { name: 'V3', tint: [0.55, 0.55, 1.0], alpha: 0.5, marker: [0, 0, 1], corner: [0, 0] },
  { name: 'V4', tint: [1.0, 1.0, 0.45], alpha: 0.5, marker: [1, 1, 0], corner: [1, 0] },
];

// ─── small stats helpers ─────────────────────────────────────────────────────────────
const sorted = (xs) => [...xs].sort((a, b) => a - b);
function quantile(s, q) {
  if (s.length === 0) return null;
  if (s.length === 1) return s[0];
  const pos = (s.length - 1) * q;
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  return lo === hi ? s[lo] : s[lo] + (s[hi] - s[lo]) * (pos - lo);
}
function stats(xs, digits = 2) {
  if (xs.length === 0) return { n: 0 };
  const s = sorted(xs);
  const round = (v) => (v === null ? null : +v.toFixed(digits));
  const mean = s.reduce((a, b) => a + b, 0) / s.length;
  return {
    n: s.length,
    min: round(s[0]),
    p50: round(quantile(s, 0.5)),
    p95: round(quantile(s, 0.95)),
    p99: round(quantile(s, 0.99)),
    max: round(s[s.length - 1]),
    mean: round(mean),
    stdDev: round(Math.sqrt(s.reduce((a, v) => a + (v - mean) ** 2, 0) / s.length)),
  };
}
const log = (m, isErr = false) => {
  const el = typeof document !== 'undefined' ? document.getElementById('log') : null;
  const line = `${new Date().toISOString().slice(11, 23)}  ${m}`;
  if (el) el.textContent += (isErr ? '\n[err] ' : '\n') + line;
  (isErr ? console.error : console.log)(line);
};

// ─── capabilities ────────────────────────────────────────────────────────────────────
function glIdentity() {
  const out = {
    available: false,
    contextType: null,
    debugRendererInfoExtension: false,
    renderer: null,
    vendor: null,
    maskedRenderer: null,
    maskedVendor: null,
    version: null,
    maxTextureSize: null,
    isSoftware: null,
  };
  try {
    const c = document.createElement('canvas');
    const gl = c.getContext('webgl2');
    if (!gl) {
      out.renderer = 'no webgl2 context';
      return out;
    }
    out.available = true;
    out.contextType = 'webgl2';
    const dbg = gl.getExtension('WEBGL_debug_renderer_info');
    out.debugRendererInfoExtension = Boolean(dbg);
    out.renderer = dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER);
    out.vendor = dbg ? gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL) : gl.getParameter(gl.VENDOR);
    out.maskedRenderer = gl.getParameter(gl.RENDERER);
    out.maskedVendor = gl.getParameter(gl.VENDOR);
    out.version = gl.getParameter(gl.VERSION);
    out.maxTextureSize = gl.getParameter(gl.MAX_TEXTURE_SIZE);
    out.isSoftware = /swiftshader|software|llvmpipe|basic render/i.test(String(out.renderer));
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return out;
  } catch (e) {
    out.renderer = `probe threw: ${e.message}`;
    return out;
  }
}

function webCodecsProbe() {
  return {
    videoDecoder: typeof VideoDecoder !== 'undefined',
    encodedVideoChunk: typeof EncodedVideoChunk !== 'undefined',
    requestVideoFrameCallback: typeof HTMLVideoElement !== 'undefined' &&
      typeof HTMLVideoElement.prototype.requestVideoFrameCallback === 'function',
  };
}

function measureTimerResolutionUs() {
  // Chromium clamps performance.now() to 100 µs. A raw median of 0 is the clock floor,
  // not a measurement of zero, so it is reported alongside every median.
  const diffs = [];
  let prev = performance.now();
  let guard = 0;
  while (diffs.length < 2000 && guard < 200000) {
    guard++;
    const now = performance.now();
    if (now > prev) {
      diffs.push(now - prev);
      prev = now;
    }
  }
  const s = sorted(diffs);
  return +(quantile(s, 0.0) * 1000).toFixed(1);
}

// ─── one layer: its own demuxer sink + its own decoder + a bounded frame buffer ──────
class Layer {
  constructor(spec, index, bytes, decoderConfig) {
    this.spec = spec;
    this.index = index;
    this.config = decoderConfig;
    this.bytes = bytes;

    /** Decoded frames by presentation index, oldest first (insertion order = PTS order). */
    this.frames = new Map();
    /**
     * The frame currently on screen, and its index. The two are updated TOGETHER and only
     * when a new frame is actually taken, and the held frame is EXEMPT from eviction: it is
     * what the compositor re-uploads when a deadline is missed, and closing it would turn a
     * dropped frame into a `texImage2D: can't texture a closed VideoFrame` no-op — i.e. a
     * silently blank layer rather than a reported drop.
     */
    this.held = null;
    this.heldIndex = -1;
    /** Set by the scenario; lets the feeder compute the playhead without the rAF loop. */
    this.playheadFn = () => 0;

    this.submittedThroughSec = 0;
    this.nextPacket = null;
    this.iter = null;
    this.pumpBusy = false;
    this.ended = false;

    this.decodeLatencyUs = [];
    this.outputIntervalUs = [];
    this.lastOutputUs = undefined;
    this.submitTimeByTsUs = new Map();
    this.decodedCount = 0;
    this.submittedCount = 0;
    this.droppedDecodedFrames = 0; // decoded but evicted before it could be presented
    this.startedAt = 0;
    this.errors = [];
    this.input = null;
  }

  async open() {
    const t0 = performance.now();
    this.input = new mediabunny.Input({
      source: new mediabunny.BlobSource(new Blob([this.bytes])),
      formats: mediabunny.ALL_FORMATS,
    });
    const videoTrack = await this.input.getPrimaryVideoTrack();
    if (!videoTrack) throw new Error(`${this.spec.name}: no video track`);
    this.sink = new mediabunny.EncodedPacketSink(videoTrack);
    const firstKey = await this.sink.getFirstKeyPacket();
    if (!firstKey) throw new Error(`${this.spec.name}: no first key packet`);
    this.firstKeyPacket = firstKey;
    this.iter = this.sink.packets(firstKey);
    this.openMs = performance.now() - t0;
    return this;
  }

  /**
   * Back to frame 0 with a fresh decoder and a fresh packet iterator, and every counter
   * reset. Used only between the measurement controls and the measured run, so that the
   * controls' decode work never leaks into the run's numbers.
   */
  restart() {
    try {
      this.decoder?.close();
    } catch {
      /* already closed */
    }
    for (const f of this.frames.values()) {
      try {
        f.close();
      } catch {
        /* already closed */
      }
    }
    this.frames.clear();
    this.held = null;
    this.heldIndex = -1;
    this.submitTimeByTsUs.clear();
    this.decodeLatencyUs = [];
    this.outputIntervalUs = [];
    this.lastOutputUs = undefined;
    this.decodedCount = 0;
    this.submittedCount = 0;
    this.droppedDecodedFrames = 0;
    this.submittedThroughSec = 0;
    this.ended = false;
    this.errors = [];
    this.iter = this.sink.packets(this.firstKeyPacket);
    this.startDecoder();
  }

  startDecoder() {
    this.decoder = new VideoDecoder({
      output: (frame) => this.onFrame(frame),
      error: (e) => {
        this.errors.push(`decoder error: ${e.message}`);
      },
    });
    this.decoder.configure(this.config);
  }

  onFrame(frame) {
    const nowUs = performance.now() * 1000;
    const tsUs = frame.timestamp;
    const submittedAt = this.submitTimeByTsUs.get(tsUs);
    if (submittedAt !== undefined) {
      this.decodeLatencyUs.push(nowUs - submittedAt);
      this.submitTimeByTsUs.delete(tsUs);
    }
    // Inter-output gap: this layer's own decode THROUGHPUT, independent of how deep the
    // pool's queue is. `decodeLatencyUs` above includes the look-ahead's queue wait, so it
    // answers "when is the frame ready on screen", not "how fast is the codec".
    if (this.lastOutputUs !== undefined) this.outputIntervalUs.push(nowUs - this.lastOutputUs);
    this.lastOutputUs = nowUs;
    const index = Math.round((tsUs / 1e6) * TARGET_FPS);
    this.frames.set(index, frame);
    this.decodedCount++;
    // Eviction: close the OLDEST decoded frame once the buffer exceeds its ceiling.
    // Frames are inserted in presentation order, and Map iterates in insertion order, so
    // the first key is the oldest. Closing frees the underlying buffer; leaking these is
    // how a WebCodecs harness runs out of memory instead of reporting a number.
    while (this.frames.size > POOL.maxDecodedBufferedFrames) {
      const oldest = this.frames.keys().next().value;
      if (oldest === undefined) break;
      if (oldest === this.heldIndex) break; // never evict what is on screen
      const f = this.frames.get(oldest);
      this.frames.delete(oldest);
      this.droppedDecodedFrames++;
      try {
        f.close();
      } catch {
        /* already closed */
      }
    }
    // Self-sustaining feed: a decoder that only gets fed by the render loop starves as
    // soon as the render loop is the thing that is behind, and then its measured "decode
    // latency" is really a measurement of the render cadence. A real player feeds from the
    // decoder's own drain, never from rAF.
    setTimeout(() => this.pump(this.playheadFn()).catch(() => {}), 0);
  }

  /** Push encoded chunks until we are `lookAheadFrames` ahead of the playhead. */
  async pump(playheadSec) {
    if (this.pumpBusy || this.ended) return;
    this.pumpBusy = true;
    try {
      while (!this.ended) {
        const aheadSec = this.submittedThroughSec - playheadSec;
        const pressured =
          aheadSec >= POOL.lookAheadSec ||
          this.decoder.decodeQueueSize >= POOL.maxEncodedQueuedChunks;
        if (pressured) break;
        const { value: pkt, done } = await this.iter.next();
        if (done || !pkt) {
          this.ended = true;
          break;
        }
        const tsUs = Math.round(pkt.timestamp * 1e6);
        this.submitTimeByTsUs.set(tsUs, performance.now() * 1000);
        try {
          this.decoder.decode(
            new EncodedVideoChunk({
              type: pkt.type === 'key' ? 'key' : 'delta',
              timestamp: tsUs,
              duration: Math.round(pkt.duration * 1e6),
              data: pkt.data,
            }),
          );
        } catch (e) {
          this.errors.push(`decode() threw: ${e.message}`);
        }
        this.submittedCount++;
        this.submittedThroughSec = pkt.timestamp + (pkt.duration || 1 / TARGET_FPS);
      }
    } finally {
      this.pumpBusy = false;
    }
  }

  take(index) {
    const f = this.frames.get(index);
    if (!f) return null;
    // Everything strictly older than the frame we are about to present is unreachable
    // from this playhead, so close it now rather than waiting for the ceiling. The frame
    // currently on screen is exempt — see `heldIndex`.
    for (const k of [...this.frames.keys()]) {
      if (k < index && k !== this.heldIndex) {
        const old = this.frames.get(k);
        this.frames.delete(k);
        try {
          old.close();
        } catch {
          /* already closed */
        }
      }
    }
    return f;
  }

  decodedAhead(playheadIndex) {
    let n = 0;
    for (const k of this.frames.keys()) if (k >= playheadIndex) n++;
    return n;
  }

  dispose() {
    try {
      this.decoder?.close();
    } catch {
      /* already closed */
    }
    for (const f of this.frames.values()) {
      try {
        f.close();
      } catch {
        /* already closed */
      }
    }
    this.frames.clear();
    this.input?.dispose?.();
  }
}

// ─── WebGL2 compositor ──────────────────────────────────────────────────────────────
const VS = `#version 300 es
in vec2 aPos;
uniform vec4 uRect;   // x, y, w, h in NDC
out vec2 vUv;
void main() {
  vec2 p = uRect.xy + (aPos * 0.5 + 0.5) * uRect.zw;
  gl_Position = vec4(p, 0.0, 1.0);
  // Video textures are top-left origin; NDC is bottom-left, so V is flipped.
  vUv = vec2(aPos.x * 0.5 + 0.5, 1.0 - (aPos.y * 0.5 + 0.5));
}`;

const FS = `#version 300 es
precision mediump float;
in vec2 vUv;
uniform sampler2D uTex;
uniform vec3 uTint;
uniform float uAlpha;
uniform float uSolid;      // 1.0 on the marker pass: emit uTint flat, ignore the texture
out vec4 outColor;
void main() {
  if (uSolid > 0.5) {
    outColor = vec4(uTint, 1.0);
    return;
  }
  outColor = texture(uTex, vUv) * vec4(uTint, uAlpha);
}`;

function createCompositor(canvas, width, height) {
  canvas.width = width;
  canvas.height = height;
  const gl = canvas.getContext('webgl2', {
    alpha: false,
    antialias: false,
    depth: false,
    stencil: false,
    desynchronized: false,
    powerPreference: 'high-performance',
  });
  if (!gl) throw new Error('webgl2 context unavailable');

  const compile = (type, src) => {
    const s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
      throw new Error(`shader: ${gl.getShaderInfoLog(s)}`);
    }
    return s;
  };
  const prog = gl.createProgram();
  gl.attachShader(prog, compile(gl.VERTEX_SHADER, VS));
  gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, FS));
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
    throw new Error(`link: ${gl.getProgramInfoLog(prog)}`);
  }
  gl.useProgram(prog);

  const vbo = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
  const aPos = gl.getAttribLocation(prog, 'aPos');
  gl.enableVertexAttribArray(aPos);
  gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);

  const loc = (n) => gl.getUniformLocation(prog, n);
  const U = {
    rect: loc('uRect'),
    tint: loc('uTint'),
    alpha: loc('uAlpha'),
    solid: loc('uSolid'),
    tex: loc('uTex'),
  };

  const textures = [];
  for (let i = 0; i < LAYERS; i++) {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    textures.push(t);
  }

  gl.viewport(0, 0, width, height);
  gl.clearColor(0, 0, 0, 1);
  gl.enable(gl.BLEND);
  gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

  /** Marker size in UV space (a square of ~1/10 of the shorter side). */
  const markerUv = { x: 0.08, y: 0.08 };
  /**
   * A layer's marker rectangle in NDC, from its `corner` = [xRight, yTop].
   * Canvas UV has its origin at the TOP-left; NDC has its origin at the BOTTOM-left, so
   * `ndcY = 1 - uvY * 2`. Getting this backwards puts every marker in the opposite corner
   * and makes the readback below compare a layer against a DIFFERENT layer's marker —
   * which is exactly the false "0 of 4 verified" this comment exists to prevent.
   */
  function markerNdcRect(corner) {
    const uvX = corner[0] === 0 ? 0.02 : 1 - 0.02 - markerUv.x;
    const uvY = corner[1] === 1 ? 0.02 : 1 - 0.02 - markerUv.y;
    return [uvX * 2 - 1, 1 - (uvY + markerUv.y) * 2, markerUv.x * 2, markerUv.y * 2];
  }

  return {
    gl,
    textures,
    /**
     * Upload + draw all four layers. Timed by the caller; `upload` and `draw` are split so
     * a slow texture upload (the WebCodecs→GL handoff) is never hidden inside a total.
     */
    upload(frames) {
      for (let i = 0; i < LAYERS; i++) {
        const f = frames[i];
        // A null here is a layer with no frame at all yet. texImage2D(null) is not a no-op:
        // it throws "Overload resolution failed" and kills the whole frame loop.
        if (!f) continue;
        gl.activeTexture(gl.TEXTURE0 + i);
        gl.bindTexture(gl.TEXTURE_2D, textures[i]);
        // VideoFrame is a valid TexImageSource in Chromium: this is the zero-copy-ish
        // path the real compositor uses.
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, f);
      }
    },
    draw() {
      gl.clear(gl.COLOR_BUFFER_BIT);
      // Pass 1 — the layer stack: 4 full-frame blended draws, stacked in V1..V4 order.
      gl.uniform1f(U.solid, 0);
      for (let i = 0; i < LAYERS; i++) {
        gl.activeTexture(gl.TEXTURE0 + i);
        gl.bindTexture(gl.TEXTURE_2D, textures[i]);
        gl.uniform1i(U.tex, i);
        gl.uniform4f(U.rect, -1, -1, 2, 2); // full-frame
        gl.uniform3fv(U.tint, LAYER_SPECS[i].tint);
        gl.uniform1f(U.alpha, LAYER_SPECS[i].alpha);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      }
      // Pass 2 — one small solid quad per layer in its own corner. Drawn AFTER the stack,
      // because a marker drawn during the stack would be washed out by the layers above
      // it and the readback could not then distinguish "layer drew" from "layer blended".
      gl.uniform1f(U.solid, 1);
      for (let i = 0; i < LAYERS; i++) {
        const [x, y, w, h] = markerNdcRect(LAYER_SPECS[i].corner);
        gl.uniform4f(U.rect, x, y, w, h);
        gl.uniform3fv(U.tint, LAYER_SPECS[i].marker);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      }
    },
    readback() {
      // Called in the SAME task as a draw, so it does not need preserveDrawingBuffer.
      const px = (fx, fy) => {
        const buf = new Uint8Array(4);
        gl.readPixels(
          Math.min(width - 1, Math.max(0, Math.round(fx * width))),
          Math.min(height - 1, Math.max(0, Math.round(fy * height))),
          1,
          1,
          gl.RGBA,
          gl.UNSIGNED_BYTE,
          buf,
        );
        return [buf[0], buf[1], buf[2], buf[3]];
      };
      return {
        markers: LAYER_SPECS.map((s) => {
          const [x, y, w, h] = markerNdcRect(s.corner);
          // readPixels is bottom-left origin — the same space as NDC.
          const got = px((x + w / 2 + 1) / 2, (y + h / 2 + 1) / 2);
          return { layer: s.name, expectedRgb: s.marker.map((v) => v * 255), gotRgb: got };
        }),
        center: px(0.5, 0.5),
      };
    },
  };
}

// ─── WebAudio: six real tracks ───────────────────────────────────────────────────────
async function buildAudioGraph(ctx) {
  const buffers = [];
  const loadStart = performance.now();
  for (const url of STEM_URLS) {
    const res = await fetch(url, { cache: 'no-store' });
    if (!res.ok) throw new Error(`stem fetch failed ${url}: HTTP ${res.status}`);
    const ab = await res.arrayBuffer();
    buffers.push(await ctx.decodeAudioData(ab));
  }
  const loadMs = performance.now() - loadStart;

  const master = ctx.createGain();
  master.gain.value = 1 / STEM_URLS.length; // headroom: six full-scale stems would clip
  const analyser = ctx.createAnalyser();
  analyser.fftSize = 2048;
  master.connect(analyser);
  analyser.connect(ctx.destination);

  const stems = buffers.map((buf) => {
    const src = ctx.createBufferSource();
    src.buffer = buf;
    src.loop = true; // gapless: buffer looping decodes nothing and costs no seek
    const gain = ctx.createGain();
    gain.gain.value = 1;
    const stemAnalyser = ctx.createAnalyser();
    stemAnalyser.fftSize = 1024;
    src.connect(gain);
    gain.connect(master);
    gain.connect(stemAnalyser);
    return { src, gain, analyser: stemAnalyser, buf, timeData: new Float32Array(stemAnalyser.fftSize) };
  });

  const startAt = ctx.currentTime + 0.1;
  for (const s of stems) s.src.start(startAt);

  return {
    loadMs,
    buffers: buffers.map((b) => ({
      durationSec: +b.duration.toFixed(3),
      sampleRate: b.sampleRate,
      channels: b.numberOfChannels,
      frames: b.length,
    })),
    analyser,
    master,
    stems,
    timeData: new Float32Array(analyser.fftSize),
    sampleRate: ctx.sampleRate,
    state: ctx.state,
  };
}

function rmsOf(analyser, timeData) {
  analyser.getFloatTimeDomainData(timeData);
  let sum = 0;
  for (let i = 0; i < timeData.length; i++) sum += timeData[i] * timeData[i];
  return Math.sqrt(sum / timeData.length);
}

/** rAF ticks per second over `ms`, with `onTick` run in every callback. */
async function measureRafHz(ms, onTick) {
  let n = 0;
  const t = performance.now();
  const elapsed = await new Promise((res) => {
    const tick = () => {
      n++;
      if (onTick) onTick();
      const dt = performance.now() - t;
      if (dt >= ms) res(dt);
      else requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  return { ticks: n, hz: +(n / (elapsed / 1000)).toFixed(2), elapsedMs: +elapsed.toFixed(1) };
}

// ─── the fallback path: a single <video> over the proxy (§3.5) ───────────────────────
async function runFallbackVideoScenario({ seconds }) {
  log('WebCodecs unavailable -> taking the §3.5 fallback path (single-track <video>)');
  const video = document.createElement('video');
  video.src = PROXY_URL;
  video.muted = true;
  video.playsInline = true;
  video.loop = true;
  video.preload = 'auto';
  video.style.width = '640px';
  video.style.display = 'block';
  document.body.appendChild(video);

  const waitEvent = (name, timeoutMs) =>
    new Promise((res, rej) => {
      const t = setTimeout(() => rej(new Error(`timeout waiting for '${name}'`)), timeoutMs);
      video.addEventListener(name, () => {
        clearTimeout(t);
        res(performance.now());
      }, { once: true });
    });

  const tPlay = performance.now();
  const loaded = waitEvent('loadeddata', 20000);
  await video.play().catch((e) => log(`video.play() rejected: ${e.message}`, true));
  await loaded;
  const firstFrameMs = performance.now() - tPlay;
  await waitEvent('playing', 20000).catch(() => null);

  // Count presented frames with requestVideoFrameCallback when the build has it; a plain
  // rAF sampler would count render ticks, not decoded frames, and would overstate the
  // fallback's health.
  const useRvfc = typeof video.requestVideoFrameCallback === 'function';
  let rvfcFrames = 0;
  let rvfcFirstExpectedDisplayMs = null;
  let rvfcLastExpectedDisplayMs = null;
  if (useRvfc) {
    const tRvfcStart = performance.now();
    const onFrame = (_now, meta) => {
      rvfcFrames++;
      if (rvfcFirstExpectedDisplayMs === null) {
        rvfcFirstExpectedDisplayMs = meta.expectedDisplayTime - tRvfcStart;
      }
      rvfcLastExpectedDisplayMs = meta.expectedDisplayTime - tRvfcStart;
      video.requestVideoFrameCallback(onFrame);
    };
    video.requestVideoFrameCallback(onFrame);
  }

  const qualityStart = video.getVideoPlaybackQuality?.() ?? null;
  const t0 = performance.now();
  let rafTicks = 0;
  await new Promise((res) => {
    const tick = () => {
      rafTicks++;
      if ((performance.now() - t0) / 1000 >= seconds) res();
      else requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  const wallMs = performance.now() - t0;
  const qualityEnd = video.getVideoPlaybackQuality?.() ?? null;

  video.pause();
  video.removeAttribute('src');
  video.load();
  video.remove();

  const wallSec = wallMs / 1000;
  return {
    harness: 'T-201 compositor budget — fallback path',
    path: 'fallback-video-element',
    fallbackReason:
      'WebCodecs VideoDecoder is not available in this page, so the compositor cannot run; ' +
      'the §3.5 degradation is a single <video> over the proxy.',
    requiresWebCodecs: false,
    videoElement: {
      readyStateAfter: video.readyState,
      firstFrameMs: +firstFrameMs.toFixed(2),
      currentSrc: video.currentSrc || PROXY_URL,
      sourceDurationSec: Number.isFinite(video.duration) ? +video.duration.toFixed(3) : null,
      requestVideoFrameCallback: useRvfc,
    },
    measuredWindowSec: +wallSec.toFixed(3),
    achieved: {
      rafTicks,
      rafHz: +(rafTicks / wallSec).toFixed(2),
      decodedFramesPresented: useRvfc ? rvfcFrames : null,
      achievedFps: useRvfc ? +(rvfcFrames / wallSec).toFixed(2) : null,
      targetFps: TARGET_FPS,
      firstFrameExpectedDisplayMs:
        rvfcFirstExpectedDisplayMs === null ? null : +rvfcFirstExpectedDisplayMs.toFixed(1),
      lastFrameExpectedDisplayMs:
        rvfcLastExpectedDisplayMs === null ? null : +rvfcLastExpectedDisplayMs.toFixed(1),
    },
    playbackQuality: {
      start: qualityStart,
      end: qualityEnd,
      totalVideoFramesDelta:
        qualityStart && qualityEnd ? qualityEnd.totalVideoFrames - qualityStart.totalVideoFrames : null,
      droppedVideoFramesDelta:
        qualityStart && qualityEnd ? qualityEnd.droppedVideoFrames - qualityStart.droppedVideoFrames : null,
    },
    layers: 1,
    audioTracks: 0,
    errors: [],
    notes:
      'Fallback plays ONE video track with NO compositing and NO WebAudio mix. Its numbers ' +
      'are not comparable to the primary path and are reported separately on purpose.',
  };
}

// ─── the primary path: 4 decoders + WebGL2 composite + 6 WebAudio tracks ────────────
async function runCompositeScenario(opts) {
  const { seconds, warmupSec, compositeWidth, compositeHeight, scalingProbeRequested } = opts;
  const errors = [];
  const t0All = performance.now();

  const gl = glIdentity();
  const wc = webCodecsProbe();
  if (!wc.videoDecoder || !wc.encodedVideoChunk) {
    errors.push('WebCodecs absent at the composite entry point — caller should have branched');
  }
  log(`GL renderer: ${gl.renderer}  (isSoftware=${gl.isSoftware})`);

  // ── 0. control: how fast does rAF run with NO canvas, NO decoders, NO audio? ──────
  // Under headless SwiftShader the page's BeginFrame rate — not our GL work — can be the
  // ceiling. Without this control a low achieved fps would be misread as a compositor
  // cost, so it is measured first, on the bare page, and reported next to the result.
  const controlRaf = await new Promise((res) => {
    let n = 0;
    const t = performance.now();
    const tick = () => {
      n++;
      const dt = performance.now() - t;
      if (dt >= 1000) res({ ticks: n, hz: +(n / (dt / 1000)).toFixed(2) });
      else requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  log(`control rAF (bare page, no canvas): ${controlRaf.hz} Hz over ${controlRaf.ticks} ticks`);

  // ── 1. source bytes: fetched ONCE, shared by four independent Inputs ──────────────
  const tFetch = performance.now();
  const res = await fetch(PROXY_URL, { cache: 'no-store' });
  if (!res.ok) throw new Error(`proxy fetch failed: HTTP ${res.status}`);
  const bytes = await res.arrayBuffer();
  const fetchMs = performance.now() - tFetch;
  log(`proxy fetched: ${bytes.byteLength} bytes in ${fetchMs.toFixed(1)} ms`);

  // ── 2. four layers, four decoders ────────────────────────────────────────────────
  const probe = new mediabunny.Input({
    source: new mediabunny.BlobSource(new Blob([bytes])),
    formats: mediabunny.ALL_FORMATS,
  });
  const probeTrack = await probe.getPrimaryVideoTrack();
  const decoderConfig = await probeTrack.getDecoderConfig();
  const support = await VideoDecoder.isConfigSupported(decoderConfig);
  probe.dispose?.();

  const layers = [];
  const tOpen = performance.now();
  for (let i = 0; i < LAYERS; i++) {
    const layer = await new Layer(LAYER_SPECS[i], i, bytes, decoderConfig).open();
    layer.startDecoder();
    layers.push(layer);
  }
  const openMs = performance.now() - tOpen;
  log(`${LAYERS} layers opened in ${openMs.toFixed(1)} ms; codec=${decoderConfig.codec}`);

  // ── 3. compositor ────────────────────────────────────────────────────────────────
  const stage = document.getElementById('stage');
  stage.innerHTML = '';
  const canvas = document.createElement('canvas');
  canvas.style.maxWidth = '480px';
  canvas.style.display = 'block';
  stage.appendChild(canvas);
  const compositor = createCompositor(canvas, compositeWidth, compositeHeight);
  const { gl: g } = compositor;

  // ── 3b. control 2: rAF with the canvas attached but only `gl.clear()` per tick ─────
  // This is the number that separates "our compositor is slow" from "presenting ANY
  // 1280x720 WebGL canvas to this headless SwiftShader surface costs this much". Measured
  // with the four decoders configured but not yet fed (the feeder starts in step 5).
  const controlRafCanvas = await new Promise((res) => {
    let n = 0;
    const t = performance.now();
    const tick = () => {
      n++;
      g.clear(g.COLOR_BUFFER_BIT);
      const dt = performance.now() - t;
      if (dt >= 1000) res({ ticks: n, hz: +(n / (dt / 1000)).toFixed(2) });
      else requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  log(`control rAF (canvas attached, gl.clear only): ${controlRafCanvas.hz} Hz`);

  // ── 4. audio ─────────────────────────────────────────────────────────────────────
  const AudioCtx = window.AudioContext || window.webkitAudioContext;
  const ctx = new AudioCtx({ latencyHint: 'interactive' });
  await ctx.resume();
  const audio = await buildAudioGraph(ctx);
  log(`audio: ${audio.stems.length} stems, ctx.state=${ctx.state}, rate=${audio.sampleRate}`);

  const wallStartBeforeControls = performance.now();
  void wallStartBeforeControls;

  // ── 4b. control D: the FULL composite, decoders idle ─────────────────────────────
  // Isolates the GL half. Four static frames are held and re-composited every tick with
  // nothing decoding and nothing mixing, so whatever this costs is the compositor's own.
  for (const layer of layers) await layer.pump(0).catch(() => {});
  await new Promise((r) => setTimeout(r, 400));
  for (const layer of layers) await layer.pump(0).catch(() => {});
  const staticFrames = layers.map((l) => l.held ?? l.frames.values().next().value ?? null);
  const haveStatic = staticFrames.every(Boolean);
  const controlComposite = haveStatic
    ? await measureRafHz(2000, () => {
        compositor.upload(staticFrames);
        g.finish();
        compositor.draw();
        g.finish();
      })
    : null;
  log(`control rAF (full composite of 4 static frames, no decoders): ${controlComposite?.hz ?? 'n/a'} Hz`);

  // ── 4c. control C: the four decoders running FLAT OUT, no composite ───────────────
  // Isolates the decode half. The look-ahead is temporarily disabled so the decoder is
  // never told to wait — this is strictly harder on the CPU than the real scenario, which
  // caps itself at 12 frames ahead.
  for (const layer of layers) layer.restart();
  const savedLookAheadSec = POOL.lookAheadSec;
  POOL.lookAheadSec = 1e6;
  const ctlFeed = setInterval(() => {
    for (const layer of layers) layer.pump(0).catch(() => {});
  }, 4);
  const controlDecode = await measureRafHz(2000, () => g.clear(g.COLOR_BUFFER_BIT));
  clearInterval(ctlFeed);
  POOL.lookAheadSec = savedLookAheadSec;
  const controlDecodeChunks = layers.map((l) => l.submittedCount);
  log(
    `control rAF (4 decoders flat out, no composite): ${controlDecode.hz} Hz; ` +
      `chunks=${JSON.stringify(controlDecodeChunks)}`,
  );

  // Everything the controls did is discarded before the measured run starts.
  for (const layer of layers) layer.restart();
  for (const layer of layers) layer.pump(0).catch(() => {});
  await new Promise((r) => setTimeout(r, 600));

  // ── 5. the measured run ──────────────────────────────────────────────────────────
  const glUploadMs = [];
  const glDrawMs = [];
  const glCompositeMs = [];
  const glCompositeSyncedMs = [];
  const syncBuf = new Uint8Array(4);
  const deadlineLatenessMs = [];
  const rafDeltaMs = [];
  const decodedAheadSamples = [];
  let rafTicks = 0;
  let presented = 0;
  let onTime = 0;
  let stale = 0;
  let lastPresentedIndex = -1;
  let lastRaf = null;

  const audioRms = audio.stems.map(() => []);
  let audioDriftMs = null;
  /** Deadlines no rAF tick ever owned — a diagnostic for a stalled frame loop. */
  let droppedBetweenTicks = 0;

  const wallStart = performance.now();
  const audioStartCtxTime = ctx.currentTime;

  // The decoder feeder runs on its OWN timer, not on rAF. A pool fed only by the render
  // loop starves exactly when the render loop is the thing that is behind, and every
  // decode-latency figure then becomes a measurement of the render cadence instead of a
  // measurement of decoding. Real players feed from the decoder's drain; so does this.
  for (const layer of layers) layer.playheadFn = () => (performance.now() - wallStart) / 1000;
  const feedTimer = setInterval(() => {
    const p = (performance.now() - wallStart) / 1000;
    for (const layer of layers) layer.pump(p).catch((e) => errors.push(String(e)));
  }, 4);

  await new Promise((resolve) => {
    const tick = () => {
      const now = performance.now();
      const elapsedSec = (now - wallStart) / 1000;
      const measuring = elapsedSec >= warmupSec;
      rafTicks++;
      if (lastRaf !== null && measuring) rafDeltaMs.push(now - lastRaf);
      lastRaf = now;

      const playheadSec = elapsedSec; // the master clock: never the decoder's own timing
      const targetIndex = Math.floor(playheadSec * TARGET_FPS);

      // Keep every decoder fed, independently of whether we are going to present.
      for (const layer of layers) layer.pump(playheadSec).catch((e) => errors.push(String(e)));

      if (targetIndex > lastPresentedIndex) {
        const missed = targetIndex - lastPresentedIndex - 1;
        const frames = [];
        let got = 0;
        for (const layer of layers) {
          const f = layer.take(targetIndex);
          if (f) {
            frames.push(f);
            got++;
          } else {
            // Fall back to whatever is on screen for this layer: a repeat, not a freeze.
            frames.push(layer.held);
          }
        }
        const canPresent = got === LAYERS;
        // Nothing at all to show yet (the pool is still filling): present nothing rather
        // than compositing a half-populated stack.
        const hasSomethingOnScreen = frames.every((f) => f);
        if (!canPresent && measuring) droppedBetweenTicks += missed;

        if (hasSomethingOnScreen) {
          const tUp = performance.now();
          compositor.upload(frames);
          g.finish();
          const tDraw = performance.now();
          compositor.draw();
          g.finish();
          const tSync = performance.now();
          // gl.finish() is NOT a synchronisation primitive in WebGL — the work can still be
          // queued when it returns. A 1-pixel readPixels IS a real sync, because its result
          // cannot be produced before everything before it has executed.
          //
          // SAMPLED, never per frame: on this SwiftShader host a readback costs hundreds of
          // milliseconds (it forces a GPU-process round trip), and doing it every frame
          // starves the decoder feeder and the whole loop — measured, not assumed. One in
          // `SYNC_SAMPLE_EVERY` frames is enough to bound the real composite cost.
          let tEnd = tSync;
          if (presented % SYNC_SAMPLE_EVERY === 0) {
            syncBuf.fill(0);
            g.readPixels(0, 0, 1, 1, g.RGBA, g.UNSIGNED_BYTE, syncBuf);
            tEnd = performance.now();
            if (measuring) glCompositeSyncedMs.push(tEnd - tUp);
          }
          if (measuring) {
            glUploadMs.push(tDraw - tUp);
            glDrawMs.push(tSync - tDraw);
            glCompositeMs.push(tSync - tUp);
            deadlineLatenessMs.push(now - (wallStart + (targetIndex / TARGET_FPS) * 1000));
            decodedAheadSamples.push(layers.map((l) => l.decodedAhead(targetIndex)));
          }
          presented++;
          if (canPresent) onTime++;
          else stale++;
          for (let i = 0; i < LAYERS; i++) {
            if (frames[i] && frames[i] !== layers[i].held) {
              // Only a NEW frame replaces the held one, and `held`/`heldIndex` move
              // together — see the `held` comment on Layer.
              layers[i].held = frames[i];
              layers[i].heldIndex = targetIndex;
            }
          }
          lastPresentedIndex = targetIndex;
        }
      }

      if (measuring && rafTicks % 15 === 0) {
        for (let i = 0; i < audio.stems.length; i++) {
          audioRms[i].push(rmsOf(audio.stems[i].analyser, new Float32Array(1024)));
        }
        audioDriftMs = (ctx.currentTime - audioStartCtxTime) * 1000 - (now - wallStart);
      }

      if (elapsedSec >= warmupSec + seconds) resolve();
      else requestAnimationFrame(tick);
    };

    requestAnimationFrame(tick);
  });

  const wallMs = performance.now() - wallStart;
  const measuredSec = wallMs / 1000 - warmupSec;
  const targetTicks = Math.floor(measuredSec * TARGET_FPS);
  clearInterval(feedTimer);

  // ── 6. composite verification: still inside the same task as a draw ──────────────
  compositor.draw();
  g.finish();
  const compositeCheck = compositor.readback();

  // ── 6b. optional: how does the composite cost scale with canvas AREA? ─────────────
  // The composite turned out to be the binding constraint on this host, so the useful
  // thing to hand P3-F1 is not one number but the curve. Decoders are idle throughout
  // (the four held frames are re-composited), so this is purely the compositor's cost.
  let scalingProbe = null;
  if (scalingProbeRequested) {
    const held = layers.map((l) => l.held);
    scalingProbe = [];
    if (held.every(Boolean)) {
      for (const [w, h] of [
        [480, 270],
        [640, 360],
        [960, 540],
        [1280, 720],
        [1920, 1080],
      ]) {
        canvas.width = w;
        canvas.height = h;
        g.viewport(0, 0, w, h);
        const r = await measureRafHz(1200, () => {
          compositor.upload(held);
          g.finish();
          compositor.draw();
          g.finish();
        });
        const msPerFrame = r.elapsedMs / r.ticks;
        scalingProbe.push({
          width: w,
          height: h,
          megapixels: +((w * h) / 1e6).toFixed(3),
          rafHz: r.hz,
          msPerFrame: +msPerFrame.toFixed(2),
          msPerMegapixel: +((msPerFrame / ((w * h) / 1e6))).toFixed(2),
          meetsFps30: msPerFrame <= 1000 / TARGET_FPS,
        });
        log(`scaling ${w}x${h}: ${msPerFrame.toFixed(1)} ms/frame (${r.hz} Hz)`);
      }
    }
    canvas.width = compositeWidth;
    canvas.height = compositeHeight;
    g.viewport(0, 0, compositeWidth, compositeHeight);
  }

  // ── 7. teardown ──────────────────────────────────────────────────────────────────
  for (const s of audio.stems) {
    try {
      s.src.stop();
    } catch {
      /* already stopped */
    }
  }
  const audioStateAfter = ctx.state;
  await ctx.close().catch(() => null);
  for (const layer of layers) layer.dispose();

  const decodeLatencyByLayer = layers.map((l) => stats(l.decodeLatencyUs, 1));
  const allDecode = layers.flatMap((l) => l.decodeLatencyUs);
  const aheadByLayer = layers.map((l, i) =>
    stats(decodedAheadSamples.map((row) => row[i]), 2),
  );

  return {
    harness: 'T-201 compositor budget',
    path: 'webcodecs-4layer-gl2-composite',
    requiresWebCodecs: true,
    startedAt: new Date().toISOString(),
    durationMsTotal: +(performance.now() - t0All).toFixed(1),

    scenario: {
      videoLayers: LAYERS,
      audioTracks: audio.stems.length,
      sourceResolution: '1920x1080',
      sourceFps: TARGET_FPS,
      compositeWidth,
      compositeHeight,
      targetFps: TARGET_FPS,
      warmupSec,
      measuredWindowSec: +measuredSec.toFixed(3),
      sourceProxyBytes: bytes.byteLength,
      sourceDurationSec: SOURCE_DURATION_SEC,
    },

    pool: { ...POOL },

    capabilities: { gl, webCodecs: wc, timerResolutionUs: measureTimerResolutionUs(), decoderConfig: {
      codec: decoderConfig.codec ?? null,
      codedWidth: decoderConfig.codedWidth ?? null,
      codedHeight: decoderConfig.codedHeight ?? null,
      hardwareAcceleration: decoderConfig.hardwareAcceleration ?? null,
      isConfigSupported: Boolean(support?.supported),
    } },

    /**
     * rAF on the BARE page: the ceiling this host imposes before the scenario starts.
     * `achieved.rafHz` below can never exceed it, and any gap between the two is the
     * scenario's own cost.
     */
    controlRafHz: controlRaf.hz,
    controlRafTicks: controlRaf.ticks,
    controlRafWithCanvasHz: controlRafCanvas.hz,
    controlRafWithCanvasTicks: controlRafCanvas.ticks,
    /**
     * The two controls that DECOMPOSE the achieved frame rate. Read together they say
     * whether a budget miss is the decoders or the compositor — which is the difference
     * between "buy a GPU" and "use fewer layers".
     */
    controls: {
      bareRafHz: controlRaf.hz,
      canvasClearOnlyRafHz: controlRafCanvas.hz,
      fullCompositeNoDecodersRafHz: controlComposite?.hz ?? null,
      fourDecodersFlatOutNoCompositeRafHz: controlDecode.hz,
      fourDecodersFlatOutChunksSubmitted: controlDecodeChunks,
      note:
        'each control ran ~2 s on the same page, same browser, same launch flags; the ' +
        'decoders were restarted and every counter reset between a control and the run.',
    },

    // ── the four numbers, separate ────────────────────────────────────────────────
    achieved: {
      targetFps: TARGET_FPS,
      wallSec: +measuredSec.toFixed(3),
      targetDeadlines: targetTicks,
      onTimeFrames: onTime,
      achievedFps: +(onTime / measuredSec).toFixed(2),
      framesPresented: presented,
      displayedFps: +(presented / measuredSec).toFixed(2),
      droppedFrames: targetTicks - onTime,
      droppedFramesPct: +(((targetTicks - onTime) / targetTicks) * 100).toFixed(2),
      deadlinesSkippedBetweenRafTicks: droppedBetweenTicks,
      stalePresents: stale,
      rafTicks,
      rafHz: +(rafTicks / measuredSec).toFixed(2),
      rafIntervalMs: stats(rafDeltaMs, 3),
      deadlineLatenessMs: stats(deadlineLatenessMs, 3),
    },

    decodeLatency: {
      unit: 'µs',
      definition:
        'per decoded frame: performance.now() at the decoder output callback minus the ' +
        'moment its EncodedVideoChunk was handed to decode(). This INCLUDES the time the ' +
        'frame sat behind the pool\'s look-ahead in the decoder queue, so it is the "when ' +
        'is the frame ready to show" number, NOT the codec\'s own speed. The codec\'s speed ' +
        'is `throughput` below.',
      overall: stats(allDecode, 1),
      perLayer: decodeLatencyByLayer,
      throughput: {
        definition:
          'per layer, the interval between consecutive decoder output callbacks — the ' +
          'layer\'s own decode throughput, independent of queue depth.',
        unit: 'µs',
        intervalPerLayer: layers.map((l) => stats(l.outputIntervalUs, 1)),
        effectiveDecodeFpsPerLayer: layers.map((l) => {
          const s = l.outputIntervalUs;
          if (s.length === 0) return null;
          const mean = s.reduce((a, b) => a + b, 0) / s.length;
          return +(1e6 / mean).toFixed(2);
        }),
      },
      decodedFrames: layers.map((l) => l.decodedCount),
      submittedChunks: layers.map((l) => l.submittedCount),
      evictedUndisplayed: layers.map((l) => l.droppedDecodedFrames),
      bufferedAheadFrames: { overall: stats(decodedAheadSamples.flat(), 2), perLayer: aheadByLayer },
    },

    glComposite: {
      unit: 'ms',
      definition:
        'per presented frame. upload = 4 × texImage2D(VideoFrame); draw = clear + 4 blended ' +
        'full-frame quads + 4 marker quads. `totalMs` ends at gl.finish(), which is NOT a ' +
        'WebGL synchronisation primitive; `totalSyncedMs` additionally does a 1-pixel ' +
        'readPixels, whose result cannot be produced before the GL work has executed. ' +
        'quote `totalSyncedMs`. Software GL: these are CPU-side costs.',
      uploadMs: stats(glUploadMs, 3),
      drawMs: stats(glDrawMs, 3),
      totalMs: stats(glCompositeMs, 3),
      totalSyncedMs: stats(glCompositeSyncedMs, 3),
      readPixelsSyncMs: stats(
        glCompositeSyncedMs.map((v, i) => v - (glCompositeMs[i] ?? 0)),
        3,
      ),
      frameBudgetMs: +(1000 / TARGET_FPS).toFixed(3),
    },

    audio: {
      contextStateDuringRun: 'running',
      contextStateAfterClose: audioStateAfter,
      sampleRate: audio.sampleRate,
      loadMs: +audio.loadMs.toFixed(1),
      buffers: audio.buffers,
      /** Six stems each measured by their OWN analyser: silence here means a dead track. */
      perStemRms: audioRms.map((xs) => stats(xs, 5)),
      driftMs: audioDriftMs === null ? null : +audioDriftMs.toFixed(2),
      driftDefinition:
        'audioContext.currentTime advance minus wall-clock advance at the end of the ' +
        'window. Audio is on its own thread; a render loop that falls behind must not ' +
        'move this number.',
    },

    compositeCheck: {
      readbackTiming: 'same task as a draw, immediately after gl.finish()',
      markers: compositeCheck.markers,
      centerPixelRgb: compositeCheck.center,
      layersVerified: compositeCheck.markers.filter(
        (m) =>
          Math.abs(m.gotRgb[0] - m.expectedRgb[0]) <= 8 &&
          Math.abs(m.gotRgb[1] - m.expectedRgb[1]) <= 8 &&
          Math.abs(m.gotRgb[2] - m.expectedRgb[2]) <= 8,
      ).length,
    },

    compositeScaling: scalingProbe,

    fetchMs: +fetchMs.toFixed(2),
    openMs: +openMs.toFixed(2),
    errors: [...errors, ...layers.flatMap((l) => l.errors)],
  };
}

// ─── WebCodecs kill switch (the fallback experiment's control) ──────────────────────
window.__spikeSetPool = function setPool(overrides = {}) {
  Object.assign(POOL, overrides);
  if (typeof POOL.lookAheadFrames === 'number') {
    POOL.lookAheadSec = POOL.lookAheadFrames / TARGET_FPS;
  }

  // The invariant is re-asserted after EVERY override, because `lookAheadFrames` is exactly the knob
  // that broke it. A caller may suspend it only by saying so explicitly; an override that broke the
  // pool by accident throws here instead of quietly measuring a pool that cannot present a frame.
  const satisfied = POOL.maxDecodedBufferedFrames >= POOL.lookAheadFrames + 2;

  if (overrides.allowUnsafeLookAhead === true) {
    POOL.allowUnsafeLookAhead = true;
    log(
      `pool set by an explicit unsafe override — invariant ${satisfied ? 'still holds' : 'SUSPENDED'}: ` +
        JSON.stringify(POOL),
    );
  } else {
    delete POOL.allowUnsafeLookAhead;
    assertPoolInvariant(POOL);
    log(`pool set: ${JSON.stringify(POOL)}`);
  }

  // The RESULT states whether the pool in force actually satisfies the invariant, so a report can
  // never describe a suspended run as a normal one — the flag is derived, not repeated from intent.
  return { ...POOL, poolInvariantSatisfied: satisfied };
};

window.__spikeDisableWebCodecs = function disableWebCodecs() {
  const names = ['VideoDecoder', 'VideoEncoder', 'EncodedVideoChunk', 'VideoFrame'];
  const results = {};
  for (const n of names) {
    try {
      Object.defineProperty(window, n, { value: undefined, configurable: true, writable: true });
      results[n] = typeof window[n] === 'undefined' ? 'removed' : 'still defined';
    } catch (e) {
      results[n] = `failed: ${e.message}`;
    }
  }
  log(`WebCodecs kill switch: ${JSON.stringify(results)}`);
  return results;
};

// ─── entry point: pick the path, report which one ran ───────────────────────────────
window.runScenario = async function runScenario(opts = {}) {
  const cfg = {
    seconds: opts.seconds ?? 30,
    warmupSec: opts.warmupSec ?? 4,
    compositeWidth: opts.compositeWidth ?? 1280,
    compositeHeight: opts.compositeHeight ?? 720,
    scalingProbeRequested: opts.scalingProbeRequested ?? false,
  };
  log(`runScenario ${JSON.stringify(cfg)}`);
  const wc = webCodecsProbe();
  if (!wc.videoDecoder || !wc.encodedVideoChunk) {
    const out = await runFallbackVideoScenario(cfg);
    out.requestedConfig = cfg;
    out.webCodecsProbe = wc;
    return out;
  }
  const out = await runCompositeScenario(cfg);
  out.requestedConfig = cfg;
  out.webCodecsProbe = wc;
  return out;
};

window.__playbackReady = true;
log('playback harness ready — call window.runScenario()');
