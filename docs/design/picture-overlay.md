# Pictures and videos over the film

**Status:** SPEC — initiative `picture-overlay`, planning lane only. Nothing here is built until the
Owner answers Q1–Q3 through the operator. Branch `picture-overlay/plan`, based on
`origin/master` `a1e6a19b`. Owner: program lead.

**Companions:** bead `docs/video-editing.md` (behaviour of `VideoCompile`, tracked screen inserts,
insert content, motion graphics); `docs/design/workflow-apps.md` (run brief, app manifest, linter);
`docs/run-brief.md` (wire shape); `docs/design/assistant-approvals.md` (approval cards);
`docs/metering-and-billing.md` (what is priced).

**Citations.** `path:line` is where the symbol was on the branch above. Every claim names the symbol
so it can be found again after line drift.

---

## 1. What the trigger run teaches

Alessandro uploaded a testimonial screenshot and a talking-head clip. The footage run analysed the
clip, cut it, and returned a trimmed talking head. The picture was never shown.

The gap is structural, not a bug:

- The compile can place content only **inside a tracked chroma plate** (`InsertContents`,
  `InsertContentRef`) or **as a drawn graphic** in a motion-graphics band (`GraphicsPlan`,
  `OverlayPlacementBuilder`, `DrawtextFilterBuilder`). There is no stage that composites an
  arbitrary project image or video onto the program at a chosen rectangle and time.
  `docs/video-editing.md` "Explicitly not built" records exactly this: *no arbitrary unanalyzed
  asset/image insertion* and *no free-floating picture-in-picture*.
- The motion-graphics planner is wired only into `video-derush-edit-graphics` and its room variant
  (`WorkflowTemplateCatalog.cs:304`, `:481`); the plain footage template `video-derush-edit`
  (`WorkflowTemplateCatalog.cs:239`) has no graphics step at all.
- No agent is ever given a list of the project's pictures or videos. The bounded view carries only
  analysis-derived ids (`VideoAnalyzeStepExecutor.cs:5711` for `view.placements`; the media list
  would be a new node).
- The only way a project picture reaches a run today is the green-screen `insertContent` slot,
  which binds it to a **tracked plate** (`RunBriefOverrides.cs:42-59`,
  `WorkflowTemplateAppManifests.cs:96-108`). A testimonial screenshot is not a plate.

The trigger run therefore had no vocabulary in which "show this picture at this moment" could be
expressed, let alone rendered.

---

## 2. Decisions at a glance

| # | Question | Decision | Why |
|---|---|---|---|
| D1 | Picture forms | **Both**: a full-frame **card** and a **picture-in-picture** inset. The agent chooses per picture; the step config can pin either. | Owner, 2026-11-01. A screenshot wants the card; a logo, product clip or second angle wants the inset. |
| D2 | Who chooses | A new **`OverlayPlanner`** agent picks the media, the moment, the form, the anchor, the size and the opacity. A deterministic config path exists for the assistant and the Advanced editor. | Separates graphic taste from the cut decision (which is already a hard job) and works in the plain footage path, where the motion-graphics planner is absent. |
| D3 | Placement vocabulary | Nine **edge anchors** (`TopLeft` … `BottomRight`), three **sizes** (`Small`/`Medium`/`Large`), a server-owned **safe margin**. Never a pixel, never a fraction from the model. | Same offered-id discipline as `MotionGraphicsPlanOutput`; a model cannot invent geometry. |
| D4 | Opacity | Integer **0–100 %** on the plan, clamped and validated server-side; `0` means "not drawn" and is reported. | The owner asked for 0–100 %. Opacity carries no time or space meaning, so the rushcut invariant is extended once, explicitly (D-10). |
| D5 | Picture source | Only **offered** media: the files the user puts in the new `overlay` slot, else the project's `image/*`+`video/*` files (capped). A rendered step output is allowed only through the existing, prefix-checked `InsertContentRef{from:Step}`. | No model may invent a picture; no new file-egress path. |
| D6 | Video overlay audio | **Both `Muted` and `Mixed` in v1, agent-chosen**, at a bounded level (Quiet −12 dB / Balanced −6 dB / Prominent 0 dB, clamped to −18..0). | Owner, 2026-11-01. The model may bring a demo's sound in; the bound keeps it under the film's voice. |
| D7 | Looping and end | Content shorter than the span **loops** by default; `PlayOnce` stops contributing at its own end (`eof_action=pass`). The overlay always stops at the span end; content longer than the span is trimmed. | Predictable: a short demo fills its window; a short logo does not freeze; nothing outlives its window. |
| D8 | Render | Deterministic ffmpeg only, in `VideoCompile`, two new builders, one stage per encoder. No model at render time. | The existing rule; see `docs/video-editing.md` "Why ffmpeg runs in the WorkflowEngine container". |
| D9 | Screen inserts | **Untouched.** A separate config member and a separate filter stage; `InsertContents`/`InsertContentRef` keep their exact meaning and their tests. | The brief's hard constraint. |
| D10 | Invariant | `MediaOverlayPlanOutput` carries no `double`, `DateTime`, rectangle or pixel. The single `int OpacityPercent` (0–100) is a documented exemption. | Everything the model says must be an opaque id or a bounded render constant. |
| D11 | Off means off | Null config and null plan add no filter, no `-i`, no label, and no cache-key change. `DeterministicStepRevisions` for `VideoCompile` gets `+media-overlays-1`. | Byte-identical when off; the stored config string is the cache input, so appending a nullable member does not move an old key. |
| D12 | Failure policy | A **user/assistant-configured** overlay that cannot resolve **fails the compile** with a named code. An **agent-planned** overlay that names an unoffered id or a cut-away window is **dropped and reported**, never silent about the rest. | Same asymmetry as `EFFECT_TARGET_UNVERIFIED` (fail closed for an explicit request) versus a graphics plan (degrade a model miss). |
| D13 | Card audio | The agent chooses per card: **`Keep`** (the film's sound continues under the card) or **`Mute`** (the program audio ramps to silence for the card span). Default `Keep`. A user preference in the run brief **binds** the choice. | Owner, 2026-11-01. A testimonial card usually plays under the speaker; a silent card is sometimes wanted. |
| D14 | Media offers | A user-named file set **binds** the offers (only those files are offered). With no user choice, **every usable project picture/video** is offered, with size and aspect facts. | Owner, 2026-11-01. The agent may choose freely, but never invents a file. |

---

## 3. What exists and is reused

| Existing mechanism | Where | How this feature uses it |
|---|---|---|
| `VideoCompileStepConfig` append-only positional record | `VideoCompileStepConfig.cs:1-869` | New members appended **last**. Null defaults keep every stored payload and call site. |
| `InsertContentRef` (`ProjectFile`/`Step`/`StepResult`) | `VideoCompileStepConfig.cs:1052-1088` | Reused **unchanged** as the content reference. Its `Validate()` and the save-time resolver are the authority for "is this a file of this project / a safe earlier render". |
| `InsertContentRefResolver` + `StepResultSourceResolver` | `Services/Video/InsertContentRefResolver.cs:33-88`, `StepResultSourceResolver` | Resolves project files and earlier renders; enforces `projects/{projectId}/` prefix, no `..`, image/video MIME, `Completed` status. No new resolver. |
| `InsertContentFitFilter.Build` | `ScreenInsertFilterBuilder.cs:144-230` | Not used for the inset box (the box is derived from the content's own aspect, so there is no letterbox to pad). The **card** uses the same `scale…force_original_aspect_ratio=decrease` + centred `pad` arithmetic it documents. |
| `CanvasFitGeometry.For` and `VideoOutputFit` | `Editing/CanvasFitGeometry.cs:6-43`, `VideoCompileStepConfig.cs:1134-1147` | Reference implementation for contain/cover arithmetic; the card's contain fit and the inset's aspect box mirror it. |
| `OverlayAssetFilterBuilder` | `OverlayAssetFilterBuilder.cs:27-110` | The closest existing primitive: `format=rgba,scale,setpts` + `overlay=x:y:enable='between(t,…)':eof_action=pass`. The new builder follows the same recipe, adds opacity, border, looping and two forms. It **does not replace** the rendered-asset path. |
| `OutputPolicyFilterBuilder` watermark | `OutputPolicyFilterBuilder.cs:131-141` | Proves the rgba-at-a-corner composite and the alpha-negotiation pin (`format=rgba` before `scale`). Reused as a pattern, not as code. |
| `MapSourceWindowToOutput` / `OutputTimeline` | `VideoCompileStepExecutor.cs:3860-3900` | Maps an offered source window through the cut onto the output clock, clipped to the first kept portion. Reused for overlay windows. |
| `OverlayPlacementBuilder.ChooseTimeWindow` / `SplitIntoTimeSlices` | `OverlayPlacementBuilder.cs:120-175` | The deterministic still-window / shot-middle / time-slice logic for offered windows. Extracted to one shared helper, not copied. |
| `ScreenInsertFilterBuilder` / `CensorFilterBuilder` / `CaptionFilterBuilder` | `Services/Video/` | Untouched; the new stage is inserted between censor and the program tail. |
| `RunBriefFileRole` and the app manifest | `RunBrief.cs:16-28`, `WorkflowTemplateAppManifests.cs:49-60` | New appended role `overlay`, surfaced as a `files` app input exactly like `insertContent`. |
| `AssistantGreenScreenTools` | `Agents/Tools/AssistantGreenScreenTools.cs:1-120` | The template for the new assistant pair: propose checks the content, apply writes the compile step config, a token binds the action. |
| `MotionGraphicsPlanOutput` / `VideoAnalysisArtifact` offered-id lists | `OutputSchemas.cs:570-582`, `VideoAnalysisArtifact.cs:42-118` | The offered-id discipline and the separate-namespace rule are copied, not the types. |

**What must not change**

- `InsertContents`, `InsertContentProjectFileId`, `InsertContentRef` semantics, scoring and drop
  reasons. A picture overlay is a **different** config member and a **different** filter stage.
- The green-screen template (`green-screen-insert`) and its assistant pair. Their tests stay green
  without edits.
- The motion-graphics band vocabulary (`p{n}`, `LowerThird`/`UpperThird`/`CenterBand`) and
  `DrawtextFilterBuilder` geometry.
- The tracked-insert servoing (`ScreenInsertFilterBuilder`, mesh, matte, reflections).

---

## 4. Options, cost and risk

### Option A — Config only, no agent

The app slot and the assistant write a fixed `MediaOverlays` list (content, form, span, anchor,
size, opacity). No planner, no offered windows.

- **Cost:** 3–4 tickets (config, filter builder, compile wiring, UI/assistant).
- **Risk:** low technical risk; fails the brief's core ask ("the agents choose which picture/video,
  the span, the placement and the opacity"). Alessandro could only get the picture by editing the
  step config by hand.

### Option B — Dedicated `OverlayPlanner` with offered media and windows (recommended)

`VideoAnalyze` offers `m{n}` media ids and `o{n}` moments; a new agent picks from them and emits
`MediaOverlayPlanOutput`; the compile resolves both the plan and the deterministic list.

- **Cost:** ~9 tickets (analysis offers, planner agent + prompts + schema, filter builder, compile,
  templates, linter, assistant, web, e2e).
- **Risk:** medium. The model may choose a poor moment or a legible-size violation; the offered-id
  set, the safe margin, the drop reporting and the deterministic fallback bound it. It does not
  touch the cut decision.
- **Why recommended:** it satisfies "agent-chosen" in the path the trigger run actually uses
  (`video-derush-edit`), keeps the graphic taste separate from the cut, and reuses the
  offered-id/repair-turn machinery already proven for motion graphics.

### Option C — Extend the cut decision (`VideoEditDecisionOutput`)

The story editor / edit-room director emits the media overlays alongside the kept spans.

- **Cost:** 5–6 tickets. No new agent, no template renumbering.
- **Risk:** medium-high. `VideoEditDecisionOutput` is the most load-bearing model contract in the
  product; adding media choice and graphic taste to it risks the cut quality that every footage run
  depends on. It also gives the overlay decision to an agent with no visual vocabulary for
  placement beyond the shot list.

**Recommendation: Option B.** The extra agent is small (read-only tools, one schema, one prompt)
and its failure is contained to "no overlay applied", whereas Option C's failure is "the cut got
worse". Option A is the fallback if the owner wants the picture shown without agent choice.

---

## 5. The feature model

### 5.1 Step config (exact shape)

Appended **last** to `VideoCompileStepConfig` (positional record; every existing payload keeps
deserializing):

```csharp
// -- Pictures and videos over the film (initiative "picture-overlay"). Appended LAST. --
/// <summary>
/// Fixed overlays a human or the assistant configured. Resolved at compile time; each entry that
/// cannot resolve FAILS the compile with a named code (never a silent skip). Null/empty (default)
/// is byte-identical.
/// </summary>
IReadOnlyList<VideoMediaOverlay>? MediaOverlays = null,
/// <summary>
/// The step whose <c>MediaOverlayPlanOutput</c> to apply (an OverlayPlanner agent step). From is
/// Previous or Step, and the step must be earlier (same rule as GraphicsPlan). Null (default) is
/// byte-identical.
/// </summary>
ExtractInputRef? MediaOverlayPlan = null,
/// <summary>Cap on resolved overlays from both sources together. Default 8, clamped 1..16.</summary>
int MaxMediaOverlays = 8,
/// <summary>Fraction of the canvas kept clear around a picture-in-picture inset (0..0.15). Default 0.04.</summary>
double MediaOverlaySafeMargin = 0.04,
/// <summary>
/// The user's binding choice for a card's sound. Null (default) lets the planner choose;
/// Keep or Mute overrides every card's choice. From the run brief's overlayCardAudio.
/// </summary>
MediaOverlayCardAudio? MediaOverlayCardAudioDefault = null,
```

```csharp
/// <summary>One configured picture or video over the film.</summary>
public sealed record VideoMediaOverlay(
    InsertContentRef Content,
    MediaOverlayForm Form = MediaOverlayForm.PictureInPicture,
    MediaOverlayAnchor Anchor = MediaOverlayAnchor.BottomRight,
    MediaOverlaySize Size = MediaOverlaySize.Medium,
    int OpacityPercent = 100,
    MediaOverlayBorder Border = MediaOverlayBorder.None,
    /// <summary>Output-timeline start in seconds; null = the first frame of the film.</summary>
    double? StartSec = null,
    /// <summary>Output-timeline end in seconds; null = the last frame of the film.</summary>
    double? EndSec = null,
    MediaOverlayLoop Loop = MediaOverlayLoop.Loop,
    MediaOverlayAudio Audio = MediaOverlayAudio.Muted,
    /// <summary>Mixed audio only: the overlay's bounded level.</summary>
    MediaOverlayVolume Volume = MediaOverlayVolume.Balanced,
    /// <summary>A card's sound: Keep (default) or Mute.</summary>
    MediaOverlayCardAudio CardAudio = MediaOverlayCardAudio.Keep);
```

Enums (all append-only, persisted by name):

```csharp
public enum MediaOverlayForm { PictureInPicture, Card }
public enum MediaOverlayAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }
public enum MediaOverlaySize { Small, Medium, Large }
public enum MediaOverlayBorder { None, Light, Dark }
public enum MediaOverlayLoop { Loop, PlayOnce }
public enum MediaOverlayAudio { Muted, Mixed }
public enum MediaOverlayVolume { Quiet, Balanced, Prominent }   // -12 / -6 / 0 dB, clamped to -18..0
public enum MediaOverlayCardAudio { Keep, Mute }
```

Size is a fraction of the **safe width** (`canvasWidth * (1 - 2*margin)`):

| Size | Inset width | At 1920×1080, margin 0.04 |
|---|---|---|
| `Small` | 0.25 | ~442 px |
| `Medium` | 0.36 | ~637 px |
| `Large` | 0.50 | ~884 px |

The inset height follows the content's own aspect; if it would exceed the safe height, the height
is clamped and the width follows it. The inset therefore **always matches the content's aspect** —
no surprise letterboxing inside it.

`Validate()` (pure, no media) returns one message per error, alongside the existing compile checks
(`VideoCompileStepConfig.cs:877`):

- `MediaOverlayPlan` non-null requires `Mode = Reencode` (`MEDIA_OVERLAY_REQUIRES_REENCODE`).
- Any `MediaOverlays` entry requires `Mode = Reencode`.
- `Content.Validate()` null; a malformed ref is `MEDIA_OVERLAY_REF_INVALID`.
- `OpacityPercent` outside 0..100 is `MEDIA_OVERLAY_OPACITY_INVALID`.
- `StartSec`/`EndSec` non-finite, negative, `EndSec <= StartSec`, or past `VideoSourceRef.MaxWindowSec`
  is `MEDIA_OVERLAY_SPAN_INVALID`. `EndSec` without `StartSec` is allowed (start at 0).
- A `Card` with an `EndSec` null is allowed (card to the end) but the linter warns for a card longer
  than 25 % of a known film length or 30 s (`WL025`).
- `MediaOverlays` + `MediaOverlayPlan` together are allowed; config entries resolve first and win a
  duplicate `(content, span)` pair; the combined list is capped by `MaxMediaOverlays`.

`EncodeOnceBlockers()` (`VideoCompileStepConfig.cs:905`) gains `"pictures and videos over the film"`
when `MediaOverlays` or `MediaOverlayPlan` is set — the review cannot judge an overlay from a plan
that never encoded it.

`StepCatalog`'s `[StepCard]` for `VideoCompile` gains a needs line ("Pictures or videos you want
shown, and the moment"), and `docs/assistant-kb/step-cards.md` is regenerated.

### 5.2 The plan (agent output)

New schema in `OutputSchemas.cs`, next to `MotionGraphicsPlanOutput`:

```csharp
public class MediaOverlayPlanOutput
{
    public List<MediaOverlayChoice> Overlays { get; set; } = new();
    public string PlanRationale { get; set; } = string.Empty;
}

public class MediaOverlayChoice
{
    public string MediaId { get; set; } = string.Empty;    // m{n}, offered
    public string WindowId { get; set; } = string.Empty;   // o{n}, offered
    public string Form { get; set; } = string.Empty;       // Card | PictureInPicture
    public string Anchor { get; set; } = string.Empty;     // the nine words, case-insensitive
    public string Size { get; set; } = string.Empty;       // Small | Medium | Large
    public int OpacityPercent { get; set; } = 100;         // 0..100, the one allowed number
    public string Border { get; set; } = string.Empty;     // None | Light | Dark
    public string Audio { get; set; } = string.Empty;      // Muted | Mixed
    public string AudioLevel { get; set; } = string.Empty; // Quiet | Balanced | Prominent
    public string CardAudio { get; set; } = string.Empty;  // Keep | Mute
    public string Reason { get; set; } = string.Empty;
}
```

**The invariant.** A new `MediaOverlayPlanOutputInvariantTests` reflects the reachable types and
bans `double`/`float`/`decimal`/`DateTime`/`TimeSpan`/`VideoAnalysisRect`/any coordinate-bearing
type. `OpacityPercent` is the single documented exemption: it is a bounded render constant with no
temporal or spatial meaning, and the compile clamps it regardless. Unknown words degrade to the
defaults (PiP / BottomRight / Medium / None / Muted / Balanced / Keep) and are reported; an unknown
or duplicate `MediaId` or `WindowId` drops that overlay with a reason. `CardAudio` applies only to a
card and `Audio`/`AudioLevel` only to a video; the inapplicable fields are ignored.

### 5.3 Offered ids

`VideoAnalysisArtifact` gains a media namespace (`m{n}`) and a window namespace (`o{n}`), each with
its own offered list — exactly the rule that already governs `p{n}`/`r{n}`/`m{n}`/`x{n}`
(`VideoAnalysisArtifact.cs:42-118`):

```csharp
IReadOnlyList<VideoAnalysisMedia>? Media = null,
IReadOnlyList<VideoAnalysisOverlayWindow>? OverlayWindows = null,
IReadOnlyList<string>? OfferedMediaIds = null,
IReadOnlyList<string>? OfferedOverlayWindowIds = null,

public sealed record VideoAnalysisMedia(
    string Id, Guid ProjectFileId, string Name, string Kind,   // Image | Video
    int? Width, int? Height, double? Aspect, string? Orientation,
    double? DurationSec, long SizeBytes, bool FromUser);

public sealed record VideoAnalysisOverlayWindow(
    string Id, string ShotId, int SourceIndex,
    double StartSec, double EndSec, double Suitability);
```

`VideoAnalyzeStepConfig` gains (appended, default off = byte-identical):

```csharp
bool EmitOverlayOffers = false,
int MaxOverlayWindows = 24,
int MaxOverlayMedia = 20,
```

**Media offers.** When `EmitOverlayOffers` is true, the analyze step snapshots the media the run may
use:

1. If the brief carries files for `RunBriefFileRole.Overlay` (new, §8), **only those files**, in the
   brief's order. The user's named set binds the offers; the planner may not reach outside it.
2. Else **every usable project** `image/*` and `video/*` file, ordered by `UploadedAt`, then `Id`.
   The planner chooses freely.

Quarantined files are excluded. The list is capped at `MaxOverlayMedia` (20). Each entry carries the
size and aspect facts the planner needs to choose between a card and a corner: `Width`, `Height`,
`Aspect` (width/height, null when unknown), `Orientation` (`Landscape`/`Portrait`/`Square`), and
`DurationSec` for a video. Images take their header size (`PictureSizes` in the API, the engine's
header reader); videos take the stored ingest probe where present. The snapshot stores the
`ProjectFileId` so the compile resolves against the artifact, not a later, shifted live list. This
is the "offered is the authority" rule: a file added after analysis is not offered to this run.

**Window offers.** For each shot with a `Visual`, reuse the shared still-window helper
(extracted from `OverlayPlacementBuilder.ChooseTimeWindow` / `SplitIntoTimeSlices`,
`OverlayPlacementBuilder.cs:120-175`): prefer the longest still window, else a 2.5 s window at the
shot middle, else the shot start; split a long window into up to 3 slices; clamp to the shot. Rank
by suitability (calm, low motion, no on-screen text), then cap at `MaxOverlayWindows`, then assign
`o0…oN` in deterministic shot/start order. The ids are contiguous and globally unique.

`view` gains two nodes (times are visible to the model for reasoning; the output schema cannot echo
them):

```json
"media": [{ "id": "m0", "name": "testimonial.png", "kind": "Image", "width": 1200, "height": 1500, "aspect": 0.8, "orientation": "Portrait", "durationSec": null, "fromUser": true }],
"overlayWindows": [{ "id": "o3", "shotId": "s2", "startSec": 11.8, "endSec": 17.4 }]
```

The compile maps an `o{n}` window through the cut with `MapSourceWindowToOutput`; a window wholly
cut away drops that overlay (`window_cut_away`).

### 5.4 Picture forms

**Card (full frame).** The picture fills the canvas for the span: scaled with
`force_original_aspect_ratio=decrease`, centred and padded to the canvas with black, then composited
at `0:0`. If the content's aspect equals the canvas there are no bars. The card is the right form for
a testimonial screenshot, a document excerpt or a pricing table — anything that must be read.

- The film's own picture is covered. **Card audio** is the agent's choice per card (D13):
  - `Keep` (default): the film's sound continues under the card, unchanged.
  - `Mute`: the program audio is ramped to silence for the card span, with a 50 ms linear ramp at
    each edge (`volume='if(between(t,s+0.05,e-0.05),0,…)' : eval=frame`). A card's own `Mixed` audio
    is added **after** the mute, so it still plays.
  - A user preference in the run brief (`overlayCardAudio: agent|keep|mute`) **binds** the choice:
    the compile's `MediaOverlayCardAudioDefault` overrides the plan's `CardAudio` when set.
- The program fade and the captions still apply: the tail stage runs after the overlay.
- A card is reported with `form: "Card"`, `containsBars: true|false`, `barPx`, `cardAudio`.

**Picture-in-picture.** The content is scaled to the inset box (§5.1), placed at the anchor inside
the safe rectangle, with an optional hairline border. The right form for a logo, product clip, a
second angle, or a "look at this" reference.

- Anchor + size, never a pixel.
- Border `Light`/`Dark` is a 2 px (at 1080p; `round(canvasHeight * 0.002)`, min 2) pad around the
  content, drawn into the overlay input so it scales with the content.
- Default opacity 100; lower only when the planner or the user asks.

### 5.5 Video overlays

- **Source.** A project video file, or a finished step render via `InsertContentRef{from:Step}`.
  "Only if safe" is already enforced by `InsertContentRefResolver` + `StepResultSourceResolver`:
  same project, `Completed`, addressable `outputFiles/` (or `agentFiles/generated/`) key, video
  extension. Anything else is `MEDIA_OVERLAY_CONTENT_UNUSABLE`.
- **Span.** Config: `StartSec`/`EndSec` on the **output** clock. Plan: the offered `o{n}` window on
  the **source** clock, mapped through the cut.
- **Size and placement.** Same anchor + size vocabulary as a picture. An inset video at `Large` is
  the "split screen" look; `Small` is a badge.
- **Opacity.** `colorchannelmixer=aa=<0..1>` after `format=rgba`; the audio is unaffected (muted).
- **Loop and end.** `Loop` (default): `-stream_loop -1` on the input, `trim=duration=<span>` in the
  chain, `setpts` so frame 0 lands at the span start. `PlayOnce`: no `-stream_loop`; `eof_action=pass`
  means the overlay stops contributing at its own end and the film shows through for the rest of the
  window; reported as `endedEarly: true` with the content's length. Content longer than the span is
  trimmed. The overlay never outlives its window.
- **Audio.** Both policies are v1 and agent-chosen (D6):
  - `Muted` (the default when the model says nothing): the overlay maps no audio.
  - `Mixed`: the overlay's audio is trimmed to the span, delayed to the span start, resampled to
    the program's rate/layout, and mixed at its **bounded level**: `Quiet` −12 dB, `Balanced` −6 dB,
    `Prominent` 0 dB, clamped to −18..0. It is **not** treated as dialogue, so the music bed's
    ducking is unchanged; the program `ProgramLoudness` stage runs after the mix, so the overlay is
    included in the final loudness normalization. A source with no audio degrades to `Muted` and is
    reported (`audio_missing`).
  - The plan carries `Audio` (`Muted|Mixed`) and `AudioLevel` (`Quiet|Balanced|Prominent`). A config
    entry pins both.
- **Frame rate.** The overlay input is normalized to the program's fps with `fps=<num>/<den>` before
  the composite, so a 24 fps inset in a 30 fps program does not judder.

### 5.6 Interaction with reframes, transitions and the program envelope

| Existing stage | Interaction |
|---|---|
| Keyframed reframes (`Reframes`) | Applied to the **source span** before the canvas fit. The overlay is canvas-space and is composited **after** the program is assembled, so it does not zoom or move with a reframe. This is the intended meaning: an overlay is a screen element, not part of the shot. |
| Seam transitions (`TransitionPolicy`) | Applied at seams during assembly, before the overlay stage. An overlay spanning a seam stays up through the transition (its window is on the output clock). |
| Program envelope (`ProgramFade*`, `TransitionPolicy` dips) | Applied **after** the overlay stage, so a fade-out fades the overlay too. An overlay never sits on a black screen after the fade. |
| Captions | Burned **after** the overlay, so captions are readable over a card. |
| Output policy (resolution cap, watermark) | Applied after captions, so the watermark is on top of everything. |
| Censor / region effects | Applied **before** the overlay. An overlay can cover a censored region; the overlay's own content went through ingest moderation and is not re-censored. Documented, not silently. |

---

## 6. Render

### 6.1 Two pure builders

`Services/Video/MediaOverlayFilterBuilder.cs`:

```csharp
public static class MediaOverlayFilterBuilder
{
    public static IReadOnlyList<string> BuildFilterChain(
        string baseLabel,
        IReadOnlyList<ResolvedMediaOverlay> overlays,
        int canvasWidth, int canvasHeight,
        Func<int, int> inputIndexForIndex,
        string finalLabel = "[vout]");
}
```

`ResolvedMediaOverlay` (first-party only):

```csharp
public sealed record ResolvedMediaOverlay(
    string Token,                 // mo0, mo1 … (diagnostics and the output node)
    MediaOverlayForm Form,
    int InputIndex,
    int BoxX, int BoxY, int BoxW, int BoxH,
    double Opacity,               // 0..1
    MediaOverlayBorder Border,
    double OutputStartSec, double OutputEndSec,
    bool EndsEarly,
    string? ContentName);
```

**Card recipe** (per overlay, two segments):

```
[n:v]format=rgba,fps=<fps>,scale=<cw>:<ch>:force_original_aspect_ratio=decrease,
      pad=<cw>:<ch>:(ow-iw)/2:(oh-ih)/2:black,colorchannelmixer=aa=<a>,
      setpts=PTS+<start>/TB[mo0]
[base][mo0]overlay=x=0:y=0:eof_action=pass:enable='between(t,<start>,<end>)'[vn]
```

**Inset recipe**:

```
[n:v]format=rgba,fps=<fps>,scale=<bw>:<bh>:force_original_aspect_ratio=decrease,
      pad=<bw+2b>:<bh+2b>:<b>:<b>:<borderColour>,colorchannelmixer=aa=<a>,
      setpts=PTS+<start>/TB[mo1]
[base][mo1]overlay=x=<bx-b>:y=<by-b>:eof_action=pass:enable='between(t,<start>,<end>)'[vn]
```

All numbers come from validated config, probed media and the safe-area arithmetic. No string comes
from a model.

### 6.2 Where the stage goes

There are two encoders (`VideoCompileStepExecutor.cs:9607` single-source, `:10275` segmented). The
stage is added to **both**, in the same logical place: **after the complete program picture is
assembled and censored, before the program fade and captions**.

- **Single-source** (`EncodeReencodeAsync`): after the censor stage (`:10025-10029`), before the tail
  (`:9909-9916`, appended `:10033-10034`). The tail carries the narration hold, the dip-cut and the
  program video fade, so all three apply to the overlay.
- **Segmented** (`EncodeReencodeSegmentedAsync`): after the generated-clip concat (`:10910-10957`)
  and before the program video fade (`:10964-10972`); captions and policy follow.

The builder consumes and returns labels, so the existing label-chaining convention is unchanged
(`[vcut]` → `[vins]` → `[vtxt]` → … → `[vout]`, `:9812-9836`; segmented `[vcat]`/`[vgc]`/`[vpre]`
chain, `:10442-10446`). With no overlays the helper is never called and every string is identical.

Input bookkeeping gains one `-i` per resolved overlay. The input index is assigned in the existing
single derived chain (`:10652-10673` segmented, `:9733-9835` single-source) **after** source clips
and **before** asset overlays, so the existing offsets do not move when the feature is off and move
deterministically when it is on.

### 6.3 Off is byte-identical; cache and re-render

- Null `MediaOverlays` and null `MediaOverlayPlan` → no builder call, no input, no label change.
- `StepCacheKeyInputs.VideoCompileConfigJson` is the **stored JSON string**
  (`WorkflowExecutorService.cs:2719`), not a re-serialization. Appending a nullable member therefore
  does not move an existing key. The API serializer must omit null members (as it does for the other
  appended members); a round-trip test pins this.
- `DeterministicStepRevisions` for `VideoCompile` (`Execution/Caching/DeterministicStepRevisions.cs:91`)
  gains `+media-overlays-1`. `VideoAnalyze` gains `+media-overlay-offers-1` when
  `EmitOverlayOffers` is on. No `SchemaVersion` bump.
- **Re-render cost.** A compile with an overlay re-encodes (already required by most features) and
  decodes one extra input. A 2 s inset video adds one decode stream; a still image decodes once.
  The exact compile-time delta is unmeasured (§14).

### 6.4 Output

The compile's result JSON gains:

```json
"mediaOverlays": {
  "enabled": true,
  "applied": true,
  "appliedCount": 2,
  "dropped": [{ "mediaId": "m3", "windowId": "o9", "reason": "window_cut_away" }],
  "offered": [{ "mediaId": "m0", "name": "testimonial.png" }],
  "unusedOffered": [{ "mediaId": "m1", "name": "logo.png" }],
  "items": [
    { "media": "testimonial.png", "form": "Card", "startSec": 1.2, "endSec": 6.4, "opacityPercent": 100,
      "containsBars": false, "endedEarly": false, "cardAudio": "Keep" },
    { "media": "demo.mp4", "form": "PictureInPicture", "anchor": "BottomRight", "size": "Medium",
      "box": { "x": 1128, "y": 590, "w": 637, "h": 360 }, "startSec": 7.0, "endSec": 13.0,
      "opacityPercent": 80, "border": "Light", "endedEarly": true, "audio": "Mixed", "audioLevel": "Balanced" }
  ]
}
```

`unusedOffered` is what makes the run honest when the user put three pictures in the slot and the
planner used one.

### 6.5 Failure codes

| Code | Class | When |
|---|---|---|
| `MEDIA_OVERLAY_REQUIRES_REENCODE` | `UserInput` (config) | The compile is `StreamCopy`. |
| `MEDIA_OVERLAY_REF_INVALID` | `UserInput` | A configured `InsertContentRef` is malformed. |
| `MEDIA_OVERLAY_CONTENT_NOT_FOUND` | `UserInput` | The configured file/step result is missing, foreign, not finished or not readable. |
| `MEDIA_OVERLAY_CONTENT_UNUSABLE` | `UserInput` | Not an image/video, quarantined, or unprobeable. |
| `MEDIA_OVERLAY_SPAN_INVALID` | `UserInput` | Non-finite, negative, reversed, or entirely outside the film. |
| `MEDIA_OVERLAY_OPACITY_INVALID` | `UserInput` | Outside 0..100 in config. |
| `MEDIA_OVERLAY_LIMIT_EXCEEDED` | `UserInput` | More than `MaxMediaOverlays` resolved entries. |

Agent-planned misses are **not** failures: unknown `mediaId`/`windowId`, a cut-away window, an
unusable media entry, or `opacityPercent: 0` drop that overlay and are listed in `dropped[]`. The
compile still succeeds.

---

## 7. The `OverlayPlanner` agent

### 7.1 Definition

- `AgentType.OverlayPlanner` — **appended** to the enum (the enum is persisted by name).
- `OverlayPlannerAgent : ReelBoltAgentBase`, `outputSchemaType: typeof(MediaOverlayPlanOutput)`,
  temperature 0.3, reasoning effort `low`.
- Tools: `ToolGroup.ProjectRead`, `ToolGroup.WorkflowControl` only. **No sandbox, no render, no
  write.** It reads the analysis view and picks ids; it cannot author media. (Contrast:
  `MotionGraphicsPlanner` has sandbox/render because it authors a rendered graphic; this agent
  authors nothing.)
- `AgentInputContextMode: FullWorkflow`, so it sees the analyze step's `view` (the same mode the
  graphics planner uses in `video-derush-edit-graphics`, `WorkflowTemplateCatalog.cs:328-335`).
- `CacheMode: Never` (the plan depends on the run's offered media, not a reusable prompt).

### 7.2 Prompt

Two byte-identical copies, exactly like every built-in prompt:

1. `OverlayPlannerAgent.DefaultPrompt` (runtime fallback).
2. `DatabaseSeeder.BuiltInAgents[AgentType.OverlayPlanner].SystemPrompt`.

A `MediaOverlayPlannerPromptConsistencyTests` mirrors
`VideoStoryEditorPromptConsistencyTests.cs:37-41` and asserts byte equality. The prompt must contain
the `[[BLOCKS:BEGIN]]…[[BLOCKS:END]]` list exactly once (`BlockPromptListTests.cs:68-84`).

Plain-language instructions (customer-voice, no jargon):

1. Pick only from `view.media` (`m{n}`) and `view.overlayWindows` (`o{n}`). Never invent a file, a
   name or a moment.
2. A screenshot, document, review or price table is a **card**: it must be readable, so use
   `Card`. A logo, product clip, second angle or a "look at this" reference is a picture-in-picture.
3. Put a picture-in-picture in a corner **away from the speaker's face and any on-screen text**;
   prefer the calmest offered window.
4. Keep opacity at 100 for anything that must be read. Lower it only when the picture is decoration.
5. Use at most one card per film unless the brief asks for more. Empty is a valid answer: if the
   media does not belong in this film, show nothing and say why.
6. A card's sound: `Keep` unless the user asked for silence or the picture carries its own sound
   that should be heard; then `Mute`. A video's sound: `Muted` unless the clip's own sound matters;
   then `Mixed` at `Balanced` (or `Quiet` when it is background).
7. Write `reason` in plain words: what it shows and why there.

### 7.3 Model-side validation (the agent step)

`AgentStepExecutor` already validates the answer against `OutputSchemaType` with one no-tools repair
turn then a hard fail (`AgentStepExecutor.cs:354-415`). The `MediaOverlayPlanOutput` schema is
structured; no extra model call. The **compile** is the authority on offered ids (§7.4), not the
agent's self-report.

### 7.4 Compile-side resolution

`ResolveMediaOverlaysAsync` (new, next to `ResolveGraphicsAsync` and `ResolveInsertsAsync`):

1. Resolve `MediaOverlayPlan` to JSON (Previous/Step only; unresolvable → `reason`, no overlays,
   never a failed compile — the graphics precedent).
2. Deserialize with `DeserializeWithQuoteFallback`; `overlays: null` → `[]`.
3. For each choice: `MediaId` must be in `artifact.OfferedMediaIds`; `WindowId` in
   `artifact.OfferedOverlayWindowIds`. Unknown → drop with `unknown_media_id`/`unknown_window_id`.
4. Map the window through the cut; wholly cut away → `window_cut_away`; partially cut → clip to the
   first kept portion (the existing `MapSourceWindowToOutput` rule).
5. Resolve the media snapshot's `ProjectFileId` **in this project** (tenant re-check), require
   image/video, download to scratch, probe. Failure → `content_unusable` (drop).
6. Build the resolved overlay; cap `MaxMediaOverlays`; append after the config entries.

Config overlays resolve first (§5.1) and fail hard (§6.5). Both lists share the cap.

---

## 8. App slot, run brief and UI

### 8.1 The brief role

`RunBriefFileRole` gains **`Overlay`** (appended, `RunBrief.cs:16-28`). Wire spelling `overlay`.
`RunBriefValidator` rules: at most 4 files; each must be `image/*` or `video/*`; the file must
belong to the run's project and not be quarantined (same as `InsertContent`,
`RunBriefValidator.cs:109-118`). `RunBriefSplitter` gains the same media check.

Unlike `InsertContent`, the role does **not** write a step config. It feeds the analyze step's media
offers (§5.3) and **binds** them: when the user names files, only those are offered. With no named
files, the analyze step offers every usable project picture/video and the agent chooses freely. A
user who wants a guaranteed overlay uses the assistant tool or the Advanced editor.

The brief also gains an optional **`OverlayCardAudio`** (`agent|keep|mute`). `RunBriefOverrides`
maps it to `VideoCompileStepConfig.MediaOverlayCardAudioDefault`: `agent` leaves the planner's
per-card choice alone, `keep`/`mute` overrides it. It is one closed string field, append-only like
every other brief field.

### 8.2 The app slot

- `AppManifestVocabulary.FileRoles` gains `overlay` (`AppManifest.cs:93`).
- `AppStepFacts` gains `TakesOverlayFile`; `BriefFieldConsumers.AcceptsFileRole("overlay")` is true
  when a `VideoAnalyze` step has `EmitOverlayOffers` (`BriefFieldConsumers.cs:120-142`).
- `AppManifestDeriver.Derive` adds a `files` input `{ role: "overlay", label: "A picture or video to
  show", min: 0, max: 4 }` when accepted (`AppManifestDeriver.cs:27-49`).
- The curated `Footage(...)` manifest factory (`WorkflowTemplateAppManifests.cs:49-60`) gains an
  `overlay: true` parameter; the base `video-derush-edit` manifest sets it. Other footage templates
  can opt in without a new template.
- When `overlay: true`, the manifest also exposes the **`overlayCardAudio`** brief field as a select
  ("Under a full-screen picture: let the agent choose / keep the sound / mute the sound"), default
  `agent`.
- Client mirrors: `AppFileRole` (`web/lib/types/workflow.ts:1645`), `run-model.ts` role
  nouns/upload accept/`fileHasRole`/`wireRole` (`:55-133`), `FileSlot.tsx` accept list. Messages
  `en`/`it` (`web/messages/*/apps.json`).

The slot is optional. Empty = the project's own pictures and videos are offered and the planner
chooses; a named set binds the offers and the planner chooses from it.

### 8.3 The run card

A new `MediaOverlaysCard` on the run page renders the compile's `mediaOverlays` node:

- One row per applied overlay: source name, **"Full screen"** or **"Corner (bottom right)"**, the
  time range, opacity when below 100, border when set, "sound kept"/"sound muted" for a card and
  "sound at Quiet/Balanced/Prominent" for a mixed video.
- A quiet "Not shown" list from `unusedOffered` and `dropped[]` with plain reasons ("the moment was
  cut out" / "that picture could not be read").
- Phone: stacked rows; every text ≥ 12 px (`no-small-fonts.test.ts`).
- No storage keys, ids or pixel boxes in the customer surface.

### 8.4 Where the moment is picked

- **Agent path:** the planner picks an offered window. The user can say "show it at 0:12" in the
  brief notes; the agent picks the nearest offered window. The offered windows are dense enough
  (per shot, split into up to three slices) for a named moment to land close.
- **Human path:** the Advanced editor edits the compile JSON (`startSec`/`endSec` on the output
  clock), and the assistant pair can set a precise span. The app slot deliberately does not carry a
  numeric pair (it would be the first numeric field in the run brief and a second timing vocabulary
  beside the windows).

### 8.5 Templates: the base edit and one curated testimonial recipe

**The base `video-derush-edit` template** gains the planner as designed: analyze with
`emitOverlayOffers:true`, story editor, `OverlayPlanner`, the Plan compile with
`mediaOverlayPlan`, the review loop back to the story editor, and the Final compile with the same
`mediaOverlayPlan` (the Plan stage resolves and reports the overlays so the review can judge them;
the Final stage renders them). Its later steps are renumbered and every explicit step-order
reference moves with them.

**One new curated recipe**, `testimonial-reel` — *“Testimonial reel from a talking-head clip and a
screenshot”* — is the owner's second scope decision (2026-11-01). It ships pre-wired for the trigger
run:

- **Key:** `testimonial-reel`. **Name:** "Testimonial reel from a talking-head clip and a
  screenshot". **Description:** "Upload a talking-head clip and a screenshot — a review, a message,
  a result. We cut the clip and show the screenshot as a full-screen card or a corner inset at the
  moment the agent chooses." Plain words; no internal jargon.
- **`AutoCreateOnProject: false`, `RequiresUserInput: true`.**
- **Steps:** (1) `VideoAnalyze` with `emitOverlayOffers:true` and an open `ProjectFile` source;
  (2) `VideoStoryEditor` (`PreviousStepOnly`, `Never`); (3) `OverlayPlanner` (`FullWorkflow`,
  `Never`); (4) `VideoCompile` Plan (`decision: Step 2`, `analysisStepOrder: 1`, `mediaOverlayPlan:
  Step 3`, `encodeStage: Plan`); (5) `ReviewLoop` back to step 2 (`MaxIterations: 3`, `MinScore: 8`);
  (6) `VideoCompile` Final (`decision: Step 2`, `analysisStepOrder: 1`, `mediaOverlayPlan: Step 3`,
  `encodeStage: Final`).
- **App manifest:** `video` slot (min 1), `overlay` slot (min 0, max 4, "A picture or video to
  show"), `overlayCardAudio` select (`agent|keep|mute`), source language, length, shape,
  resolution, style, and notes ("What is the clip about, and what should the picture say?").
- **Gallery category:** `footage`.

**Other footage variants stay opt-in** through the `AddMediaOverlay` recipe patch; the step is
**not** added to every template. This keeps the change to one template's renumbering plus a new
template, not a sweep of the whole family.

The curated recipe is where the trigger run lands: a talking head plus a testimonial screenshot,
with the card/PiP decision and the card sound left to the agent unless the user pins them.

---

## 9. Assistant and MCP

One new Propose/Apply pair, modelled on `AssistantGreenScreenTools`:

| Tool | Does |
|---|---|
| `ProposeSetMediaOverlay` | Checks the project Edit authority, the media file (image/video, same project, not quarantined), the target workflow/compile step, the form, the span, the opacity, the card-sound choice and the video audio/level. Prices nothing (a compile is already priced). Returns `IConfirmationTokenService` token over `{operation, canonicalPayload, userId}`. |
| `ApplySetMediaOverlay` | Verifies the token, re-reads the step, writes `MediaOverlays` through the normal `StepConfigSaveValidator`/`WorkflowStepReplacer` path. No run starts. |

- Approval catalog entry: `AssistantActionKind.Workflow`, `AssistantActionRisk.Normal`, `"Saved."`,
  `"Show a picture on the video"` (`AssistantApprovalCatalog.cs:22-60`).
- Card (`AssistantApprovalCardBuilder`): the file's name and kind, "Full screen" or "Corner
  (bottom right)", the span in plain words ("from 0:12 to 0:18"), the opacity when below 100, and
  the sound choice ("keeps the film's sound" / "mutes the film" / "sound at Balanced"). Never
  ids alone.
- Cost table `AssistantPlatformTools.Tools`: `Free`, no capability (a write, no model call).
- Annotations: `Apply*` is destructive automatically; `Propose*` read-only.

**Count bookkeeping.** Adding one pair moves the chat surface **105 → 107**, the MCP catalog
**104 → 106**, the projected MCP catalog **71 → 72**, and applies/proposes **34 → 35**. Every site
in the checklist at `docs/design/workflow-apps.md:526-535` moves in one commit: the prompt
(`AssistantAgent.cs:125`), `DatabaseSeeder.BuiltInAgents` (`:3486`, byte-identical), the count tests
(`AssistantAgentToolScopeTests.cs:73`, `AssistantToolDescriptionTests.cs:61`,
`AssistantPromptConsistencyTests.cs:163-175`), `AssistantMcpCatalogTests.cs:59`,
`AssistantAuthorityTests` (the literal), `AssistantToolProgress`, `docs/platform-assistant-mcp.md`,
`docs/design/mcp-tool-inventory.md`, and `CLAUDE.md` once at the end.

**MCP.** The pair collapses to one `SetMediaOverlay` MCP name in
`AssistantMcpToolSurface.Collapsed`. The MCP path can propose and apply a deterministic overlay; it
cannot set `allowSourceMediaEgress` (unchanged) and gains no new egress.

**The planner itself is not an assistant tool.** The assistant can answer "what pictures do you
have?" with `ListProjectFiles`, and "show this one" with the pair; the per-run choice stays with the
`OverlayPlanner`.

---

## 10. Linter and app checks

| Rule | Severity | What |
|---|---|---|
| `WL004 compile-ref-invalid` (**extended**) | Error | `MediaOverlayPlan` must be `Previous` or an earlier step that writes `MediaOverlayPlanOutput` (an `OverlayPlanner` agent). |
| `WL010 source-media-missing` (**extended**) | Error | A compile/analyze that consumes the `overlay` role counts the brief's files for it; an empty required role fails. |
| **`WL023 media-overlay-source-missing`** (new) | Error | Each configured `MediaOverlays[].Content` resolves to a file of this project that is image/video and not quarantined; step refs are earlier and media-writing. |
| **`WL024 media-overlay-span-invalid`** (new) | Error | `StartSec`/`EndSec` finite, ≥ 0, `EndSec > StartSec`, within `MaxWindowSec`; when the film length is known, `EndSec` ≤ length + 0.5. |
| **`WL025 media-overlay-card-too-long`** (new) | Warning | A card longer than 25 % of a known film or 30 s. |

Rules are appended to `StepCompatibilityRules.All` (`StepCompatibilityRules.cs:293-405`) with
`AppliesTo` = `VideoCompile` (`:416-435`), message keys `WL023`/`WL024`/`WL025` in `en` and `it`.

**App-level checks.** `AppManifestValidator` already refuses an input no step can read
(`WL018 manifest-orphan`, `AppManifestValidator.cs:76-102`). The `overlay` slot is accepted only when
the analyze step has `EmitOverlayOffers`; a stored manifest whose workflow lost that flag is flagged
by `WL018`, not silently ignored.

**Step catalog.** `VideoCompile`'s `[StepCard]` gains the overlays need; regenerate
`docs/assistant-kb/step-cards.md` with `UPDATE_STEP_CARDS_DOC=1`
(`AssistantKbDocsTests.cs:20-75`).

---

## 11. Moderation and security

- **Content.** Overlay media is project media that already passed ingest moderation; a step render
  already passed generation moderation. No new bytes are fetched: only project-file storage and the
  same-project output prefix.
- **No OCR.** A screenshot is a picture. Its text is not extracted, indexed or moderated as text.
  The final video is still screened at publish by the existing moderation path
  (`OutputsController.DownloadOutput` and `GetOutput`), which samples frames and therefore sees the
  card. Named in "What is unmeasured".
- **Sandbox.** The overlay input is opened by the engine's ffmpeg under the existing limits:
  `-protocol_whitelist file`, per-execution scratch path, pre-decode size and duration caps,
  non-root user. `MEDIA_OVERLAY_MAX_BYTES` (512 MB) and a probe duration cap are checked before the
  input is added.
- **No new egress.** `InsertContentRef` step results are prefix-checked; there is no URL kind.
- **Authority.** The compile re-checks the project tenant when it resolves the media snapshot; the
  assistant pair re-reads Edit authority per call; the planner has read-only tools.

---

## 12. Tests

| Layer | Test | Asserts |
|---|---|---|
| Config | `VideoCompileMediaOverlayConfigTests` | Every validation code; `0..100` opacity; span rules; append-only deserialization of a pre-change JSON; the serialized JSON of a null-member config is unchanged. |
| Config | `StepConfigSaveValidator` tests | A malformed `Content` ref and a foreign step result are refused at save. |
| Analysis | `MediaOverlayOfferBuilderTests` | Media from the brief role in order; else project media ordered by `UploadedAt`/`Id`; quarantined excluded; cap; window ranking, slicing, cap and contiguous `o{n}`; truncation alignment with the artifact. |
| Plan | `MediaOverlayPlanOutputInvariantTests` | No numeric/time/coordinate types; `OpacityPercent` the only int; camelCase round-trip; unknown words degrade. |
| Prompt | `MediaOverlayPlannerPromptConsistencyTests`, `BlockPromptListTests`, `SeededOutputSchemaDriftGuardTests` | Two copies byte-identical; the block list once; the seeded schema's top-level names match. |
| Builder | `MediaOverlayFilterBuilderTests` | Exact strings: card, inset, opacity < 100, border light/dark, loop, play-once, two overlays in order, odd/even dimensions, 4:5 content in a 16:9 canvas. |
| Compile | `VideoCompileMediaOverlayTests` (extends the `ReelCompileFixture` pattern, `VideoCompileReelFeatureTests.cs:40-110`) | Exact `FilterGraph` and `EncodeArgs` for a card and an inset on **both** encoders; off = the existing golden unchanged; one extra `-i` per overlay; input-index offsets; the output node. |
| Compile | Resolution tests | Unknown media/window dropped; cut-away window dropped; wrong MIME/foreign file; opacity 0; cap; a configured miss fails with each named code. |
| Loop/end | `MediaOverlayVideoLoopTests` | Short content loops; `PlayOnce` ends early with `endedEarly: true`; long content trims; `Mixed` with a silent source degrades to muted. |
| Card audio | `MediaOverlayCardAudioTests` | `Keep` leaves the program audio untouched; `Mute` ramps to silence across the card with the 50 ms edges and restores after; a brief preference (`keep`/`mute`) overrides the plan; `agent` leaves it. |
| Mixed audio | `MediaOverlayMixedAudioTests` | Delayed to the span start; `Quiet`/`Balanced`/`Prominent` map to −12/−6/0 dB and clamp; not counted as dialogue; included in the loudness stage; a silent source degrades. |
| Offer binding | `MediaOverlayOfferBindingTests` | A named brief set binds the offers (only those files); no named set offers every usable project image/video; size/aspect/orientation facts present; quarantined excluded. |
| Linter | `WorkflowLinter` cases | WL004 extension, WL010 role, WL023, WL024, WL025; no false positive on a graphics-only workflow. |
| App | `AppManifestTests`, `run-model.test.ts`, `AppRunPage.test.tsx` | Slot derived/curated; `WL018` when the analyze flag is absent; client role maps and upload accept; the run card rows and the "Not shown" list. |
| Assistant | `AssistantApprovalsControllerTests`, `AssistantAgentToolScopeTests`, `AssistantMcpCatalogTests` | Catalog card present; token binds the action and goes stale after an edit; counts 105→107, 104→106, 71→72, 34→35; MCP collapse. |
| E2E | `VideoCompileMediaOverlayRealFfmpegTests` | Synthetic fixtures only: a 800×1000 PNG colour-block card and a 2 s 320×240 MP4 moving colour, generated by a committed script under `inference/tests/.../TestData/picture-overlay/`. Renders a 6 s 640×360 program; extracts frames and asserts the card's colour inside its span, the film's colour outside it, the inset's blended colour at 50 % opacity, and the border pixel. |
| Review | `ReviewVisualCheck` extension | When a card/inset is applied, the sampled frame times include each overlay's midpoint (within the existing `Frames` cap), so the reviewer sees the picture. |

The E2E fixtures are synthetic and committed; no customer media. The unit and argv tests run with
the existing Moq `IVideoToolRunner`; the real-ffmpeg test is in the existing real-ffmpeg collection
and skips where ffmpeg is absent.

---

## 13. Tickets

Sizes: **S** ≤ 1 day, **M** 2–3 days, **L** ≥ 4 days, one executor each. Dependencies are explicit.

| Ticket | Content | Size | Depends on |
|---|---|---|---|
| `T-001` | Config, enums and validation (§5.1): card audio, volume, media-overlay default | S | — |
| `T-002` | Analysis offers: `m{n}`/`o{n}`, all usable media or the bound user set, size/aspect facts, snapshot, view nodes, caps, revisions (§5.3) | M | T-001 |
| `T-003` | `MediaOverlayPlanOutput`, `OverlayPlanner` agent, both prompts, schema, invariant tests (§7) | M | T-002 |
| `T-004` | `MediaOverlayFilterBuilder` + `ResolvedMediaOverlay` + argv goldens (§6.1) | M | T-001 |
| `T-005` | Compile execution on both encoders, input bookkeeping, output node, failure codes, card audio (§6.2–6.5) | L | T-004 |
| `T-006` | Video specifics: probe, loop/end, fps, muted/mixed audio at a bounded level (§5.5) | M | T-005 |
| `T-007` | Base template: planner step in `video-derush-edit`, `AddMediaOverlay` recipe patch, brief role and card-audio preference, app slot/manifest (§8) | M | T-002, T-003, T-005 |
| `T-014` | Curated recipe `testimonial-reel`: the template, its app manifest and its shape/linter/round-trip tests (§8.5) | S | T-003, T-005, T-007 |
| `T-008` | Linter: WL004/WL010 extensions, WL023–WL025, messages, step card (§10) | S | T-001, T-007 |
| `T-009` | Assistant/MCP pair, approval card, counts and every count site (§9) | M | T-001, T-007 |
| `T-010` | Web: slot, role maps, run card, messages en/it (§8.2–8.3) | M | T-007 |
| `T-011` | Docs: `docs/video-editing.md` sections + "not built" update, customer recipe, `run-brief.md`, `CLAUDE.md` once (§15) | S | T-005, T-007 |
| `T-012` | Synthetic fixtures + real-ffmpeg assertions (§12) | M | T-005, T-006 |
| `T-013` | Review-loop visual check samples overlay midpoints (§12) | S | T-005 |

**Order.** T-001 freezes the contract. T-002 and T-004 run in parallel; T-003 follows T-002; T-005
follows T-004; T-006 follows T-005. T-007 follows T-005. Then T-008, T-009, T-010, T-013 and T-014
run in parallel; T-012 and T-011 close the lane.

**What each ticket must not do.** T-001–T-010 must not alter `InsertContents`,
`InsertContentRef`, `green-screen-insert`, the graphics band vocabulary, or the 105-tool prompt
before T-009's single count commit. T-011 edits `CLAUDE.md` **once, at the end** (cache-prefix rule).

---

## 14. What is unmeasured until a real run

- **Agent taste.** Whether `OverlayPlanner` picks the card for a testimonial and a legible corner for
  a logo on real footage. The eval harness is the only measure; no paid eval run is in scope.
- **Legibility.** Whether a `Medium` inset is readable on a phone; whether a `Card` of a tall
  screenshot leaves bars that look intentional. Needs a real testimonial run.
- **Compile time.** The delta of one still card + one 2 s inset on a real 60 s film. Expect a small
  decode cost; unmeasured.
- **VFR and rotation.** A phone clip with variable frame rate or a display-rotation tag as the
  overlay source; the existing `display-rotation-8` and `vfr-sync-1` work covers the program, not
  yet an overlay input.
- **Audio mix.** The balance of a `Mixed` overlay (levels and the loudness stage) against a real
  program is unmeasured; the bounded range and the no-duck-on-overlay rule are the design, not a
  measured level.
- **Moderation of text in pictures.** Not OCR'd; the publish sampler sees frames, not words.
- **Cost of the new agent.** The planner is one `low`-effort agent call per footage run; its real
  token use is unmeasured.

---

## 15. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Template step renumbering breaks a stored workflow or a test | Medium | Medium | Only the base `video-derush-edit` changes; the recipe patch covers the rest; the template shape tests and a stored-workflow round-trip test run in T-007. |
| The planner's output is ignored by the compile (offered-id drift) | Low | Medium | The media snapshot lives in the analysis artifact and the compile reads that, not a live list; a shift cannot change an id. |
| Cache-key drift makes every old compile miss | Low | High | The key hashes the stored config string; a round-trip test asserts a null-member config serializes unchanged. |
| The new stage changes existing output when off | Low | High | The helper is not called when both members are null; the existing goldens are asserted unchanged in T-005. |
| Screen-insert semantics regress | Low | High | Separate config member, separate stage, `InsertContents`/`InsertContentRef` untouched; the green-screen suite runs as-is. |
| A model picks a face-covering inset | Medium | Medium | Prompt rule + `view` subject boxes (when `LocateSubjects` is on) + the reviewer's visual check; no deterministic override. |
| `Mixed` audio adds mix complexity | Medium | Low | It is the first cut (Q3); `Muted` is the default and the MVP. |
| A large overlay video costs decode time | Low | Medium | `MEDIA_OVERLAY_MAX_BYTES`, probe duration cap, one input at a time in the existing ffmpeg lane. |
| The app slot is filled but the planner uses none | Medium | Low | `unusedOffered` on the run card; the assistant can set a fixed overlay. |

---

## 16. Decisions for the owner

All three questions were answered on 2026-11-01. The answers are implemented as design, not reduced:

| # | Question | Owner answer | Where it lives |
|---|---|---|---|
| Q1 | Under a full-frame card, keep the film's audio or mute it? | **The agent chooses per card**; the user may set a binding preference. Default `Keep`. | `CardAudio` on the config/plan, `overlayCardAudio` in the brief, `MediaOverlayCardAudioDefault` on the compile. |
| Q2 | With an empty overlay slot, may the planner use any project picture/video? | **Yes**; the user may name files, which bind the offers. | `MediaOverlayOfferBuilder`: named set binds, else all usable project media with size/aspect facts. |
| Q3 | Is mixed audio in v1? | **Yes**, both muted and mixed, agent-chosen, at a bounded level. | `Audio`/`AudioLevel` on the plan and config; `Quiet`/`Balanced`/`Prominent` = −12/−6/0 dB clamped −18..0. |

**Decided earlier (Owner, 2026-11-01):** both forms — a full-frame card and a picture-in-picture
inset — are MVP, and the agent chooses per picture. No reduction is proposed.

---

## 17. What this does not do yet

- **No OCR and no text extraction** from a screenshot. The picture is shown, not read.
- **No `EditImageDocument` or `DesignSystemLibrary` asset** as an overlay source. Both are designed
  but cannot bind to a run today (`InsertContentRef` has three kinds only,
  `InsertContentRefResolver.cs:60-61`). A flattened picture export is a later step.
- **No free placement by fraction** from the model. Anchors + sizes only. A human can set a fixed
  inset in the Advanced editor only through the anchor/size vocabulary; arbitrary fractions are not
  added in this lane.
- **No picture overlay inside a tracked plate.** That remains the screen-insert path.
- **No card transition** of its own (a dip or slide). The program fade and the existing seam
  treatments apply around it.
- **No per-overlay ducking** or sidechain; mixed audio is a plain level.
- **No parallel planning** of overlays with the cut: the planner runs after the cut decision, so it
  cannot change which spans are kept.
- **No new template** beyond the base footage one and the recipe patch; other footage variants opt
  in.

---

## Appendix A — Why the card is a separate stage from a screen insert

A screen insert is defined by a **tracked plate**: its geometry is servoed per frame from a chroma
quad and its content is warped into that quad (`ScreenInsertFilterBuilder`,
`ChromaQuadTracker`). A card or inset is defined by the **canvas**: a fixed rectangle derived from
the content's own aspect and the anchor. They share no geometry, no tracker and no failure mode.
Overloading `InsertContents` with a canvas rectangle would either change the tracked path's
meaning or require a second mode inside one field — the brief forbids the first and the design
avoids the second. The two stages coexist: a run can insert a rendered scene into a tracked phone
**and** show a testimonial card later.

## Appendix B — Why the plan carries `o{n}` windows instead of a duration

A duration word (`Short`/`Medium`/`Hold`) cannot say **when**. The graphics planner can because its
placement already carries a fully resolved window (`VideoAnalysisPlacement.StartSec/EndSec`). The
overlay planner needs the same: the moment is the product decision ("show the review while they say
this"). Offering pre-resolved windows keeps the model away from time while giving it the moment. A
human who wants an exact second uses the compile config, which is on the output clock and validated
against the finished film.
