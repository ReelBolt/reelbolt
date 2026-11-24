/**
 * Mirrors `ReelBolt.Shared.Styles` and `StyleDtos.cs` (Inference API) — see docs/editing-styles.md.
 * Every union below is an APPEND-ONLY C# enum persisted by name.
 */
export type StylePacing = 'Slow' | 'Medium' | 'Fast' | 'BeatSynced';
export type StyleStorytelling = 'Chronological' | 'HookFirst' | 'ProblemSolution' | 'Montage';
export type StyleEditing = 'JumpCuts' | 'SmoothTransitions' | 'Mixed';
export type StyleTone = 'Calm' | 'Upbeat' | 'Energetic' | 'Serious' | 'Playful' | 'Inspirational';
export type StyleCaptions = 'Clean' | 'Karaoke' | 'Punch' | 'Bold';
export type StyleColor = 'Natural' | 'Warm' | 'Cool' | 'Filmic' | 'Vibrant' | 'Muted' | 'Mono';
export type StyleMusicEnergy = 'Low' | 'Medium' | 'High';
export type StyleGraphicsDensity = 'None' | 'Minimal' | 'Moderate' | 'Rich';
/** How busy the dynamic edit is (beat-aligned punch-ins, alternations, flashes). Absent = no preference. */
export type StyleDynamics = 'Calm' | 'Medium' | 'Fast';

/** The output shape picked when a workflow is run. `Original` keeps each step's own format. */
export type RunOutputFormat = 'Original' | 'Vertical' | 'Square' | 'Landscape' | 'Portrait';

/**
 * The output resolution picked when a workflow is run, by its short side (`2160p` is shown as
 * "4K"). Absent means Auto: today's output, up to 1080p or the source's own size.
 */
export type RunOutputResolution = '720p' | '1080p' | '2160p';

/** The resolution control's value: `Auto` (sends nothing) or an explicit resolution. */
export type RunResolutionChoice = 'Auto' | RunOutputResolution;

/** One optional choice per axis plus short notes; null/absent means "no preference". */
export interface StyleConfig {
  pacing?: StylePacing | null;
  storytelling?: StyleStorytelling | null;
  editing?: StyleEditing | null;
  tone?: StyleTone | null;
  captions?: StyleCaptions | null;
  color?: StyleColor | null;
  music?: StyleMusicEnergy | null;
  graphics?: StyleGraphicsDensity | null;
  dynamics?: StyleDynamics | null;
  notes?: string | null;
}

export interface StylePreset {
  key: string;
  name: string;
  description: string;
  config: StyleConfig;
  suggestedFormat: RunOutputFormat;
}

export interface EditingStyle {
  id: string;
  name: string;
  description: string | null;
  /** Set when the style is offered only inside that project. */
  projectId: string | null;
  config: StyleConfig;
  createdAt: string;
  updatedAt: string;
}

export interface SaveEditingStyleRequest {
  name: string;
  description?: string | null;
  projectId?: string | null;
  config: StyleConfig;
}

/** What a run is started with beyond its brief. Every field optional: none = a plain run. */
export interface RunChoices {
  styleId?: string;
  stylePresetKey?: string;
  outputFormat?: RunOutputFormat;
  /** Sent only when the run asks for one; omitted for Auto. */
  outputResolution?: RunOutputResolution;
}
