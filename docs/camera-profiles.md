# Camera log profiles

Camera log profiles convert footage shot in a camera's log curve (Apple Log, S-Log3, V-Log and the
others) to standard Rec.709 colour at the start of its processing chain, so every later stage — trim,
crop, transform, grade, preview — builds on a picture with normal contrast and saturation. The
implementation lives in `inference/src/ReelBolt.Shared/Editing/Color/`; the browser mirror is
`web/lib/editing/color/camera-profiles.ts`.

This page covers the model (the clip field and the closed list), the maths, the render and preview
stages, detection, the preview-only viewing LUT, the workflow and agent surfaces, and the known gaps.
The customer page is [`user-guide/editor/camera-profiles.md`](user-guide/editor/camera-profiles.md).

## The model: `MediaClip.inputProfile`

A media clip carries an optional `inputProfile` (`inference/src/ReelBolt.Shared/Editing/Item.cs`): a
closed camel-case id from `CameraProfiles.Ids`, or `null`/absent for no conversion.

- **Twelve ids**, in the frozen order of `CameraProfiles.All` (`CameraProfiles.cs`):
  `appleLog`, `djiDLog`, `djiDLogM`, `sonySLog3`, `panasonicVLog`, `canonCLog3`, `arriLogC3`,
  `arriLogC4`, `fujiFLog`, `fujiFLog2`, `nikonNLog`, `redLog3G10`.
- **The existing op, no new op.** It is written by `setProperty` (the key is in the media-clip
  allowlist, `web/lib/editing/engine/ops.ts`), so the op catalogue stays at **34** and the document
  schema at `schemaVersion: 2`. The engine refuses an unknown id before the op is sent
  (`web/lib/editing/engine/apply.ts`) and the server refuses it again at
  `tracks[i].items[j].inputProfile` (`EditTimelineValidator.cs`).
- **No identity change.** A document without a profile serializes without the key; flattening a
  compound sequence keeps it (`TimelineFlattener.cs`); a copy helper that rebuilds a `MediaClip` must
  pass it through, which `InputProfileContractTests` pins.
- **Multicam angles carry it too.** A `MulticamAngle` has its own optional `inputProfile`, set from
  the source clip when the multicam is created (`web/lib/editing/structure/multicam.ts`) and copied
  onto every run the flattener emits for that angle (`TimelineFlattener.cs`), so an angle switch
  keeps the conversion in both preview and render.
- **Not a general "input colour" field.** HDR (HLG/PQ) is handled by its own tone-map; a clip can have
  both, and the HDR path wins (below).

## The twelve profiles

All data is in `CameraProfiles.All`; the table below is a readable view of it. `ExpectedRange` and
`ExpectedYuvMatrix` are the **profile's** published signal conventions, used instead of the file tags.

| Id | Display name | Maker | Expected range | Expected YUV matrix | Status |
|---|---|---|---|---|---|
| `appleLog` | Apple Log | Apple | legal | `bt2020nc` | `uncalibrated` |
| `djiDLog` | D-Log | DJI | full | `bt709` | `uncalibrated` |
| `djiDLogM` | D-Log M | DJI | full | `bt709` | `approximate` |
| `sonySLog3` | S-Log3 | Sony | full | `bt709` | `uncalibrated` |
| `panasonicVLog` | V-Log | Panasonic | full | `bt709` | `uncalibrated` |
| `canonCLog3` | Canon Log 3 | Canon | full | `bt709` | `uncalibrated` |
| `arriLogC3` | LogC3 | ARRI | legal | `bt709` | `uncalibrated` |
| `arriLogC4` | LogC4 | ARRI | legal | `bt709` | `uncalibrated` |
| `fujiFLog` | F-Log | Fujifilm | full | `bt2020nc` | `uncalibrated` |
| `fujiFLog2` | F-Log2 | Fujifilm | full | `bt2020nc` | `uncalibrated` |
| `nikonNLog` | N-Log | Nikon | legal | `bt2020nc` | `uncalibrated` |
| `redLog3G10` | Log3G10 | RED | legal | `bt709` | `uncalibrated` |

`CameraProfileStatus` (`CameraProfileDefinition.cs`) has three values: `uncalibrated` (the formula is
taken from the maker's document but has not been checked against real footage), `approximate` (an
explicit stand-in where no public specification exists) and `calibrated` (checked on real footage).
No profile is `calibrated` yet. Only `djiDLogM` is `approximate`: DJI publishes no D-Log M
specification, so the D-Log curve and D-Gamut stand in, and this is not a calibrated D-Log M decoder.

The order of `CameraProfiles.All` is **frozen and append-only**: the index is the proxy derivative
variant number (see `CameraProfileVariants`), and a test pins the order.

## Where the tables come from

The curves and gamut matrices are **our own implementation** of the makers' published technical
documents. No manufacturer LUT file is included in the repository, and none is shipped or
redistributed.

Each profile names its source document in `CameraProfileDefinition.SourceNote`:

| Maker | Document |
|---|---|
| Apple | `IDT.Apple.AppleLog_BT2020.ctl` (Apple-supplied, ACES aces-dev at `528c78f`); the Apple Log white paper was not read. Range is unconfirmed (ProRes convention assumed) |
| DJI | *White Paper on D-Log and D-Gamut of DJI Cinema Color System*, Rev. 1.0 (2017-09-29); D-Log M has no public specification and uses the D-Log curve |
| Sony | *Technical Summary for S-Gamut3.Cine/S-Log3 and S-Gamut3/S-Log3* (Sony, 2016); S-Gamut3.Cine is the default gamut |
| Panasonic | *V-Log/V-Gamut REFERENCE MANUAL* Rev. 1.0 (2014-11-28) |
| Canon | *Description of the Canon Log, Canon Log 2 and Canon Log 3 Gamma Curves* (white paper, 2018-11-01); Cinema Gamut; decoded value is scaled by 0.9 so grey is 0.18 |
| ARRI | *ALEXA Log C Curve - Usage in VFX* (ARRI, 2017-03-09), EI 800 parameters, ALEXA Wide Gamut; *ARRI LogC4 Logarithmic Color Space Specification* (2025-01-23), ARRI Wide Gamut 4 |
| Fujifilm | *F-Log Data Sheet* Ver. 1.2 and *F-Log2 Data Sheet* Ver. 1.1; F-Gamut uses BT.2020 primaries |
| Nikon | *N-Log Specification Document* Version 1.0.0 (2018-09-01); file range unconfirmed |
| RED | *White Paper on REDWideGamutRGB and Log3G10*, 915-0187 Rev C (2017-11); file range unconfirmed |

The research notes that collected the constants are kept in the internal research records
and are internal working material, not part of the documentation tree.

**Why no maker LUT ships.** None of the nine makers' published terms grant redistribution of their
conversion LUTs. Canon and Nikon forbid distribution in writing; the others grant nothing (several
are behind a login or carry site terms reserving all rights). See the internal licence note for the
evidence. Our own code is therefore the only implementation, and this paragraph belongs on internal
pages only — never on a customer page.

## The neutral display transform

`CameraDisplayTransform.ToRec709` (`CameraDisplayTransform.cs`) is our own neutral conversion, not a
maker's creative look. It is deliberately contrast-neutral and looks flatter than a maker LUT by
intent. Per channel after the profile's decode:

1. **Decode** the normalised code value (`0..1`, clamped) through the profile's log curve to scene
   linear, where `0.18` is 18% grey.
2. **Gamut matrix** from the camera's gamut to linear Rec.709 (the row-major 3x3 in the profile).
3. **Exposure**, identity unless an EV is supplied (the renderer supplies none).
4. **Soft shoulder**: identity below a knee of `0.45`, then
   `knee + (1 - knee) * tanh((x - knee) / (1 - knee))`.
5. **Rec.709 OETF**: `4.5y` below `0.018`, else `1.099 y^0.45 - 0.099`.

The result is a full-swing Rec.709-encoded signal; the renderer scales it to legal levels. The
anchors are checked by `CameraProfilesTests` and `CameraProfileSpecAnchorTests`: 18% grey decodes to a
neutral triple at **0.409** and 90% white to about **0.906**.

## Render: `CameraProfileStage`

`CameraProfileStage` (`TimelineRender/CameraProfileStage.cs`) turns a profile id plus a generated
`.cube` into the filter stage prepended to that clip's leg. `RegionFilterGraphBuilder` places it first
for an SDR leg that names a profile, in the same slot the HDR tone-map uses for an HDR leg.

- **HDR wins.** A source whose probe is HLG/PQ is tone-mapped and its profile is ignored. The region
  reports the warning `camera_profile_ignored_hdr` with a plain message (`TimelineRenderer.cs`); an
  `auto` request on an HDR source produces no warning because nothing explicit was set.
- **Range and matrix come from the profile, never the file tags.** Cameras tag log footage
  inconsistently (untagged, BT.601, BT.2020), and ffmpeg's implicit YUV-to-RGB conversion would follow
  those tags. The stage builds an explicit `zscale` prefix from `ExpectedRange`/`ExpectedYuvMatrix`
  (`rin=tv|pc`, `min=709|2020_ncl`, with transfer/primaries pinned to 709 so zscale does not touch the
  log signal) and falls back to `scale=in_range=…:in_color_matrix=…` on a host whose ffmpeg lacks
  `zscale` (`FfmpegCameraProfileCapability`, probed once per process). The fallback is self-consistent
  but slightly less accurate.
- **Precision.** The 65^3 table is fed to `lut3d` as `gbrpf32le`, never `rgb24` (8-bit quantisation
  adds about 3 code values of mean error), and interpolated **tetrahedrally**.
- **Output contract.** The stage ends in Rec.709 limited-range `yuv420p` with explicit tags
  (`setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709`), the same contract
  `HdrToneMap` ends on, so the rest of the leg sees a standard SDR picture.
- **Cache identity.** The region recipe hash carries
  `camera-profile-v1:<id>:<range>:<matrix>` **only** for a region whose clip has a profile and is not
  HDR (`RegionRecipeHasher.cs`). A region without a profile hashes exactly as before, and the region
  `RendererVersion` is **not** bumped (`timeline-render-region-v7`). `RecipeVersion` is
  `camera-profile-v1` and changes if the table or stage changes. A host whose ffmpeg lacks `zscale`
  appends `:swscale` to the token, so its slightly different pixels do not share a cache entry.
- **Trust boundary.** The only document value is the profile id, which the validator restricts to the
  catalogue; the cube path is the renderer's own scratch path.

## Export scope

The conversion is applied at render time, not written into an interchange file. Exporting to `edl`,
`srt`, `vtt` or `fcpxml` therefore drops `inputProfile` and reports the reason
("Camera profile conversion is not carried; apply it before importing"). **OTIO is lossless**: it
carries the ReelBolt document, so the field survives a round trip
(`EditTimelinesController.cs`, `TimelineInterchange.WriteOtio`).

## The viewing LUT (preview only)

A **viewing LUT** is a monitor LUT for the viewer: a project `.cube` or one of the camera profiles used
purely as a view. It is never part of the document and never reaches a render, a still export, a review
link or a hand-off — nothing that leaves the browser can carry it. It lives in `localStorage` per
device and per timeline (`reelbolt:editor:viewing-lut:<timelineId>`,
`web/lib/editing/prefs/viewing-lut.ts`) and is deliberately not in the editor-preferences API.

- **Menu and badge.** The viewer menu (`ViewingLutMenu.tsx`) lists `Off`, project LUT files and
  "Camera profile as view". While one is on, a permanent badge on the picture says "Viewing LUT on.
  Preview only, not in your export."
- **Phone.** The choice is ignored on the phone surface: the hook returns `null`, and the stored value
  is left alone for the device's other surfaces.
- **Clean picture.** Export-like readbacks never carry the LUT. `readCleanPicture` and the wrapped
  `toDataURL`/`toBlob` (`gl-compositor.ts`) re-present the frame with the viewing pass off, so scopes,
  reference stills and thumbnails read the clean picture.
- **Implementation.** Both stages are planned by `web/lib/editing/color/viewer-color.ts`: the clip
  input stage (the same 65^3 table the renderer uses) and the final viewing pass (the viewer only).
  A viewing pass runs only when a LUT is set **and** the caller did not ask for a clean picture.
- **Loader limits.** The viewer checks a project file's listed size against the 16 MiB cap before
  requesting its body. The parse runs in a worker with a 10 s timeout; a crashed worker falls back to
  a main-thread parse only up to 2 MiB. The compositor keeps at most four viewing-LUT textures (LRU).
  A stored file id is accepted only if it is a GUID, and the parsed table is cached by the file's
  upload time, so a same-length edit cannot serve a stale table (`cube-lut-loader.ts`,
  `viewing-lut.ts`, `useViewingLutSpec.ts`, `gl-compositor.ts`).

## Detection: `CameraProfileDetector`

`CameraProfileDetector` (`CameraProfileDetector.cs`) reads probe facts (transfer, primaries, matrix,
range, pixel format, bit depth, codec, container, make, model, software) and returns a profile id with
a confidence, or `None`. It is deliberately **conservative**: no standard transfer code identifies a
log curve, so a rule needs maker evidence plus a consistent colour signature.

| Rule | Confidence | Requirements |
|---|---|---|
| `apple_make_model_bt2020_prores` | **High** | Apple make, iPhone 15 Pro–class model (or later Pro), unspecified transfer, BT.2020 primaries, BT.2020 matrix, ProRes, 10-bit or more |
| `apple_make_model_bt2020` | Medium | Apple, same model family, unspecified transfer, BT.2020 primaries, ProRes or HEVC, 10-bit |
| `dji_make_10bit` | Medium | DJI make, an open transfer, 10-bit |
| `sony_make_model_10bit` | Medium | Sony make, model family `ILCE`/`ILME`/`FX`/`ZV`/`PXW`, an open transfer, 10-bit |
| `panasonic_make_model_10bit` | Medium | Panasonic make, `DC-GH`/`DC-S`/`DC-BS`/`DC-BGH` family, an open transfer, 10-bit |
| `canon_make_model_10bit` | Medium | Canon make, an EOS R or C-series model, an open transfer, 10-bit |
| `arri_alexa35_make_model` | Medium | ARRI make, ALEXA 35 model → LogC4 |
| `arri_make_10bit` | Medium | ARRI make, an open transfer, 10-bit → LogC3 |
| `fuji_make_model_10bit` | Medium | Fujifilm make, X-H2/X-H2S/X-T5/X-S20/X-T50 or GFX family, an open transfer, 10-bit |
| `nikon_make_model_10bit` | Medium | Nikon make, Z-series model, an open transfer, 10-bit |

An **open transfer** is one of `unspecified`, `bt709`, `smpte170m`, `bt470bg` or `iec61966-2-1`
(`OpenTransfers` in `CameraProfileDetector.cs`). A transfer outside that list — `bt2020-10`, for
example — is matched by no rule, even on a known maker's camera.

- **Only High is applied automatically.** Apple Log is the only High rule today; a High hint is
  auto-applied when a clip is added, with the badge "Converted from {profile} automatically", a note
  beside it and one-click undo. The badge and the note are **session-only** (an in-memory set in
  `auto-profile.ts`): after a reload the converted clip keeps its profile, but the badge no longer
  shows. Medium is a suggestion chip, never applied on its own.
- **HLG and PQ always return `hdr_transfer`.** `arib-std-b67` and `smpte2084` are checked before
  any maker rule and never return `no_match`. Plain BT.709 or BT.601 footage with no maker evidence
  never matches (the make must be present).
- **Rules are data.** The rule table (`Rules`) is intended to be tuned with real clips; adding or
  tightening a rule is a data-only change. The reason codes are stable machine words, never free text.

## Preview: the input stage and converted proxies

The browser viewer has an **input stage** in the WebGL2 compositor (`gl-compositor.ts`,
`viewer-color.ts`): a clip with `inputProfile` whose proxy is not already converted gets the same 65^3
table as a sampler3D, applied before the grade. It is trilinear, while the render's `lut3d` is
tetrahedral, so the parity classifier marks the clip `approximate` ("tiny colour differences are
possible"). A clip that also has a grade LUT gets a double-conversion hint. HDR clips skip the input
stage because the renderer ignores their profile.

Converted proxies avoid the visible banding of converting an 8-bit log proxy in the browser. The proxy
derivative carries a **variant index 1..12** (`CameraProfileVariants`: 0 is the plain proxy, `1..12`
map to `CameraProfiles.All`), with the conversion baked in at creation
(`ProxyArgvBuilder.cs`, `MediaDerivativeDispatcher.cs`). The encode scales to the requested proxy
height first and converts on that small frame — the same ordering as the HDR tone-map — so the float
`zscale`/`lut3d` stage never runs at full source resolution. The spike measured **112 usable mid-tone
levels with the conversion baked in, against 46 when converting unconverted log in the preview**, so the
baked path is the default.

- **Built automatically for High hints.** `MediaDerivativeDispatcher.WithConvertedProxy` inserts the
  variant when the request names one or when `AutoProfileFromProbe` returns a High-confidence profile.
  Medium hints never spend a proxy encode on their own. HDR sources are refused earlier.
- **On request.** The derivatives endpoint accepts `cameraProfile` (a known id on a video source; HDR
  and non-video sources are refused with `camera_profile_not_applicable_hdr` /
  `camera_profile_not_applicable_source`, an unknown id with `camera_profile_unknown`,
  `MediaController.cs`). The viewer asks for the pair at most once per session per source.
- **Filmstrip and thumbnails stay flat.** The filmstrip and stills derivatives read the **plain**
  proxy (`MediaDerivativeDispatcher.cs` comment: a converted one is never handed on). Timeline
  thumbnails and strips therefore keep the gray log look even when the viewer shows converted colour;
  this is a known limitation, not a bug.

## Workflows and agents

Both step configs gained an appended, optional `SourceInputProfiles` map
(`VideoCompileStepConfig.cs`, `VideoAnalyzeStepConfig.cs`): **source index → profile id or the word
`auto`**. `auto` applies only when the detector is High confidence; null is byte-identical.

- **Validation.** `SourceInputProfileRules` (shared by both) allows at most 64 entries, a non-negative
  index and a known id or `auto`. The save-path validator checks both configs, and
  `VideoAnalyzeStepExecutor` calls `ValidateSourceInputProfiles` at run time: an unknown id or too
  many entries fails the step with `CONFIG_INVALID`.
- **Compile.** `VideoCompileStepExecutor` resolves each source once: HDR ignores an explicit profile
  and records `camera_profile_ignored_hdr`; otherwise `SourceCameraProfile.Pick` returns the explicit
  id or the High- confidence auto result. Applied conversions are reported in
  `meta.inputProfiles: [{ sourceIndex, profile, source: "explicit"|"auto" }]`, present only when
  something applied. The source is converted to Rec.709 at the start of its picture chain and the
  output is tagged BT.709.
- **Analyze.** `VideoAnalyzeStepExecutor` converts the analysis copy first, so scene, visual and
  colour facts are measured on corrected pixels; it records the same `meta.inputProfiles` and the HDR
  warning. When a resolved profile does not reach the copy — a still source, authored scenes, a step
  that reads the source's own pixels (insert regions, sharpness, object tracking) or a copy that could
  not be made — it records warning `camera_profile_not_applied` with a plain `reason` instead of
  silently analysing uncorrected pixels.
- **Cache.** A step whose config sets `SourceInputProfiles` folds an `InputProfileRecipes` component
  into its workflow step-cache key: the stage recipe version, `CameraProfileTable.TableVersion`, the
  map itself, and `CameraProfileDetector.RuleVersion` when any value is `auto`
  (`SourceCameraProfile.CacheKeyComponent`). A formula, table or rule change therefore cannot replay
  a stale result. Without `SourceInputProfiles` the component is null and the key is unchanged; the
  full compile/analyze config JSON is still part of the key, so adding or changing the map re-keys
  the step. Within a timeline render, only a region with a profile clip changes its region hash
  (above).
- **Grade helper and proposals.** Proposal facts use **only the clip's own** `inputProfile`, never
  the source's detected hint, because the facts describe what renders: a clip with no profile is
  reported as uncorrected even when its source detects High (including a user who turned it off).
  A profiled clip is prefixed with the fact word
  `log-converted footage (<id>): already corrected to standard colour` (`EditProposal.cs`), and the
  edit-proposal prompt says "Footage marked as log-converted is already corrected to standard colour,
  so do not add contrast or saturation to fix a washed-out look" (`AgentEditProposalModel.cs`).
- **Seeders and old uploads.** `EditTimelineSeeder` and `LineageSeeder` accept an `inputProfileFor`
  resolver; the controllers pass `StoredMediaProbes.InputProfileResolver`, which returns a profile
  only for a **High** stored-probe hint. A new timeline therefore starts with the field already set
  where the detector is sure. A probe stored before the colour facts existed has no make/model or bit
  depth, so detection for that upload is unknown until its derivatives run again; the next run
  replaces the stale probe, and nothing re-probes in bulk (`MediaDerivativeDispatcher.cs`).
- **No new assistant or MCP tools.** The feature is entirely a document field, render stage and
  workflow config; the assistant chat surface stays at **113 tools** and the MCP catalogue at **75**
  (`AssistantAgentToolScopeTests`, `AssistantMcpCatalogTests`).

## Known gaps and what to measure with real clips (pending)

- **No profile is calibrated.** Every profile except D-Log M is `uncalibrated`: the formula is the
  maker's, but it has not been checked against real footage from that camera. D-Log M is
  `approximate` by design (no public DJI specification).
- **Tag signatures per camera.** Detection uses make/model families plus broad colour facts, not
  per-camera probe signatures. Rules should be tuned once real clips show what each model writes.
- **Full versus legal range per maker.** `ExpectedRange` is data per profile, and several entries
  note that the range is unconfirmed (Apple, F-Log2, Nikon, Log3G10). Measuring real clips will
  settle them.
- **Banding.** The baked converted proxy is the mitigation; the residual quality of the 8-bit proxy
  path and the tetrahedral/trilinear difference still need measurement on real footage.
- **DJI D-Log M curve.** The D-Log stand-in can be off; no public D-Log M specification exists to
  implement.
- **Samsung Log.** A specification exists behind a Samsung developer login and is not implemented, so
  Samsung Log is not in the catalogue.
- **Filmstrip and timeline thumbnails stay flat** because they read the plain proxy (above).
- **Auto-apply does not remember a per-file "none".** Turning the profile off on one clip is not
  remembered for the file: a new clip from the same file is converted again when its probe is High
  (`auto-profile.ts`). The "Converted automatically" badge is also session-only (above).
- **The reference wipe compares unlike pictures.** While a viewing LUT is on, the program side is
  LUT-viewed but the reference still is the clean picture, so a wipe between them mismatches
  (`ProgramViewer.tsx`).
- **Clips inside compound clips use the GPU input stage.** The converted-proxy request walks
  top-level tracks only, so a clip nested in a compound never asks for a converted proxy and falls
  back to the browser's GL conversion (`proxy-urls.ts`).
- **No cap on converted proxies.** A source can accumulate one converted proxy per requested profile
  (up to 12 variants) without a quota.
