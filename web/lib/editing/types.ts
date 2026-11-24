/**
 * TypeScript mirror of the committed EditTimeline **v2** contract.
 *
 * Source of truth: `inference/src/ReelBolt.Shared/Editing/EditTimeline.schema.json` plus the
 * C# records beside it (`EditTimeline.cs`, `Item.cs`, `Keyframed.cs`, `Provenance.cs`). This
 * module is hand-written, so `__tests__/schema-drift.test.ts` asserts it against the schema —
 * never edit one side without the other.
 *
 * Mirroring rules:
 *   - Names are camelCase: the C# enums are serialized through
 *     `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`, so every enum is a lowerCamel
 *     string-literal union rather than a TS `enum`.
 *   - C# `long`/`double`/`int` → `number`, `Guid` → `string`, `T?` → `T | null`.
 *   - A field the schema does not list as required is optional here (it may be absent on the
 *     wire even though the current C# records always serialize it).
 */

import type { CameraProfileId } from './color/camera-profiles';

// ─────────────────────────────────────────────────────────────────────────────
// Enums — camelCase string unions, exactly as the schema spells them.
// ─────────────────────────────────────────────────────────────────────────────

/** Track type. */
export type TrackKind = 'video' | 'audio';

/** How to handle aspect-ratio mismatches. */
export type AspectPolicy = 'fit' | 'fill' | 'stretch';

/** How an edited item was created. */
export type ProvenanceOrigin = 'manual' | 'generated' | 'imported' | 'analyzed';

/** What an {@link AssetRef} points at. */
export type AssetRefKind = 'projectFile' | 'stepResult';

/** Interpolation method from a keyframe to the next one. */
export type InterpKind =
  | 'linear'
  | 'easeInQuad'
  | 'easeOutQuad'
  | 'easeInOutQuad'
  | 'easeInCubic'
  | 'easeOutCubic'
  | 'easeInOutCubic'
  | 'step';

/** P7: the closed effects catalogue (`EditTimelineValidator.EffectTypes`). */
export type EffectType = 'blur' | 'sharpen' | 'vignette' | 'grain' | 'pixelate' | 'mono';

/** P7: the closed title-template catalogue (`EditTimelineValidator.TitleTemplates`). */
export type TitleTemplate = 'plain' | 'lowerThird' | 'boxed' | 'outline' | 'card';

/** R09.6: the closed caption-look catalogue (`EditTimelineValidator.CaptionLooks`). */
export type CaptionLook = 'clean' | 'bold' | 'boxed' | 'outline' | 'spotlight';

/** R09.6/R10.5: the 3x3 text-position pad (`EditTimelineValidator.TextPositions`). */
export type TextPosition = 'topLeft' | 'topCenter' | 'topRight' | 'middleLeft' | 'center' | 'middleRight' | 'bottomLeft' | 'bottomCenter' | 'bottomRight';

/** P7: how a slowed clip makes its frames. */
export type RetimeQuality = 'frame' | 'opticalFlow';

/** P7: the mask shapes. */
export type MaskKind = 'rect' | 'ellipse';

/** P8: a media clip's channel routing into the stereo mix. */
/** T-012: how a clip's picture blends with what is below it; absent/`normal` is plain alpha-over. Video tracks only. */
export type BlendMode = 'normal' | 'multiply' | 'screen' | 'overlay' | 'softLight' | 'hardLight' | 'darken' | 'lighten' | 'add' | 'difference';

export type ChannelMap = 'stereo' | 'left' | 'right' | 'mono' | 'swap';

/** P8: the denoiser kinds this wave ships (`rnn` needs a model file the image does not carry). */
export type DenoiseKind = 'fft';

/** P8: the leveler kinds. */
export type LevelerKind = 'speech' | 'dynamic';

// ─────────────────────────────────────────────────────────────────────────────
// Primitives
// ─────────────────────────────────────────────────────────────────────────────

/** Rational number representation (numerator/denominator). */
export type Rational = {
  num: number;
  den: number;
};

// ─────────────────────────────────────────────────────────────────────────────
// Keyframed animation
// ─────────────────────────────────────────────────────────────────────────────

/** A single keyframe point in an animation sequence. */
export type Keyframe<T> = {
  /** Frame position of this keyframe. */
  frame: number;
  /** Value at this keyframe. */
  value: T;
  /** Interpolation method to the next keyframe. */
  interp: InterpKind;
};

/**
 * A value that can optionally be keyframed for animation.
 *
 * `value` is the base/default used when there are no keyframes or before the first one;
 * `keys`, when present, overrides it from each keyframe's frame onward.
 */
export type Keyframed<T> = {
  /** Base/default value. */
  value?: T | null;
  /** Optional keyframes for animation. */
  keys?: Keyframe<T>[] | null;
};

// ─────────────────────────────────────────────────────────────────────────────
// Provenance and asset references
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Metadata describing how an edited timeline item was created or modified.
 *
 * Ids and names are populated by *different* paths and neither is derived from the other: the
 * `Guid` fields are filled by v2-native creation paths (which hold the entities), the display
 * names by a v1 seed (whose manifest records names only). Whichever path did not run leaves its
 * fields null — a name is never synthesized from an id, or vice versa.
 */
export type Provenance = {
  /** How this item was created. */
  origin: ProvenanceOrigin;
  /** Optional workflow step result that generated this item. */
  stepResultId?: string | null;
  /** Optional agent that created or modified this item. */
  agentDefinitionId?: string | null;
  /** Optional inference provider used to generate this item. */
  providerId?: string | null;
  /** Optional offered id from analysis or decision output. */
  offeredId?: string | null;
  /** Optional explanation of why this item was chosen or created. */
  rationale?: string | null;
  /** Whether the user has manually edited or confirmed this item. */
  editedByUser: boolean;
  /** Optional agent display name (a v1 seed fills this); ids are filled by v2-native paths. */
  agentName?: string | null;
  /** Optional inference provider display name (a v1 seed fills this); ids are filled by v2-native paths. */
  providerName?: string | null;
};

/** Reference to an asset used in editing (a project file or a workflow step result). */
export type AssetRef = {
  /** Type of asset reference. */
  kind: AssetRefKind;
  /** Reference to a project file when `kind` is `projectFile`. */
  projectFileId?: string | null;
  /** Reference to a workflow step result when `kind` is `stepResult`. */
  stepResultId?: string | null;
};

// ─────────────────────────────────────────────────────────────────────────────
// Corner-pin geometry
// ─────────────────────────────────────────────────────────────────────────────

/** A point in source-frame coordinates. */
export type QuadPoint = {
  /** Horizontal coordinate. */
  x: number;
  /** Vertical coordinate. */
  y: number;
};

/** The four corner points of a (possibly non-planar) quad, corner-pinning an insert. */
export type QuadCorners = {
  topLeft: QuadPoint;
  topRight: QuadPoint;
  bottomRight: QuadPoint;
  bottomLeft: QuadPoint;
};

// ─────────────────────────────────────────────────────────────────────────────
// Items
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Fields every timeline item carries, whatever its concrete `type`.
 *
 * The C# `Item` base record is abstract and contributes exactly these; the concrete members
 * below intersect it with their own fields plus the `type` discriminator.
 */
export type ItemBase = {
  /** Unique identifier for this item. */
  id: string;
  /** Human-readable name for this item. */
  name: string;
  /** Start position on the record (output) clock, in frames. */
  recordIn: number;
  /** End position on the record (output) clock, in frames (exclusive). */
  recordOut: number;
  /** How this item was created (agent decision, import, manual edit), or null when unknown. */
  provenance?: Provenance | null;
};

/** A media clip referencing external source content. */
export type MediaClip = ItemBase & {
  type: 'mediaClip';
  /** Reference to the source media asset, resolvable against the timeline's own project. */
  assetRef: AssetRef;
  /** Offset into the source asset, in frames. */
  sourceIn?: number;
  /** Playback speed multiplier. */
  playbackRate?: number;
  /** Whether this clip is enabled in the output. */
  enabled?: boolean;
  /**
   * Clip gain in dB. `value` is the clip's static gain; `keys`, when present, is a time-varying
   * gain envelope that overrides it from each keyframe's frame onward — the clip-gain-plus-
   * automation split a DAW uses.
   */
  gainDb?: Keyframed<number> | null;
  /** P5: position/scale/rotation/opacity, each optionally keyframed; null/absent is the identity. */
  transform?: ClipTransform | null;
  /** P5: a static crop as fractions of the source picture; null/absent crops nothing. */
  crop?: ClipCrop | null;
  /** P5: play the source backwards; `sourceIn` stays the lowest source frame the clip touches. */
  reverse?: boolean;
  /** Source orientation: clockwise quarter turns applied BEFORE crop and fit (swaps width/height at 90/270). Absent/null/0 = upright. */
  sourceRotation?: SourceRotation | null;
  /** P5: freeze frame — every frame shows source frame `sourceIn`; silent. Exclusive with `reverse`. */
  hold?: boolean;
  /** P5: picture and sound fade in over this many frames from `recordIn`. */
  fadeInFrames?: number;
  /** P5: picture and sound fade out over this many frames before `recordOut`. */
  fadeOutFrames?: number;
  /** P7: the clip's own grade stack, applied after its colour group's. */
  grade?: GradeStack | null;
  /**
   * P7: a shape mask on the picture. On the wire this may also be a SegmentationMask (see ClipMask); the field
   * type flips to ClipMask with the UI that reads x/y/w/h (frame-handles, ComposeWorkspace), which is outside T-014.
   */
  mask?: ClipMask | null;
  /** P7: a chroma keyer. */
  keyer?: ChromaKeyer | null;
  /** P7: effects from the closed catalogue, applied in order after the grade. */
  effects?: ClipEffect[] | null;
  /** Prompted region effects (blur, pixelate, ...) on the object a segmentation mask follows, in order; at most 4. Render-only. */
  regionEffects?: RegionClipEffect[] | null;
  /** P7: stabilise the picture at render time (two-pass vidstab; not previewed). */
  stabilize?: boolean;
  /** P7: crop the source to the canvas aspect around a subject point. */
  reframe?: ReframeTarget | null;
  /** P7: how a slowed clip makes its frames. */
  retimeQuality?: RetimeQuality;
  /** P7: a manual corner pin of the whole picture onto the canvas, corners in canvas fractions. */
  cornerPin?: QuadCorners | null;
  /** P8: how the source's channels reach the stereo mix; absent/`stereo` passes them as they are. */
  channelMap?: ChannelMap;
  /**
   * R08.1: clips sharing a `linkId` are one linked group (typically a video clip and its camera-audio
   * companion). null/absent = not linked. 1-64 chars of [A-Za-z0-9_-]; minted with `newGuid()`.
   */
  linkId?: string | null;
  /**
   * P3: the camera log profile the source was shot in — one of `CAMERA_PROFILE_IDS` (e.g.
   * `appleLog`, `sonySLog3`), or null/absent for a source already in display space. Saved on the
   * clip and exported; the renderer applies the matching input transform.
   */
  inputProfile?: CameraProfileId | null;
  /** T-012: blend mode against the pictures below (video tracks only); absent = `normal`. */
  blendMode?: BlendMode;
};

/**
 * P5: a media clip's picture transform. Every channel is a `Keyframed<number>`; a `null`/absent
 * channel is its identity (position 0, scale 1, rotation 0, opacity 1). Positions are fractions of
 * the canvas (x of its width, y of its height, positive = right/down), scale is relative to the
 * fitted size, rotation is degrees clockwise.
 */
export type ClipTransform = {
  positionX?: Keyframed<number> | null;
  positionY?: Keyframed<number> | null;
  scale?: Keyframed<number> | null;
  /** Vertical scale, same limits as `scale`; absent means the same as `scale` (a uniform scale). When present `scale` is the horizontal scale. */
  scaleY?: Keyframed<number> | null;
  rotation?: Keyframed<number> | null;
  opacity?: Keyframed<number> | null;
};

/** P5: a static crop of the source picture, each edge a fraction in [0, 1); opposite edges sum below 1. */
export type ClipCrop = {
  left?: number;
  top?: number;
  right?: number;
  bottom?: number;
};

// ─────────────────────────────────────────────────────────────────────────────
// P7: colour and compose
// ─────────────────────────────────────────────────────────────────────────────

/** P7: one colour wheel — per-channel offsets plus a master, each in [-1, 1]; 0 is no change. */
export type GradeWheel = { r?: number; g?: number; b?: number; master?: number };

/** P7: the primaries of one grade node; every field defaults to its identity. */
export type GradePrimaries = {
  lift?: GradeWheel | null;
  gamma?: GradeWheel | null;
  gain?: GradeWheel | null;
  offset?: GradeWheel | null;
  /** [0.25, 3], identity 1. */
  contrast?: number;
  /** [0, 1], identity 0.5. */
  pivot?: number;
  /** [0, 3], identity 1. */
  saturation?: number;
  /** Degrees [-180, 180]. */
  hue?: number;
  /** [-1, 1], positive = warmer. */
  temperature?: number;
  /** [-1, 1], positive = magenta. */
  tint?: number;
  /** Stops [-4, 4]. */
  exposure?: number;
};

/** P7: a point of a tone curve, both coordinates in [0, 1]. */
export type CurvePoint = { x: number; y: number };

/** P7: a master (luma) curve and one per channel, each ≤ 16 points with strictly increasing x. */
export type GradeCurves = {
  y?: CurvePoint[] | null;
  r?: CurvePoint[] | null;
  g?: CurvePoint[] | null;
  b?: CurvePoint[] | null;
};

/** P7: a `.cube` project file applied as a 3D LUT, blended toward identity by `intensity` [0, 1]. */
export type GradeLut = { file: AssetRef; intensity?: number };

/** P7: one correction of a grade stack, individually bypassable. */
export type GradeNode = {
  id: string;
  name?: string;
  enabled?: boolean;
  primaries?: GradePrimaries | null;
  curves?: GradeCurves | null;
  lut?: GradeLut | null;
  /** P9: an HSL qualifier confining the correction (absent/null: whole frame). */
  qualifier?: GradeQualifier | null;
  /** P9: a (tracked) power window confining the correction. */
  window?: GradeWindow | null;
};

/** P9: an HSL secondary — hue within `hueWidth`° of `hueCenter`° (180 = every hue), saturation and lightness bands [0, 1], softness [0, 0.5], invert. */
export type GradeQualifier = {
  hueCenter?: number;
  hueWidth?: number;
  satMin?: number;
  satMax?: number;
  lumMin?: number;
  lumMax?: number;
  softness?: number;
  invert?: boolean;
};

/** P9: a power window — a shape, optionally tracked by keyframed offsets (canvas fractions [-1, 1], timeline frames). */
export type GradeWindow = {
  shape: ShapeMask;
  offsetX?: Keyframed<number> | null;
  offsetY?: Keyframed<number> | null;
};

/** P7: the ordered corrections of a clip, a colour group or an adjustment clip; `enabled: false` bypasses the whole stack. */
export type GradeStack = { enabled?: boolean; nodes?: GradeNode[] | null };

/** P7: clips graded together; the group's stack is applied before each member's own. */
export type ColorGroup = {
  id: string;
  name: string;
  clipIds: string[];
  grade?: GradeStack | null;
  /** The analysis look group (`k{n}`) the group was pre-populated from. */
  lookGroupId?: string | null;
};

/** P7: a rectangle or ellipse mask in canvas fractions with a feathered edge. */
export type ShapeMask = { kind?: MaskKind; x?: number; y?: number; w?: number; h?: number; feather?: number; invert?: boolean };

/**
 * A segmentation mask: the union of the named tracks (`g{n}`, 1..8) of a VideoAnalyze step result of the
 * same project, with an optional invert and a pixel feather in [0, 64]. A shape mask has no `kind: 'segmentation'`.
 */
export type SegmentationMask = {
  kind: 'segmentation';
  analysisStepResultId: string;
  trackIds: string[];
  invert: boolean;
  featherPx: number;
};

/** A clip mask: a shape (kind absent, rect or ellipse) or a segmentation mask. */
export type ClipMask = ShapeMask | SegmentationMask;

/** P7: a chroma keyer — key colour `#RRGGBB`, similarity (0, 1], blend [0, 1], spill [0, 1]. */
export type ChromaKeyer = { color?: string; similarity?: number; blend?: number; spill?: number };

/** P7: one effect of the closed catalogue with an amount in [0, 1]. */
export type ClipEffect = { type: EffectType; amount?: number; enabled?: boolean };

/** The closed region-effect words. */
export type RegionEffectType = 'blur' | 'pixelate' | 'highlight' | 'spotlight' | 'colorpop' | 'outline';

/** One prompted region effect: a word, a strength in [0, 1] and the segmentation mask it follows. */
export type RegionClipEffect = { effect: RegionEffectType; strength: number; mask: SegmentationMask; enabled: boolean };

/** P7: the subject point (canvas fractions) a reframe crops around. */
export type ReframeTarget = { x?: number; y?: number };

/** A title/text clip with formatting. */
export type TitleClip = ItemBase & {
  type: 'titleClip';
  /** Title text content. */
  text: string;
  /** Font size in points. */
  fontSize?: number;
  /** Font family name. */
  fontFamily?: string;
  /** Text color hex code. */
  color?: string;
  /** Whether this clip is enabled in the output. */
  enabled?: boolean;
  /** P7: a title template from the closed catalogue; null/absent is the plain centred title. */
  template?: TitleTemplate | null;
  /** R10.5: where the title sits on the 3x3 pad; null/absent is centred. */
  position?: TextPosition | null;
  /** T-014: free move/scale/rotate/opacity of the title around its own anchor (where `template`/`position` place it); absent is today's render. */
  transform?: ClipTransform | null;
};

/** A generated clip (bars, noise, color, …). */
export type GeneratorClip = ItemBase & {
  type: 'generatorClip';
  /** Type of generated content (e.g. `bars`, `noise`, `color`). */
  generatorType: string;
  /** Configuration specific to the generator type. */
  generatorConfig?: Record<string, unknown> | null;
  /** Whether this clip is enabled in the output. */
  enabled?: boolean;
};

/** An insert clip for placing assets or composed content, optionally corner-pinned. */
export type InsertClip = ItemBase & {
  type: 'insertClip';
  /** Reference to an inserted asset. */
  assetRef?: AssetRef | null;
  /** Tracked corner-pin quad, keyframed over tracked frames, when this insert is corner-pinned. */
  quad?: Keyframed<QuadCorners> | null;
  /** Whether this clip is enabled in the output. */
  enabled?: boolean;
  /** P7: the matte parameters the insert is keyed with before it is pinned; null is an opaque insert. */
  matte?: ChromaKeyer | null;
};

/** A caption/subtitle clip with language support. */
export type Caption = ItemBase & {
  type: 'caption';
  /** Caption text content. */
  text: string;
  /** Language code (e.g. `en`, `es`, `fr`). */
  language?: string;
  /** Whether this caption is enabled in the output. */
  enabled?: boolean;
  /** R09.6: the burn-in look from the closed catalogue; null/absent is clean. */
  look?: CaptionLook | null;
  /** R09.6: where the caption sits on the 3x3 pad; null/absent is bottom centre. */
  position?: TextPosition | null;
};

/** A transition effect between clips. */
export type Transition = ItemBase & {
  type: 'transition';
  /** Type of transition (e.g. `dissolve`, `fade`, `wipe`). */
  transitionType: string;
  /** Easing function for the transition. */
  easing?: string;
  /** Whether this transition is enabled. */
  enabled?: boolean;
};

/**
 * P8: a compound clip — plays the sequence named by `sequenceId` (from `EditTimeline.sequences`)
 * from sequence frame `sourceIn` for its span. The renderer and the preview flatten it
 * (`lib/editing/structure/flatten.ts`), so nothing downstream knows nesting exists.
 */
export type CompoundClip = ItemBase & {
  type: 'compoundClip';
  /** The sequence this clip plays; must exist in the document's `sequences`. */
  sequenceId: string;
  /** Offset into the sequence, in frames. */
  sourceIn?: number;
  enabled?: boolean;
};

/** P8: one angle of a multicam clip; `offsetFrames` is the angle's source frame at multicam-source frame 0 (the sync result). */
export type MulticamAngle = {
  id: string;
  name: string;
  assetRef: AssetRef;
  offsetFrames?: number;
  /**
   * P3: the camera log profile the angle's source was shot in — one of `CAMERA_PROFILE_IDS`, or
   * null/absent for a display-space source. Carried onto every media clip the flattener emits for
   * the angle, so preview and render apply the input transform.
   */
  inputProfile?: CameraProfileId | null;
};

/** P8: an angle switch at a record frame strictly inside the clip's span. */
export type MulticamCut = {
  atFrame: number;
  angleId: string;
};

/**
 * P8: a multicam clip — synchronised angles of one moment, one playing at a time: `activeAngleId`
 * from the clip's start, then each cut switches at its frame. Flattened into media clips for
 * rendering and preview.
 */
export type MulticamClip = ItemBase & {
  type: 'multicamClip';
  angles: MulticamAngle[];
  activeAngleId: string;
  cuts?: MulticamCut[] | null;
  /** Offset into the synchronised multicam source, in frames. */
  sourceIn?: number;
  enabled?: boolean;
  /** Clip gain in dB for the active angle's audio. */
  gainDb?: Keyframed<number> | null;
  /**
   * R08.2: whose sound the clip plays: 'program' (default when absent: the active angle follows the
   * cuts), one angle id of this clip (that angle's sound throughout), or 'none' (silent).
   */
  audioSource?: string | null;
};

/** P8: a nested timeline a compound clip plays — its own tracks in sequence-local frames. */
export type EditSequence = {
  id: string;
  name: string;
  tracks: Track[];
};

/** P7: an adjustment clip — a grade and effects applied to everything composited below it for its span. */
export type AdjustmentClip = ItemBase & {
  type: 'adjustmentClip';
  grade?: GradeStack | null;
  effects?: ClipEffect[] | null;
  enabled?: boolean;
};

/**
 * Any timeline item, discriminated on `type`. All nine members are implemented (the adjustment clip
 * since P7, the compound and multicam clips since P8).
 */
export type Item =
  | MediaClip
  | TitleClip
  | GeneratorClip
  | InsertClip
  | Caption
  | Transition
  | CompoundClip
  | MulticamClip
  | AdjustmentClip;

// ─────────────────────────────────────────────────────────────────────────────
// Timeline
// ─────────────────────────────────────────────────────────────────────────────

/** Settings for an editing timeline: dimensions, frame rate and aspect handling. */
export type EditTimelineSettings = {
  /** Video width in pixels. */
  width: number;
  /** Video height in pixels. */
  height: number;
  /** Frame rate, as a rational number. */
  rate: Rational;
  /** Audio sample rate in Hz. */
  sampleRate: number;
  /** Pixel aspect ratio. */
  pixelAspect: Rational;
  /** How to handle aspect-ratio mismatches. */
  aspectPolicy: AspectPolicy;
  /** Starting timecode offset, in frames. */
  startTimecodeFrames: number;
};

/** A track in an editing timeline, containing clips and other items. */
export type Track = {
  /** Unique track identifier. */
  id: string;
  /** Track type. */
  kind: TrackKind;
  /** Track name. */
  name: string;
  /** Optional track color (hex code, e.g. `#FF0000`). */
  color?: string | null;
  /** Track height in editor units. */
  height: number;
  /** Whether the track is locked for editing. */
  locked: boolean;
  /** Whether the track is enabled in output. */
  enabled: boolean;
  /** Whether the track is muted (audio tracks). */
  mute: boolean;
  /** Whether only this track should play (audio tracks). */
  solo: boolean;
  /** Optional audio bus for mixing. */
  busId?: string | null;
  /** Clips and elements on this track. */
  items: Item[];
  /** P5: the track fader in dB — `value` static, `keys` an automation envelope over timeline frames. */
  gainDb?: Keyframed<number> | null;
  /** P5: stereo balance -1..1 (0 = centre). */
  pan?: number;
  /** P5: the track equaliser; null/absent is flat. */
  eq?: TrackEq | null;
  /** P8: the track's dynamics section; null/absent is none. */
  dynamics?: AudioDynamics | null;
  /** P8: the track's denoiser; null/absent is none. */
  denoise?: AudioDenoise | null;
  /** P8: the track's leveler; null/absent is none. */
  leveler?: AudioLeveler | null;
};

/** P8: a compressor — threshold dBFS [-60, 0], ratio [1, 20], attack/release ms [1, 2000], make-up dB [0, 24]. */
export type AudioCompressor = { thresholdDb?: number; ratio?: number; attackMs?: number; releaseMs?: number; makeupDb?: number };
/** P8: a brick-wall limiter at a ceiling in dBFS [-24, 0]. */
export type AudioLimiter = { ceilingDb?: number };
/** P8: a noise gate — threshold dBFS [-90, 0], ratio [1, 20], attack/release ms [1, 2000]. */
export type AudioGate = { thresholdDb?: number; ratio?: number; attackMs?: number; releaseMs?: number };
/** P8: a de-esser at an intensity in [0, 1]. */
export type AudioDeesser = { intensity?: number };
/** P8: the dynamics section of a track or bus; every stage optional. */
export type AudioDynamics = { compressor?: AudioCompressor | null; limiter?: AudioLimiter | null; gate?: AudioGate | null; deesser?: AudioDeesser | null };
/** P8: a denoiser — kind `fft`, strength [0, 1]. */
export type AudioDenoise = { kind?: DenoiseKind; strength?: number };
/** P8: a leveler — kind `speech` or `dynamic`, target dBFS [-40, 0]. */
export type AudioLeveler = { kind?: LevelerKind; targetDb?: number };

/**
 * P8: a submix bus. Tracks route to it through `Track.busId`, a bus routes to another through
 * `outputBusId` (never in a cycle, at most four levels), a bus with no output feeds the master.
 */
export type AudioBus = {
  id: string;
  name: string;
  gainDb?: Keyframed<number> | null;
  pan?: number;
  eq?: TrackEq | null;
  dynamics?: AudioDynamics | null;
  denoise?: AudioDenoise | null;
  leveler?: AudioLeveler | null;
  outputBusId?: string | null;
};

/** P5: one parametric EQ band. */
export type EqBand = {
  frequencyHz: number;
  gainDb: number;
  q?: number;
};

/** P5: a track equaliser — optional low cut, optional high cut, up to four bands. */
export type TrackEq = {
  lowCutHz?: number | null;
  highCutHz?: number | null;
  bands?: EqBand[] | null;
};

/** A marker point in an editing timeline. */
export type Marker = {
  /** Unique marker identifier. */
  id: string;
  /** Marker position, in frames. */
  frame: number;
  /** Marker name. */
  name: string;
  /** Optional marker color (hex code). */
  color?: string | null;
  /** Optional marker note. */
  note?: string | null;
  /** Optional marker duration, in frames. */
  durationFrames?: number | null;
};

/** A range with in and out points. */
export type EditRange = {
  /** In point, in frames. */
  inFrame: number;
  /** Out point, in frames. */
  outFrame: number;
};

/** Seed data for timeline generation. */
export type EditTimelineSeed = {
  /** The workflow execution id that generated this timeline. */
  executionId: string;
  /** The version of the timeline manifest. */
  version: number;
};

/**
 * Complete video editing timeline with tracks, markers, items and metadata.
 *
 * `schemaVersion` is fixed at 2 — the schema pins it with `const: 2`, so the v1 document shape
 * is not representable here at all.
 */
export type EditTimeline = {
  /** Schema version for this timeline. */
  schemaVersion: 2;
  /** Unique identifier for this timeline. */
  id: string;
  /** Project this timeline belongs to. */
  projectId: string;
  /** Human-readable timeline name. */
  name: string;
  /** Timeline settings. */
  settings: EditTimelineSettings;
  /** Tracks in this timeline, in compositing order. */
  tracks: Track[];
  /** Markers in this timeline. */
  markers: Marker[];
  /** Optional in/out point range for playback. */
  inOut?: EditRange | null;
  /** Optional seed data for timeline generation. */
  seed?: EditTimelineSeed | null;
  /** P7: clips graded together; a clip is in at most one group. */
  colorGroups?: ColorGroup[] | null;
  /** P8: the nested timelines compound clips play; null/absent is none. */
  sequences?: EditSequence[] | null;
  /** P8: the submix buses tracks route to; null/absent is none (every track feeds the master). */
  buses?: AudioBus[] | null;
};

/** The clockwise source orientations a media clip may carry. */
export type SourceRotation = 0 | 90 | 180 | 270;
