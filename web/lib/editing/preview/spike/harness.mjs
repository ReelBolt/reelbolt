/**
 * T-200 — the measurement harness.  REMOVABLE (see README.md).
 *
 * Runs in a real Chromium page (served by `serve.mjs`, driven by `run-spike.mjs`) and
 * measures BOTH demuxers against the SAME committed fixture:
 *   fixtures/proxy-1080p-12s.mp4 — 1080p H.264 + AAC, 12 s, one IDR per second.
 *
 * Contract: `window.runSpike()` returns a plain, JSON-serialisable report. The driver
 * never computes a metric itself — every number below is taken here, in the browser,
 * with `performance.now()` around the real call.
 *
 * ── How each demuxer is driven, and why ────────────────────────────────────────────
 *
 * Both receive the same bytes, and neither is allowed a network round trip inside a timing
 * region: the fixture is fetched once up front and every seek is served out of that
 * in-memory ArrayBuffer. The subject is the demuxer, not the network.
 *
 * mediabunny (`Input` + `EncodedPacketSink`) owns its own source. `getKeyPacket(t)` plans
 * the seek and hands back the encoded bytes in one call.
 *
 * mp4box has no source abstraction: it is a streaming parser you push buffers into. Its
 * seek is genuinely two steps, and the harness performs both:
 *   1. `file.seek(t, true)` — the planner.
 *   2. push the target sample's `[offset, offset+size)` back in, then `getTrackSample()`.
 * Step 2 pushes the smallest read a player could make, because the sample table already
 * states the extent — no arbitrary read-ahead constant penalises mp4box.
 *
 * ⚠ TWO mp4box TRAPS FOUND THE HARD WAY, BOTH LOAD-BEARING FOR T-201
 * ─────────────────────────────────────────────────────────────────
 * 1. `createFile()` defaults to `keepMdatData = false`, i.e. `discardMdatData = true`.
 *    Every `getTrackSample()` then returns `undefined` and `setExtractionOptions()` is dead
 *    — the parser marks the mdat's bytes used and the stream drops the buffers. The library
 *    only says so in a `Log.warn` that is off by default. **You must call
 *    `createFile(true)`.**
 * 2. `seek()`'s returned `offset` is NOT the keyframe's file offset. `ISOFile.seek` ends
 *    with `seek_info.offset = this.stream.getEndFilePositionAfter(seek_info.offset)` —
 *    a BUFFER-ADJUSTED "where to resume reading from" hint. Once any byte range has been
 *    appended, that returns the end of a buffered range instead of the RAP's offset. The
 *    RAP's real offset must come from the sample table (`getTrackSamplesInfo` → the
 *    `is_sync` sample with the greatest `cts <= t`). The harness does exactly that, and
 *    this is why the loop below never trusts `plan.offset`.
 *
 * ── The one shot of WebCodecs ──────────────────────────────────────────────────────
 *
 * `openToFirstDecodedFrameMs` needs a decoder, and the ticket's scope note says not to use
 * WebCodecs `VideoDecoder`. Acceptance criterion 3 asks for the number anyway. Resolution:
 * ONE throwaway decoder, one frame, per demuxer, purely to close the interval — no pool, no
 * policy, no decode comparison, and no decoded frame is ever rendered. It is reported
 * separately from `openToFirstSampleMs` (the pure-demux number, the in-scope one for this
 * ticket) and flagged as a one-shot probe. Decode as a subject stays T-201's.
 */

import * as mediabunny from '/node_modules/mediabunny/dist/modules/src/index.js';
// NOTE: `mp4box.all.mjs` has NO default export — `import MP4Box from '...'` is a link-time
// SyntaxError ("does not provide an export named 'default'"). A namespace import is the
// only correct form, and it is what the ticket's snippet got wrong.
import * as mp4box from '/node_modules/mp4box/dist/mp4box.all.mjs';

const FIXTURE_URL = './fixtures/proxy-1080p-12s.mp4';
/** Committed property of the fixture (`make-fixture.sh`), not a number either library reported. */
const FIXTURE_DURATION_SEC = 12;

/** Distinct timestamps per demuxer for the random-seek median. Acceptance asks for >= 20. */
const SEEK_COUNT = 24;
/** Extra passes over those same timestamps, for a warm-cache figure and a batch mean. */
const SEEK_ROUNDS = 5;
/** Repeats behind the two "open to …" latencies, which include a real `fetch`. */
const OPEN_REPEATS = 5;
/** Repeats behind the first-keyframe number (time resolution needs it; see `timerUs`). */
const FIRST_KEY_REPEATS = 20;
/** Seeks run before anything is measured, so first-touch costs stay out of the median. */
const WARMUP_SEEKS = 3;

const log = (msg, isErr = false) => {
  const el = document.getElementById('log');
  if (!el) return;
  const line = document.createElement('div');
  if (isErr) line.className = 'err';
  line.textContent = msg;
  el.appendChild(line);
};

// ─────────────────────────────────────────────────────────────────────────────────
// Statistics.  Chromium clamps `performance.now()` hard enough that a single fast seek
// reads as 0 ms, so every latency below is reported in MICROSECONDS and the measured
// clock resolution is reported alongside, so a reader can tell a real sub-resolution
// result from a fabricated one.
// ─────────────────────────────────────────────────────────────────────────────────
const sorted = (xs) => [...xs].sort((a, b) => a - b);
const us = (ms) => +(ms * 1000).toFixed(2);

function quantile(s, q) {
  if (s.length === 0) return null;
  const pos = (s.length - 1) * q;
  const lo = Math.floor(pos);
  const hi = Math.ceil(pos);
  return lo === hi ? s[lo] : s[lo] + (s[hi] - s[lo]) * (pos - lo);
}

/** Median of an even-length list is the mean of the two central values. */
function median(xs) {
  if (xs.length === 0) return null;
  const s = sorted(xs);
  const mid = s.length >> 1;
  return s.length % 2 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
}

/** Milliseconds in, microseconds out. */
function summariseMs(xs) {
  if (xs.length === 0) return { n: 0 };
  const s = sorted(xs);
  return {
    n: s.length,
    minUs: us(s[0]),
    medianUs: us(median(s)),
    p95Us: us(quantile(s, 0.95)),
    maxUs: us(s[s.length - 1]),
  };
}

/** Total wall time divided by the number of operations — immune to clock quantisation. */
function meanUs(totalMs, count) {
  return count === 0 ? null : us(totalMs / count);
}

/**
 * The clock's actual resolution: repeatedly sample `performance.now()` in a tight loop and
 * take the smallest non-zero delta. Chromium coarsens this to 100 µs by default, which is
 * why raw per-seek medians of "0" are meaningless without this number beside them.
 */
function measureTimerResolutionUs() {
  const seen = new Set();
  let prev = performance.now();
  for (let i = 0; i < 200_000; i++) {
    const now = performance.now();
    if (now !== prev) {
      seen.add(+(now - prev).toFixed(6));
      prev = now;
      if (seen.size > 40) break;
    }
  }
  const deltas = [...seen].filter((d) => d > 0);
  return deltas.length ? us(Math.min(...deltas)) : null;
}

// ─────────────────────────────────────────────────────────────────────────────────
// Renderer / browser identity — required by acceptance 3, and the thing that decides
// whether any GL-derived number means anything on this host.
// ─────────────────────────────────────────────────────────────────────────────────
function glIdentity() {
  const canvas = document.createElement('canvas');
  const gl2 = canvas.getContext('webgl2');
  const gl = gl2 ?? canvas.getContext('webgl');
  if (!gl) {
    return {
      available: false,
      reason:
        'no WebGL context — Chromium\'s GPU process failed to initialise any EGL display ' +
        '(ANGLE falls back to DisplayVkXcb, xcb_connect() fails with no X server, SwANGLE ' +
        'returns EGL_NOT_INITIALIZED). Launching with --disable-gpu-sandbox is what fixes it.',
    };
  }
  const dbg = gl.getExtension('WEBGL_debug_renderer_info');
  const out = {
    available: true,
    contextType: gl2 ? 'webgl2' : 'webgl',
    debugRendererInfoExtension: Boolean(dbg),
    // The unmasked strings are the authoritative ones; the masked pair is what a page sees
    // without the debug extension, kept so the report can show both.
    renderer: dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER),
    vendor: dbg ? gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL) : gl.getParameter(gl.VENDOR),
    maskedRenderer: gl.getParameter(gl.RENDERER),
    maskedVendor: gl.getParameter(gl.VENDOR),
    version: gl.getParameter(gl.VERSION),
    maxTextureSize: gl.getParameter(gl.MAX_TEXTURE_SIZE),
    isSoftware: /swiftshader|llvmpipe|software|subzero/i.test(
      String(dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER)),
    ),
  };
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return out;
}

// ─────────────────────────────────────────────────────────────────────────────────
// Fixture fetch — timed, because "file open" is part of two of the metrics.
// ─────────────────────────────────────────────────────────────────────────────────
async function fetchFixture() {
  const t0 = performance.now();
  const res = await fetch(FIXTURE_URL, { cache: 'no-store' });
  if (!res.ok) throw new Error(`fixture fetch failed: HTTP ${res.status}`);
  const bytes = await res.arrayBuffer();
  return { bytes, fetchMs: performance.now() - t0 };
}

/** 24 distinct, deterministic timestamps spread strictly inside the fixture. */
function seekTargets(durationSec) {
  const out = [];
  for (let i = 0; i < SEEK_COUNT; i++) {
    // (i + 0.5)/SEEK_COUNT of the duration: never 0, never the very end, all distinct.
    out.push(+(((i + 0.5) / SEEK_COUNT) * durationSec).toFixed(4));
  }
  return out;
}

// ═════════════════════════════════════════════════════════════════════════════════
// mediabunny
// ═════════════════════════════════════════════════════════════════════════════════
async function mediabunnyOpen(bytes, { withDuration = true } = {}) {
  const t0 = performance.now();
  const input = new mediabunny.Input({
    source: new mediabunny.BlobSource(new Blob([bytes])),
    formats: mediabunny.ALL_FORMATS,
  });
  const format = await input.getFormat();
  const videoTrack = await input.getPrimaryVideoTrack();
  const audioTrack = await input.getPrimaryAudioTrack();
  // Split deliberately: `computeDuration` is a full-table walk in mediabunny while mp4box
  // gets its duration free out of the moov header, so folding them together would compare
  // unlike work. Both halves are reported, and the "fast" open paths below skip it — which
  // is what a player chasing first frame would actually do.
  const headerMs = performance.now() - t0;

  let duration = null;
  let durationMs = 0;
  if (withDuration) {
    const tDur = performance.now();
    duration = await input.computeDuration();
    durationMs = performance.now() - tDur;
  }

  return {
    input,
    format,
    videoTrack,
    audioTrack,
    duration,
    headerMs,
    durationMs,
    openMs: headerMs + durationMs,
  };
}

async function runMediabunny(bytes, targets) {
  const opened = await mediabunnyOpen(bytes);
  const { input, format, videoTrack, audioTrack, duration } = opened;
  if (!videoTrack) throw new Error('mediabunny: no video track in fixture');

  const decoderConfig = await videoTrack.getDecoderConfig();

  // ── sample table: the library's own full-table walk, which also yields the count ──
  const tTable = performance.now();
  const stats = await videoTrack.computePacketStats();
  const sampleTableMs = performance.now() - tTable;

  // ── packets/samples enumerated: a real traversal, not an index lookup ──────────
  // Byte totals are accumulated as well as counts, because a count alone cannot tell a real
  // traversal from a stubbed one — the bytes can, and they are the counterpart of mp4box's
  // `enumeratedSampleBytes`.
  const tEnumerate = performance.now();
  let packetsParsed = 0;
  let enumeratedBytes = 0;
  const perTrack = {};
  for (const [name, track] of [
    ['video', videoTrack],
    ['audio', audioTrack],
  ]) {
    if (!track) continue;
    let n = 0;
    for await (const packet of new mediabunny.EncodedPacketSink(track).packets()) {
      n++;
      enumeratedBytes += packet.byteLength;
    }
    perTrack[name] = n;
    packetsParsed += n;
  }
  const enumerateMs = performance.now() - tEnumerate;

  const sink = new mediabunny.EncodedPacketSink(videoTrack);

  // ── first-keyframe seek ───────────────────────────────────────────────────────
  const firstKeyTimes = [];
  let firstKeyPacket = null;
  for (let i = 0; i < FIRST_KEY_REPEATS; i++) {
    const t = performance.now();
    const pkt = await sink.getFirstKeyPacket();
    firstKeyTimes.push(performance.now() - t);
    if (!firstKeyPacket) firstKeyPacket = pkt;
  }

  // ── random seeks ──────────────────────────────────────────────────────────────
  for (let i = 0; i < WARMUP_SEEKS; i++) await sink.getKeyPacket(targets[i]);

  const perSeek = [];
  const detail = [];
  let integrityFailures = 0;
  const tAll = performance.now();
  for (let round = 0; round < SEEK_ROUNDS; round++) {
    for (const t of targets) {
      const tSeek = performance.now();
      const pkt = await sink.getKeyPacket(t);
      const ms = performance.now() - tSeek;
      perSeek.push({ round, targetSec: t, ms });
      const ok = Boolean(pkt) && pkt.type === 'key' && pkt.data.byteLength > 0 && pkt.timestamp <= t + 1e-6;
      if (!ok) integrityFailures++;
      if (round === 0) {
        detail.push({
          targetSec: t,
          keyPacketStartSec: pkt ? +pkt.timestamp.toFixed(4) : null,
          byteLength: pkt ? pkt.byteLength : 0,
          integrityOk: ok,
        });
      }
    }
  }
  const allMs = performance.now() - tAll;

  const firstTouch = perSeek.filter((s) => s.round === 0).map((s) => s.ms);
  const repeats = perSeek.filter((s) => s.round > 0).map((s) => s.ms);

  const out = {
    headerParseMs: +opened.headerMs.toFixed(3),
    durationComputeMs: +opened.durationMs.toFixed(3),
    parseMs: +opened.openMs.toFixed(3),
    sampleTableMs: +sampleTableMs.toFixed(3),
    computedPacketCount: stats.packetCount,
    computedAveragePacketRate: stats.averagePacketRate,
    computedAverageBitrate: stats.averageBitrate,
    formatName: format.name,
    formatMimeType: format.mimeType,
    durationSec: duration,
    packetsParsed,
    packetsPerTrack: perTrack,
    enumerateMs: +enumerateMs.toFixed(3),
    enumeratedSampleBytes: enumeratedBytes,
    firstKeyframeSeek: summariseMs(firstKeyTimes),
    firstKeyframe: firstKeyPacket
      ? { startSec: +firstKeyPacket.timestamp.toFixed(4), byteLength: firstKeyPacket.byteLength }
      : null,
    randomSeek: {
      distinctTimestamps: targets.length,
      rounds: SEEK_ROUNDS,
      samples: perSeek.length,
      // The acceptance number: median over the 24 DISTINCT timestamps, first touch each.
      firstTouch: summariseMs(firstTouch),
      // Repeat visits to the same timestamps — the scrubbing-back-and-forth case.
      repeat: summariseMs(repeats),
      all: summariseMs(perSeek.map((s) => s.ms)),
      batchMeanUs: meanUs(allMs, perSeek.length),
      batchTotalMs: +allMs.toFixed(3),
      integrityFailures,
    },
    seekDetail: detail,
    decoderConfig: {
      codec: decoderConfig?.codec ?? null,
      codedWidth: decoderConfig?.codedWidth ?? null,
      codedHeight: decoderConfig?.codedHeight ?? null,
      hasDescription: Boolean(decoderConfig?.description),
      descriptionBytes: decoderConfig?.description?.byteLength ?? 0,
    },
  };

  input.dispose?.();
  return out;
}

/** Open (real fetch) → first keyframe's encoded sample bytes available. No decoder. */
async function mediabunnyOpenToFirstSample(withDuration) {
  const { bytes, fetchMs } = await fetchFixture();
  const t0 = performance.now();
  const opened = await mediabunnyOpen(bytes, { withDuration });
  const pkt = await new mediabunny.EncodedPacketSink(opened.videoTrack).getFirstKeyPacket();
  const ms = performance.now() - t0;
  opened.input.dispose?.();
  return { ms, fetchMs, byteLength: pkt ? pkt.byteLength : 0 };
}

/** Open (real fetch) → first DECODED frame. One throwaway decoder, one frame. */
async function mediabunnyOpenToFirstDecodedFrame(withDuration) {
  const { bytes, fetchMs } = await fetchFixture();
  const t0 = performance.now();
  const opened = await mediabunnyOpen(bytes, { withDuration });
  const config = await opened.videoTrack.getDecoderConfig();
  const pkt = await new mediabunny.EncodedPacketSink(opened.videoTrack).getFirstKeyPacket();
  const frame = await decodeOneFrame(pkt.data, config);
  const ms = performance.now() - t0;
  opened.input.dispose?.();
  return { ms, fetchMs, frame: { codedWidth: frame.codedWidth, codedHeight: frame.codedHeight } };
}

// ═════════════════════════════════════════════════════════════════════════════════
// mp4box
// ═════════════════════════════════════════════════════════════════════════════════
function mp4boxCreate() {
  // Trap 1: `createFile()` alone (keepMdatData = false) silently discards mdat data and
  // makes every getTrackSample() return undefined. See the file header.
  const file = mp4box.createFile(true);
  file.onError = (e) => {
    // mp4box reports parse errors through a callback, not a throw. Re-surface it on the
    // file object so `mp4boxOpen` can reject instead of hanging on a promise that will
    // never settle.
    file.__error = e instanceof Error ? e : new Error(String(e));
  };
  return file;
}

function mp4boxAppend(file, bytes, fileStart) {
  file.appendBuffer(mp4box.MP4BoxBuffer.fromArrayBuffer(bytes, fileStart));
}

async function mp4boxOpen(bytes) {
  const t0 = performance.now();
  const file = mp4boxCreate();
  const info = await new Promise((resolve, reject) => {
    file.onReady = resolve;
    try {
      mp4boxAppend(file, bytes, 0);
    } catch (e) {
      reject(e);
      return;
    }
    if (file.__error) reject(file.__error);
  });
  const videoTrack = info.tracks.find((t) => t.type === 'video');
  const audioTrack = info.tracks.find((t) => t.type === 'audio');
  // Header parse stops here — `getInfo()` is derived from the moov box, so this is the
  // like-for-like counterpart of mediabunny's Input + getFormat + getPrimaryVideoTrack.
  const headerMs = performance.now() - t0;

  // Sample table (stbl) realisation is the counterpart of mediabunny's
  // `computePacketStats`, and is timed separately for the same reason.
  const tTable = performance.now();
  const videoSamples = videoTrack ? file.getTrackSamplesInfo(videoTrack.id) : [];
  const audioSamples = audioTrack ? file.getTrackSamplesInfo(audioTrack.id) : [];
  const sampleTableMs = performance.now() - tTable;

  return {
    file,
    info,
    videoTrack,
    audioTrack,
    videoSamples,
    audioSamples,
    durationSec: info.duration / info.timescale,
    headerMs,
    sampleTableMs,
  };
}

/**
 * The RAP at or before `sec`, straight out of the sample table. Deliberately NOT taken
 * from `ISOFIle.seek()`'s return value — trap 2 in the file header.
 */
function rapAtOrBefore(videoSamples, timescale, sec) {
  let best = null;
  let bestT = -Infinity;
  for (const s of videoSamples) {
    if (!s.is_sync) continue;
    const t = s.cts / timescale;
    if (t <= sec + 1e-9 && t > bestT) {
      bestT = t;
      best = s;
    }
  }
  return best ?? videoSamples.find((s) => s.is_sync) ?? null;
}

async function runMp4box(bytes, targets) {
  const opened = await mp4boxOpen(bytes);
  const { file, info, videoTrack, videoSamples, audioSamples } = opened;
  if (!videoTrack) throw new Error('mp4box: no video track in fixture');
  const ts = videoTrack.timescale;

  const packetsParsed = videoSamples.length + audioSamples.length;
  const tEnumerate = performance.now();
  // Summing the sample table: mp4box has already materialised every sample descriptor by
  // this point, so this is NOT the same operation as mediabunny's packet iteration. It is
  // reported as informational only and is not a decision axis.
  let walkedBytes = 0;
  for (const s of videoSamples) walkedBytes += s.size;
  for (const s of audioSamples) walkedBytes += s.size;
  const enumerateMs = performance.now() - tEnumerate;

  /** Plan a seek and read the target sample's bytes. Returns timing plus integrity. */
  const seekOnce = (t) => {
    const tPlan = performance.now();
    // The planner runs for real; its returned offset is a buffer-adjusted resume hint and
    // is recorded but never used to locate the sample.
    const plan = file.seek(t, true);
    const planMs = performance.now() - tPlan;

    const hit = rapAtOrBefore(videoSamples, ts, t);
    mp4boxAppend(file, bytes.slice(hit.offset, hit.offset + hit.size), hit.offset);
    const sample = file.getTrackSample(videoTrack.id, hit.number);

    const ok =
      Boolean(sample) &&
      hit.is_sync === true &&
      sample.data?.byteLength === hit.size &&
      hit.cts / ts <= t + 1e-9;
    return { plan, planMs, hit, sample, ok };
  };

  // ── first-keyframe seek ───────────────────────────────────────────────────────
  const firstKeyTimes = [];
  let firstKeySample = null;
  for (let i = 0; i < FIRST_KEY_REPEATS; i++) {
    const t = performance.now();
    const { hit, sample } = seekOnce(0);
    firstKeyTimes.push(performance.now() - t);
    if (!firstKeySample) firstKeySample = { hit, sample };
  }

  // ── random seeks ──────────────────────────────────────────────────────────────
  for (let i = 0; i < WARMUP_SEEKS; i++) seekOnce(targets[i]);

  const perSeek = [];
  const detail = [];
  const planOnly = [];
  let integrityFailures = 0;
  const tAll = performance.now();
  for (let round = 0; round < SEEK_ROUNDS; round++) {
    for (const t of targets) {
      const tSeek = performance.now();
      const { plan, planMs, hit, sample, ok } = seekOnce(t);
      perSeek.push({ round, targetSec: t, ms: performance.now() - tSeek });
      planOnly.push(planMs);
      if (!ok) integrityFailures++;
      if (round === 0) {
        detail.push({
          targetSec: t,
          plannedOffset: plan.offset,
          keyPacketStartSec: +(hit.cts / ts).toFixed(4),
          byteLength: sample?.data?.byteLength ?? 0,
          integrityOk: ok,
        });
      }
    }
  }
  const allMs = performance.now() - tAll;

  const firstTouch = perSeek.filter((s) => s.round === 0).map((s) => s.ms);
  const repeats = perSeek.filter((s) => s.round > 0).map((s) => s.ms);

  // avcC payload (the AVCDecoderConfigurationRecord) for the WebCodecs description: the
  // box's own 8-byte header is stripped. Needed only by the decode probe.
  const avcC = videoSamples[0]?.description?.avcC ?? null;

  return {
    headerParseMs: +opened.headerMs.toFixed(3),
    // Duration is free from the moov header, so the counterpart metric is 0 by construction.
    durationComputeMs: 0,
    parseMs: +(opened.headerMs + opened.sampleTableMs).toFixed(3),
    sampleTableMs: +opened.sampleTableMs.toFixed(3),
    formatName: info.isFragmented ? 'fragmented MP4' : 'MP4 (ISO base media)',
    durationSec: info.duration / info.timescale,
    trackTimescale: ts,
    packetsParsed,
    packetsPerTrack: { video: videoSamples.length, audio: audioSamples.length },
    enumerateMs: +enumerateMs.toFixed(3),
    enumeratedSampleBytes: walkedBytes,
    firstKeyframeSeek: summariseMs(firstKeyTimes),
    firstKeyframe: firstKeySample
      ? {
          startSec: +(firstKeySample.hit.cts / ts).toFixed(4),
          byteLength: firstKeySample.sample?.data?.byteLength ?? 0,
        }
      : null,
    randomSeek: {
      distinctTimestamps: targets.length,
      rounds: SEEK_ROUNDS,
      samples: perSeek.length,
      firstTouch: summariseMs(firstTouch),
      repeat: summariseMs(repeats),
      all: summariseMs(perSeek.map((s) => s.ms)),
      batchMeanUs: meanUs(allMs, perSeek.length),
      batchTotalMs: +allMs.toFixed(3),
      // How much of the seek is mp4box's own planner (`file.seek`) versus the read the
      // caller has to perform. Reported so the comparison is not ambiguous. Note it grows
      // as `stream.buffers` accumulates appended ranges — measured, not assumed.
      planOnly: summariseMs(planOnly),
      integrityFailures,
    },
    seekDetail: detail,
    decoderConfig: avcC
      ? {
          codec: videoTrack.codec,
          codedWidth: videoTrack.video.width,
          codedHeight: videoTrack.video.height,
          hasDescription: avcC.size - 8 > 0,
          descriptionBytes: avcC.size - 8,
        }
      : null,
    keptMdatData: file.discardMdatData === false,
  };
}

async function mp4boxOpenToFirstSample(withDuration) {
  // `withDuration` is a no-op for mp4box: its duration comes free out of the moov header,
  // so there is nothing to skip. The parameter exists only to keep the two drivers
  // structurally symmetric.
  void withDuration;
  const { bytes, fetchMs } = await fetchFixture();
  const t0 = performance.now();
  const opened = await mp4boxOpen(bytes);
  const firstKey = rapAtOrBefore(opened.videoSamples, opened.videoTrack.timescale, 0);
  mp4boxAppend(
    opened.file,
    bytes.slice(firstKey.offset, firstKey.offset + firstKey.size),
    firstKey.offset,
  );
  const sample = opened.file.getTrackSample(opened.videoTrack.id, firstKey.number);
  const ms = performance.now() - t0;
  return { ms, fetchMs, byteLength: sample?.data?.byteLength ?? 0 };
}

async function mp4boxOpenToFirstDecodedFrame(withDuration) {
  void withDuration; // no-op; see mp4boxOpenToFirstSample
  const { bytes, fetchMs } = await fetchFixture();
  const t0 = performance.now();
  const opened = await mp4boxOpen(bytes);
  const firstKey = rapAtOrBefore(opened.videoSamples, opened.videoTrack.timescale, 0);
  mp4boxAppend(
    opened.file,
    bytes.slice(firstKey.offset, firstKey.offset + firstKey.size),
    firstKey.offset,
  );
  const sample = opened.file.getTrackSample(opened.videoTrack.id, firstKey.number);
  const avcC = sample.description.avcC;
  const config = {
    codec: opened.videoTrack.codec,
    codedWidth: opened.videoTrack.video.width,
    codedHeight: opened.videoTrack.video.height,
    description: new Uint8Array(bytes, avcC.start + 8, avcC.size - 8),
  };
  const frame = await decodeOneFrame(sample.data, config);
  const ms = performance.now() - t0;
  return { ms, fetchMs, frame: { codedWidth: frame.codedWidth, codedHeight: frame.codedHeight } };
}

// ═════════════════════════════════════════════════════════════════════════════════
// The single-frame decode probe (see the file header for why this exists at all).
// ═════════════════════════════════════════════════════════════════════════════════
async function decodeOneFrame(data, config) {
  if (typeof VideoDecoder === 'undefined') {
    throw new Error('WebCodecs VideoDecoder unavailable in this browser');
  }
  const support = await VideoDecoder.isConfigSupported(config);
  if (!support.supported) {
    throw new Error(`WebCodecs cannot decode ${config.codec}`);
  }
  return await new Promise((resolve, reject) => {
    const decoder = new VideoDecoder({
      output: (frame) => {
        try {
          decoder.close();
        } catch {
          /* already closed */
        }
        resolve(frame);
      },
      error: (e) => reject(new Error(`VideoDecoder error: ${e.message}`)),
    });
    try {
      decoder.configure(config);
      decoder.decode(
        new EncodedVideoChunk({
          type: 'key',
          timestamp: 0,
          duration: 0,
          data: data instanceof Uint8Array ? data : new Uint8Array(data),
        }),
      );
      decoder.flush().catch(reject);
    } catch (e) {
      reject(e);
    }
  });
}

// ═════════════════════════════════════════════════════════════════════════════════
// Entry point
// ═════════════════════════════════════════════════════════════════════════════════

/**
 * Runs the whole comparison and returns a JSON-serialisable report.
 * @returns {Promise<object>}
 */
async function runSpike() {
  const report = {
    harness: 'T-200 demuxer spike',
    startedAt: new Date().toISOString(),
    fixture: {
      url: FIXTURE_URL,
      bytes: null,
      sha256Note: 'hashed by the driver from the committed file, not in the browser',
    },
    browser: {
      userAgent: navigator.userAgent,
      hardwareConcurrency: navigator.hardwareConcurrency,
      deviceMemoryGb: navigator.deviceMemory ?? null,
      chromeVersion: (navigator.userAgent.match(/Chrome\/([\d.]+)/) ?? [])[1] ?? null,
      webCodecsVideoDecoder: typeof VideoDecoder !== 'undefined',
      timerResolutionUs: measureTimerResolutionUs(),
    },
    gl: glIdentity(),
    demuxers: {},
    errors: [],
  };
  log('browser: ' + report.browser.userAgent);
  log(`performance.now() resolution: ${report.browser.timerResolutionUs} µs`);
  log('gl renderer: ' + (report.gl.renderer ?? report.gl.reason));

  const { bytes, fetchMs } = await fetchFixture();
  report.fixture.bytes = bytes.byteLength;
  report.fixture.initialFetchMs = +fetchMs.toFixed(3);
  log(`fixture: ${bytes.byteLength} bytes, fetched in ${fetchMs.toFixed(1)} ms`);

  const targets = seekTargets(FIXTURE_DURATION_SEC);

  for (const name of ['mediabunny', 'mp4box']) {
    try {
      log(`── ${name}: parse + packets + seeks …`);
      const base =
        name === 'mediabunny'
          ? await runMediabunny(bytes, targets)
          : await runMp4box(bytes, targets);
      report.demuxers[name] = base;
      log(
        `   parse ${base.parseMs} ms, ${base.packetsParsed} packets, ` +
          `first-key seek ${base.firstKeyframeSeek.medianUs} µs, ` +
          `random seek first-touch median ${base.randomSeek.firstTouch.medianUs} µs, ` +
          `integrity failures ${base.randomSeek.integrityFailures}`,
      );
    } catch (e) {
      report.errors.push(`${name}: ${e?.stack ?? e}`);
      log(`${name} FAILED: ${e?.message ?? e}`, true);
      continue;
    }
  }

  // ── open → first sample, and open → first decoded frame (medians of OPEN_REPEATS) ──
  // Measured twice: with the library's own duration computation, and in "fast" mode that
  // skips it — which is what a player chasing the first frame would do. mp4box's duration
  // is free from the moov header, so its two figures are expected to match.
  for (const name of ['mediabunny', 'mp4box']) {
    const bucket = report.demuxers[name];
    if (!bucket) continue;
    const openSample = name === 'mediabunny' ? mediabunnyOpenToFirstSample : mp4boxOpenToFirstSample;
    const openFrame =
      name === 'mediabunny' ? mediabunnyOpenToFirstDecodedFrame : mp4boxOpenToFirstDecodedFrame;

    const collect = async (fn, withDuration) => {
      const xs = [];
      for (let i = 0; i < OPEN_REPEATS; i++) xs.push((await fn(withDuration)).ms);
      return xs;
    };

    try {
      bucket.openToFirstSample = summariseMs(await collect(openSample, true));
      bucket.openToFirstSampleFast = summariseMs(await collect(openSample, false));
      bucket.openToFirstDecodedFrame = summariseMs(await collect(openFrame, true));
      bucket.openToFirstDecodedFrameFast = summariseMs(await collect(openFrame, false));
      log(
        `${name}: open→first sample ${bucket.openToFirstSample.medianUs} µs ` +
          `(fast ${bucket.openToFirstSampleFast.medianUs} µs), ` +
          `open→first decoded frame ${bucket.openToFirstDecodedFrame.medianUs} µs ` +
          `(fast ${bucket.openToFirstDecodedFrameFast.medianUs} µs)`,
      );
    } catch (e) {
      bucket.openToFirstDecodedFrame = null;
      report.errors.push(`${name} open→frame: ${e?.stack ?? e}`);
      log(`${name} open→first decoded frame FAILED: ${e?.message ?? e}`, true);
    }
  }

  report.finishedAt = new Date().toISOString();
  return report;
}

window.runSpike = runSpike;
window.__spikeReady = true;
log('harness ready — call window.runSpike()');
