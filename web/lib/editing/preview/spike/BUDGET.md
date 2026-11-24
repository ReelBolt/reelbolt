# Compositor performance budget — editor v2 preview

**Initiative:** `editor-v2-complete` · **Ticket:** T-201 (spike (a), part 2 of 2; T-200 is part 1) ·
**Status:** measured on this host, software GL. **This file is the artifact P3-F1 consumes.**
It is the one thing in `web/lib/editing/preview/spike/` that a later wave should keep until
P3-F1 has taken its numbers; everything around it is throwaway.

Raw measurements: [`measurements.t201.json`](./measurements.t201.json). Harness:
[`playback-harness.mjs`](./playback-harness.mjs) + [`run-playback.mjs`](./run-playback.mjs).
Reproduction commands: [`README.md`](./README.md).

---

## 1. The host, named

| | |
| --- | --- |
| CPU | **AMD Ryzen 9 5950X 16-Core Processor**, 32 threads, 76 GB RAM |
| GPU physically present | **AMD Radeon AI PRO R9700 (Navi 48)**, PCI `29:00.0` |
| GPU usable by Chromium | **NO — there is no `/dev/dri` inside this sandbox** |
| GL renderer observed | `ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver)` |
| GL mode | **software** (`isSoftware = true`) |
| Browser | Chromium **153.0.8010.12** (Playwright 1.63.0, `channel: 'chromium'`, `--disable-gpu-sandbox`) |
| `performance.now()` resolution | **100 µs** — a raw median of 0 is the clock floor, not a measurement of zero |
| Host load during the run | load average **26–36 on 32 threads** (other agents shared this worktree) |

**Say this plainly: a hardware-GL budget cannot be established on this host.** The machine has a
real Radeon; the sandbox hides it. Every GL number below is a SwiftShader number and is a
**lower bound on capable hardware**, not a product budget. It is reported because it is what was
actually observed, and because the *relative* decomposition (composite vs decode vs present) is
the part that transfers.

## 2. Scenario

4 video layers + 6 audio tracks, 1080p30 source, 30 fps target, **32.16 s of real playback per
session, 3 sessions**, cross-run medians. Composite target 1280×720 unless stated.

- Source: `fixtures/playback/proxy-1080p-36s.mp4` — 1920×1080, 30 fps, 36 s, H.264 High
  (`avc1.640028`), 1080 frames, 36 IDRs (one per second), I/P/B, no audio. 6,512,582 bytes.
- Audio: six 10 s stereo AAC-LC 96 kbps stems, distinct fundamentals + tremolo, each measured by
  its own `AnalyserNode`.
- The four layers read the same proxy file but each owns an **independent `VideoDecoder`**.
  Justification is in `playback-harness.mjs`'s header. `compositeCheck` read back the framebuffer
  and confirmed **4 of 4** layers actually reached it.

## 3. The budget numbers, reported separately — never blended

Median of 3 sessions (per-run figures in brackets where the run-to-run spread matters).

| Metric | Measured | Budget | Met? |
| --- | --- | --- | --- |
| **Achieved fps** (deadlines met on time) | **6.13** (4.81 / 6.13 / 6.33) | 30 | ❌ **20 %** |
| **Dropped frames** | **767 of 964** deadline slots = **79.56 %** (78.90–83.97 %) | < 1 frame/s | ❌ |
| Frames actually presented | 212 (of 964) | — | — |
| rAF rate during the run | **8.08 Hz** (bare-page control: **61.94 Hz**) | ≥ 30 Hz | ❌ |
| **Decode latency** p50 / p95 / p99 | **105.1 / 236.3 / 538.5 ms** | — | see §5 |
| Decode *delivered* rate per layer | **29.75 fps** (4 layers ≈ 119 fps) | ≥ 30 fps/layer | ✅ |
| Decoded-but-evicted (undisplayed) | 28 over 32 s | 0 | ⚠️ |
| Frames buffered ahead of playhead | min **0**, p50 **8** | > 0 | ⚠️ |
| **GL composite** p50 / p95 — `gl.finish()`-bounded | 2.85 / 4.90 ms | 33.3 ms | **misleading, see §4** |
| **GL composite** p50 / p95 — `readPixels`-synced (sampled) | **339.6 / 491.2 ms** (327.5 / 339.6 / 367.2) | 33.3 ms | ❌ **10× over** |
| Audio clock drift over 32.16 s | **−3.67 ms** (−4.43 / −3.67 / +6.10) | audio is master | ✅ |
| Per-stem RMS (all six) | 0.0395 – 0.0483, none silent | all six live | ✅ |

At the proxy's native **1920×1080** composite target (1 session, 15.1 s): achieved **5.23 fps**,
**82.52 %** dropped, composite control **6.96 Hz**, synced composite p50 **919 ms**.

## 4. What the frame rate is actually spent on — the decomposition controls

Four controls, ~2 s each, same page, same browser, same launch flags. The decoders were restarted
and every counter reset between a control and the measured run.

| Control | rAF | ms/frame |
| --- | --- | --- |
| Bare page, no canvas, nothing running | **61.94 Hz** | 16.1 |
| Canvas attached, `gl.clear()` only | **61.54 Hz** | 16.2 |
| **Full 4-frame composite, decoders idle** | **5.52 Hz** | **181** |
| 4 decoders flat out, no composite | **41.84 Hz** | 24 |
| The actual scenario | 8.08 Hz | 124 |

**The WebGL composite is the binding constraint; decode is not.** Presenting a 1280×720 canvas
costs nothing (61.5 Hz). Four 1080p H.264 decoders running flat out cost ~24 ms/frame. The
composite of four full-frame blended quads plus four texture uploads from 1080p `VideoFrame`s
costs **~181 ms/frame**, and it alone drops the page from 62 Hz to 5.5 Hz.

⚠️ **`gl.finish()` is not a synchronisation primitive in WebGL2, and it silently fabricates
numbers.** Timed to `gl.finish()` the composite reads **2.85 ms**; forced to complete with a
1-pixel `readPixels` it is **339 ms** — a factor of **~119×** wrong. Anyone re-measuring
compositor cost here must sync with a readback (sampled, because a readback on this host itself
costs hundreds of milliseconds; per-frame readbacks starve the whole loop — measured, not
guessed).

## 5. Decoder pool + look-ahead — the pinned spec

Measured, not theoretical. The sensitivity runs (`--lookahead 6`, `12`, `24`) are in
`measurements.t201.json`.

| Knob | Pinned value | Evidence |
| --- | --- | --- |
| Decoders | **1 `VideoDecoder` per video layer** (4 for the 4-layer scenario). Never shared across layers. | 4 independent decoders delivered 29.75 fps each |
| Look-ahead depth | **12 frames = 400 ms** at 30 fps | see below |
| Decoded-frame ceiling | **18 frames** per layer | must exceed the look-ahead — see the footgun |
| Encoded queue bound | **8** (`decoder.decodeQueueSize`) | bounds decoder memory |
| Eviction order | **oldest-first by presentation index**; the frame currently on screen is exempt | `evictedUndisplayed` = 28 over 32 s at 12/18 |
| Feed source | a **4 ms timer on the main thread, plus the decoder's own output callback**. **Never rAF alone.** | feeding only from rAF made a 37 s run report 174 ms "decode latency" that was purely the render cadence |

**Why 12 / 400 ms.** It is the smallest look-ahead that kept the buffer non-empty in the
measured runs at p50 = 8 frames of headroom, with a 6-frame margin to the ceiling. Cutting it to
**6** (200 ms) still worked but the p50 headroom collapsed to **2 frames** — no margin at all.
Raising it to **24 exceeds the 18-frame ceiling and breaks the pool completely**; see below.

### ⚠️ The footgun, found by measurement

`--lookahead 24` with a ceiling of 18 produced **0.00 achieved fps, 0 frames presented,
100 % dropped, and 2292 frames decoded and evicted without ever being shown** — over 15 s, on the
same host, same harness. The decoder runs ahead of what the buffer may hold, so eviction closes
exactly the frames the playhead is about to ask for, and the preview goes black while reporting
that it is decoding perfectly.

**Invariant P3-F1 must enforce:** `maxDecodedBufferedFrames ≥ lookAheadFrames + 2`

**Enforced, not just stated.** `playback-harness.mjs` now throws at construction when the shipped pool violates it, and re-asserts after every `__spikeSetPool` override — the override path is exactly what violated it here. Suspending it is an explicit `allowUnsafeLookAhead: true` opt-in used only by the `--lookahead` experiment, and the override result carries `poolInvariantSatisfied` so a suspended run cannot be read as a normal one. Pinned by `web/app/spike/spike-pool-invariant.test.ts`.
(+1 for the frame on screen, +1 for the one in flight). Assert it at construction; do not let it
be two independently-tunable numbers.

### Missed-deadline behaviour — exactly what happens

A **deadline** for frame `i` is `t0 + i/30`. It is **missed** when the render tick that owns it
finds any of the four layers without frame `i` decoded, or when whole ticks are skipped between
two rAF callbacks. Measured p50 lateness at the moment of the presentation attempt: **16.9 ms**
(p95 31.6 ms).

1. **Which frames drop:** whole presentation slots, per deadline — never a partial composite.
   `droppedFrames = targetDeadlines − onTimeFrames`.
2. **The stack is never degraded.** The compositor still draws, re-uploading the frame each late
   layer is *already holding* — a **repeat**, not a freeze and not a silent 3-layer composite.
   Layers leave the composite only when the user removes them.
3. **Audio stays master.** The WebAudio graph is never paused, seeked or re-timed by a video
   drop; measured drift was **−3.67 ms over 32 s**. The playhead is the wall clock, not the
   decoder: video never re-times audio to catch up.
4. **What the UI is told:** a `droppedFrames` counter and a staleness flag. P3-F1 owns the badge;
   this file only pins that the signal exists and that a repeat is distinguishable from a freeze.
5. **Under sustained misses** the pool keeps feeding (the feed is not gated on the render loop)
   and the ceiling keeps evicting oldest-first, so memory stays bounded and recovery is automatic
   when the load lifts.

## 6. §3.5 fallback verdict — **CONFIRMED**

The experiment made WebCodecs unavailable *inside the page* (`delete`-equivalent redefinition of
`VideoDecoder`, `VideoEncoder`, `EncodedVideoChunk`, `VideoFrame` — all four reported `removed`),
then called the same `window.runScenario()` entry point the primary path uses.

- Observed path: **`fallback-video-element`** — the page took the fallback on its own.
- A single `<video>` over the proxy: first frame at **240 ms**; `requestVideoFrameCallback`
  counted **956 decoded frames presented over 32.01 s = 29.87 fps** against a 30 fps target;
  `getVideoPlaybackQuality()` reports **960 total frames, 5 dropped** for the window.
- No console errors, no request failures.

**The fallback is real, it takes over automatically, and it is smooth.** On this software-GL host
it is **~4.9× faster than the 4-layer composite** (29.87 fps vs 6.13 fps) — because it composites
nothing and mixes no audio. Its numbers are **not** comparable to the primary path and are
reported separately on purpose.

## 7. Parts of the budget that could NOT be met

| Not met | Number | Why | Mitigation |
| --- | --- | --- | --- |
| **30 fps with 4 layers** | 6.13 fps | WebGL composite ≈181 ms/frame on SwiftShader | **Not fixable on software GL.** P3-F1 should probe the GL renderer at startup and, when software, *refuse the multi-layer composite* and serve the §3.5 single-track player (29.87 fps) with a clear "preview is approximate on this device" state. The 4-layer composite is a hardware-GL feature. |
| **Budget on capable hardware** | unmeasurable | no `/dev/dri` in this sandbox | Run the same three commands on a host with a GPU exposed; the harness, fixtures and driver are committed and need no changes. Until then, no hardware-GL budget exists and none should be quoted. |
| **GL composite inside the 33.3 ms frame budget** | 339 ms synced / 181 ms control | SwiftShader | As above. Note that the cost does **not** track canvas area (720p 181 ms vs 1080p 919 ms synced but 6.96 Hz control vs 5.52 Hz), and that shrinking the preview canvas was therefore **not** an effective mitigation on this host — the `--mode sizes` probe was noisy and non-monotonic and is reported as inconclusive rather than dressed up. |
| **Decode headroom margin** | buffered-ahead min **0** | 12-frame look-ahead with p50 headroom 8 leaves no floor; a transient stall empties the buffer | Decode itself is fine (29.75 fps/layer delivered, 24 ms/frame for all four). Raise the ceiling to 24 and re-measure on an unloaded host before fixing the final look-ahead. |
| **A low-noise fill-rate curve** | inconclusive | host at load 26–36 throughout; the five canvas sizes did not rank monotonically by area | Re-run `--mode sizes` on an idle host. |

**Met:** the decoder pool sustains 30 fps per layer; all six audio tracks run and audio is
genuinely master; the fallback path works; the composite is verifiably 4 real layers.
