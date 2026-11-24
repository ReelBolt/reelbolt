# Graphics agents see the frame and size titles on the canvas

Status: implemented on `feat/graphics-see-frame-and-canvas-size`. Motivated by runs
`30a2792e-18b5-4e5d-90bd-e0bea3d852aa` and `d0ce2c25-26db-4b00-837a-5dfbf1d29f72` (issue 12 of
`ISSUES-ig-reel-2026-11-03.md`).

## The failure

The graphics room rendered the title "Caffè Lume / First cup on us" as a Remotion asset sized for
a 1920-wide frame (1600x216). The compile placed assets relative to a 1920 reference width and
only ever scaled them down to fit the band, so on the 1080x1920 output the asset was drawn at 0.56x
and the caps came out at about 2% of the frame height. The review loop's visual check flagged it
three times (floor 2.5%). Nothing could fix it:

- `overlayFontSizePct` only reaches the drawtext path; an asset overlay never reads it.
- The agents never saw the frame or the output canvas, only region names and fractions.
- No step measured the rendered type before the compile shrank it.

## What changes

1. **The agents see the frame.** Before the planner or the room runs, the placement annotator
   (`MotionGraphicsPlacementAnnotator`, the hook both already call) adds `view.graphicsCanvas`:
   the output canvas (width x height after the consuming compile's `outputFormat` and the run's
   format and resolution choice), the legibility floor, the cap and font sizes each `textSize`
   word stands for in canvas pixels, and every placement's band in canvas pixels. When a Vision
   provider is configured, `PlacementStillsDescriber` grabs up to 4 stills (one per shot, at most
   3 bands each) from the source at the placement time, fitted to the canvas the way the compile
   fits it, with each band outlined and labelled `p{n}`, and asks the Vision model ONE metered
   question about what sits behind each band (clean or busy, light or dark, a face, a product,
   existing text). The answers land in `view.graphicsCanvas.seen`. No Vision provider, no source
   key, an ffmpeg failure or a malformed answer leaves `seen` out and records
   `stills.status: "unavailable"` with a fixed reason; the step never fails for it.

   *Why a described still and not an image on the agent call:* the room replays its opening
   message on every seat turn (issue 10 measured ~700k tokens for one title), and the planner's
   tool loop replays it on every tool round, so an image there is paid tens of times. The agent's
   own Chat model may also have no image input. One Vision call per step, shared by every seat, is
   bounded (4 stills, 768 px long side) and metered by the existing `MeteringChatClient` path,
   exactly like the review loop's visual check. Attaching the stills directly to the synthesis
   call is a possible follow-up (open question).

2. **Assets are drawn on the full output canvas.** `MotionGraphicsOverlay` gains three
   append-only STRING members, so the rushcut invariant (no numeric, time- or coordinate-bearing
   property) still holds and its tests stay strict:

   | Member | Words | Meaning |
   |---|---|---|
   | `assetCanvas` | `""` (legacy) or `Output` | `Output`: the asset is a transparent composition exactly the size of `view.graphicsCanvas`; the compile composites it full frame at its designed size. |
   | `textSize` | `Small` `Medium` `Large` `XLarge` | cap height 3 / 4 / 5.5 / 7.5 % of the output height, never below the floor, never above 12 %. Drives drawtext size; for an asset it states the intent the agent renders at. |
   | `anchor` | `Top` `Center` `Bottom` | where in its band a drawtext overlay sits (default: the region's own edge). |

   Words instead of the bounded numbers the request allowed: the enum is bounded by construction,
   the server owns every number (`OverlayTextSizing`), and the invariant tests keep banning numeric
   members. The agent still positions type inside its own composition in pixels: those numbers
   live in the Remotion code it renders, never in the plan.

   The compile composites an `Output` asset uniformly fitted to the canvas (scale 1 when it was
   rendered at the canvas size) and records `designedWidth/Height`, `drawnWidth/Height` and
   `scale` for every asset overlay. A legacy band asset still uses the band fit, but its scale is
   now recorded and a downscale below 0.98 adds a `graphics.warnings` entry saying so.

3. **Legibility is measured and enforced.** While a graphics step runs, its step context carries
   an `OverlayLegibilityLedger` (canvas + floor), published to the render tool the same way the
   render-failure ledger is. After each successful render of a canvas-shaped composition the
   render tool runs the existing layout lint (`CheckLayout`'s script) with `minTextPx` = the font
   size whose cap height is the floor (`ceil(H x floor% / 0.7)`), records the result per storage
   key and tells the agent in the render result to enlarge the type and re-render with `force`.
   Compositions of another shape (screen inserts, legacy band assets) are recorded as
   `not_canvas_sized` and never refused. The executor writes the records it holds for the plan's
   overlay keys into the step output as `overlayLegibility` (server-written; a model-supplied key
   of that name is removed). The graphics room spends its one policy retry on a failed asset.

   The compile then enforces it: an asset measured below its floor is replaced by a drawtext
   overlay of the same entry's `text` (now asked for on every asset overlay as the fallback), at
   least the floor in size; without `text` it is dropped with reason `asset_text_below_floor`.
   Every decision is listed in `graphics.legibility`.

4. **One setting reaches asset overlays.** `VideoCompileStepConfig.MinOverlayCapHeightPct`
   (default 2.5, clamped 1..12): the floor for drawtext (font raised to it), the floor the graphics
   step measures assets against (it reads the compile step that consumes its plan), and the floor
   the compile enforces. An asset measured at a lower floor than the compile's is kept but flagged
   `unverified_at_floor`.

## Defaults and compatibility

- Seeing the frame is additive: without a Vision provider the view gains only the canvas facts.
- `assetCanvas: Output` is what the prompts now ask for; old plans (no word) compile exactly as
  before apart from the recorded scale. The drawtext floor only moves type smaller than 2.5% caps
  (an `overlayFontSizePct` of 3 or less); the default 5 is untouched.
- `DeterministicStepRevisions` for `VideoCompile` gains `+canvas-overlays-1`, so cached compiles
  made before this change are not reused.
- The planner and director prompts (class `DefaultPrompt` and `DatabaseSeeder.BuiltInAgents`) and
  the graphics room charter change together; the consistency tests pin them.
