# Spike (a) harness — demuxer decision (T-200) + compositor budget (T-201)

**Initiative:** `editor-v2-complete` · **Tickets:** T-200 (plan task P0-F1 part 1) and T-201
(part 2) · **Status:** throwaway. **Everything in this directory, plus `web/app/spike/**`, is
REMOVABLE.**

The deliverable is a decision and the numbers behind it, not shipping code. What a later wave
must keep is [`BUDGET.md`](./BUDGET.md) — the decoder-pool/look-ahead spec, the budget numbers
and the fallback verdict that P3-F1 builds the real compositor from — and, in the product, the
**winning** demuxer `mediabunny`. Everything else here exists to produce that file.

- Raw measurements behind T-200's demuxer decision: [`measurements.t200.json`](./measurements.t200.json)
- Raw measurements behind T-201's budget: [`measurements.t201.json`](./measurements.t201.json)

## Layout — what is here and what it is for

| Path | REMOVABLE | What it is |
| --- | --- | --- |
| `make-fixture.sh` | ✅ | T-200. Regenerates the committed demuxer fixture deterministically (ffmpeg + the ffprobe evidence). Nothing in the product reads it. |
| `fixtures/proxy-1080p-12s.mp4` | ✅ | T-200. The 1080p H.264 + AAC / 12 s / 1-IDR-per-second input both demuxers were measured against. Committed, 3,357,815 bytes. |
| `make-playback-fixture.sh` | ✅ | T-201. Regenerates the playback fixtures deterministically (36 s 1080p30 proxy + six 10 s stereo AAC stems) with the ffprobe evidence. |
| `fixtures/playback/proxy-1080p-36s.mp4` | ✅ | T-201. The 4-layer playback source: 1080p30 H.264 High, 36 s, 1080 frames, one IDR per second, I/P/B, no audio. Committed, 6,512,582 bytes. |
| `fixtures/playback/stem-1.m4a` … `stem-6.m4a` (six files) | ✅ | T-201. The six WebAudio tracks: 10 s stereo AAC-LC at 96 kbps, distinct fundamentals + tremolo. |
| `README.md` | ✅ | This file. Like everything else here, it goes when the spike does — except that `BUDGET.md`'s numbers must be carried into `docs/editor.md` first. |
| `harness.html` | ✅ | T-200. The demuxer measurement page, with the import map (see below). |
| `harness.mjs` | ✅ | T-200. The demuxer measurement code. Exposes `window.runSpike()`. |
| `run-spike.mjs` | ✅ | T-200. Playwright driver for the demuxer comparison; writes `measurements.t200.json`. |
| `playback.html` | ✅ | T-201. The compositor budget page. |
| `playback-harness.mjs` | ✅ | T-201. The 4-layer / 6-track compositor harness, the two decomposition controls and the §3.5 fallback path. Exposes `window.runScenario()`. Also ASSERTS the decoder-pool invariant (`maxDecodedBufferedFrames >= lookAheadFrames + 2`) at construction and after every `__spikeSetPool` override, because the harness reported healthy at 0.00 fps without it. |
| `run-playback.mjs` | ✅ | T-201. Playwright driver for the budget; writes `measurements.t201.json`. |
| `browser-node-fs-shim.mjs` | ✅ | Browser shim for `node:fs/promises`, needed to load mediabunny's ESM entry natively. Used by both pages. |
| `serve.mjs` | ✅ | Dev-only `node:http` static server rooted at `web/`. Replaces `next dev` for this work. |
| `measurements.t200.json` | ✅ | T-200's committed raw report. |
| `measurements.t201.json` | ✅ | T-201's committed raw report. |
| `BUDGET.md` | ⚠️ **KEEP UNTIL P3-F1 HAS CONSUMED IT** | T-201's durable artifact: budget numbers, decoder-pool/look-ahead spec, missed-deadline behaviour, fallback verdict, with the host's CPU/GPU named. |
| `web/app/spike/page.tsx` | ✅ | Dev-only route; the production-build reachability guard (returns `notFound()` under `NODE_ENV === 'production'`). |
| `web/app/spike/spike-guard.test.tsx` | ✅ | Asserts that guard fires. |
| `web/app/spike/spike-isolation.test.ts` | ✅ | Drift guard: no module under `app/(app)/**` or `components/**` may import anything from the spike harness. |
| `web/app/spike/spike-pool-invariant.test.ts` | ✅ | Drift guard: the decoder-pool invariant is asserted in the harness, the shipped pool satisfies it, every override re-asserts it, and suspending it is an explicit opt-in the `--lookahead` experiment states. |

### ⚠️ `harness.mjs` / `harness.html` / `run-spike.mjs` no longer run

T-201 removed the losing demuxer `mp4box` from `web/package.json`, exactly as T-200's decision
required. The T-200 harness's `import * as mp4box from '/node_modules/mp4box/dist/mp4box.all.mjs'`
therefore no longer resolves, and `run-spike.mjs` cannot be re-run without reinstalling `mp4box`.
Those three files are kept as **the historical record of the measurement that produced the
decision**; their numbers are in `measurements.t200.json` and are not reproducible from the tree
any more. Nothing in the product, in the build, or in the T-201 budget path touches them.
`playback-harness.mjs` (T-201) imports only `mediabunny`, so the budget path stays runnable.

## Reproduction

```bash
cd web

# T-201 — the compositor budget (>=30 s of real playback per session, 3 sessions)
env -u NODE_ENV node lib/editing/preview/spike/run-playback.mjs --mode main --runs 3 --seconds 32 --warmup 5

# T-201 — the same scenario at the proxy's native 1920x1080 composite target
env -u NODE_ENV node lib/editing/preview/spike/run-playback.mjs --mode native --runs 1 --seconds 15 --warmup 4

# T-201 — the §3.5 fallback experiment (WebCodecs removed inside the page)
env -u NODE_ENV node lib/editing/preview/spike/run-playback.mjs --mode fallback --runs 1 --seconds 32

# T-201 — decoder-pool sensitivity and the composite fill-rate curve
env -u NODE_ENV node lib/editing/preview/spike/run-playback.mjs --mode pool --lookahead 6  --runs 1 --seconds 15 --warmup 4
env -u NODE_ENV node lib/editing/preview/spike/run-playback.mjs --mode pool --lookahead 24 --runs 1 --seconds 15 --warmup 4
env -u NODE_ENV node lib/editing/preview/spike/run-playback.mjs --mode sizes --runs 1 --seconds 6 --warmup 3

# fixtures (both are committed; regenerate only if you need to)
./lib/editing/preview/spike/make-playback-fixture.sh
./lib/editing/preview/spike/make-fixture.sh          # T-200's demuxer fixture
```

`NODE_ENV` must be unset (`env -u NODE_ENV`) on this host. Playwright is resolved from
`e2e/node_modules` by absolute path — **do not add `playwright` to `web/package.json`**, and
nothing under `e2e/**` is written.

## Environment findings a later wave will otherwise re-discover

These cost real time to find. They are recorded here because this directory is what gets
deleted, and the knowledge is not in the code.

1. **`mediabunny`'s browser entry does NOT load as a native ES module.** The T-200 ticket's
   premise ("both demuxers ship browser-ready ESM with no bare imports … no bundler and no import
   map are required") is false for mediabunny 1.61.0. Static chain:
   `index.js → source.js → node.js → export * as fs from 'node:fs/promises'`. The `node:`
   scheme is rejected before any request is made, so **no server can intercept it** — only an
   import map can. Both pages map that one specifier to `browser-node-fs-shim.mjs`, reproducing
   mediabunny's own `package.json` `browser` field (`{ "node:fs/promises": false }`), which
   native ESM ignores. The shimmed module is unreachable from the demuxing path.
2. **`mp4box` had no default export** (moot now that it is removed, kept for the record):
   `import MP4Box from 'mp4box/dist/mp4box.all.mjs'` was a link-time `SyntaxError`.
3. **Chromium has no usable WebGL here without `--disable-gpu-sandbox`.** There is no
   `/dev/dri` and no X server. Without that flag ANGLE selects `DisplayVkXcb`, `xcb_connect()`
   fails with error 1, SwANGLE returns `EGL_NOT_INITIALIZED`, the GPU process exits, and
   `canvas.getContext('webgl2')` is `null` — under *every* other combination tried. With it,
   WebGL 2.0 comes up on `ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero)), SwiftShader
   driver)` — **software GL**, so every GL-derived number is a software-GL number. Also required:
   `channel: 'chromium'` (the full build, not `chromium_headless_shell`).
4. **`gl.finish()` is NOT a synchronisation primitive in WebGL2**, and this silently fabricates
   numbers. A composite timed to `gl.finish()` measured **3.0 ms/frame** on this host while the
   frame loop was demonstrably running at **10 Hz** — i.e. the true cost was ~100 ms and the
   headline figure was 30× too small. Only a `readPixels` forces the pipeline to complete. T-201
   therefore reports `totalSyncedMs` (sampled, because a readback here costs hundreds of
   milliseconds) and the two **controls** that decompose the frame rate. Anyone re-measuring
   compositor cost on this host must do the same.
5. **WebAudio needs `--autoplay-policy=no-user-gesture-required`.** An `AudioContext` created
   without a user gesture starts `suspended`, renders nothing, and `currentTime` does not
   advance — the six-track half of the scenario would silently measure zero work.
6. **Feeding the decoders from the render loop measures the render loop, not the decoder.**
   With the pool pumped only from `requestAnimationFrame`, "decode latency" came out at 174 ms
   because the pump cadence *was* the render cadence. The pool must be fed from its own timer
   (and from the decoder's own drain), which is what a real player does.

## Deletion checklist

When the spike has served its purpose, one commit deletes:

- [ ] `web/lib/editing/preview/spike/` (the whole directory — `BUDGET.md` included, **once
      P3-F1 has consumed it**)
- [ ] `web/app/spike/` (the whole directory)

`mediabunny` stays: it is the winning demuxer and the compositor's demuxer of choice. `mp4box`
was already removed by T-201. Nothing outside those two places references the harness, which is
exactly what `web/app/spike/spike-isolation.test.ts` proves.
