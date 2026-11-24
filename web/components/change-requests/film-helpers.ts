import type {
  CompositionVersion,
  RevisionCutPolicy,
  RevisionHistory,
  RevisionReplay,
  RevisionScene,
} from '@/lib/types/revisions';
import type { T } from '@/i18n';

/** 4 → "4", 4.25 → "4.3". */
function formatSeconds(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  return Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(1);
}

/** "0–4.5 s" for a scene, or null when the frame rate is unknown. */
export function sceneTimeRange(scene: RevisionScene, fps: number | null | undefined): string | null {
  if (!fps || fps <= 0) return null;
  const from = scene.fromFrame / fps;
  const to = (scene.fromFrame + scene.durationInFrames) / fps;
  return `${formatSeconds(from)}–${formatSeconds(to)} s`;
}

function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 1).trimEnd()}…` : text;
}

/**
 * The workflow steps a change makes again after the animation, in the order they run, for the chosen
 * cut. A step only re-made "if the scenes changed" is listed with that condition. `t` is the `revisions.panel` translator.
 */
export function replayStepLines(replay: RevisionReplay, cut: RevisionCutPolicy, t: T): string[] {
  return replay.steps
    .map((step) => ({ step, fate: cut === 'KeepCut' ? step.keepCut : step.reEdit }))
    .filter(({ fate }) => fate === 'Run' || fate === 'KeepIfCompatible')
    .map(({ step, fate }) => (fate === 'KeepIfCompatible'
      ? t('keptUnless', { label: step.label })
      : step.label));
}

/** "Version 2 — Add better graphics in the intro" / "Version 1 — Original". `t` is the `revisions.panel` translator. */
export function versionOptionLabel(version: CompositionVersion, t: T): string {
  return t('versionOption', { number: version.versionNumber, what: version.instruction ? truncate(version.instruction, 60) : t('original') });
}

/**
 * Whether the run's video has anything to show here. A run that never produced an animated video
 * (no versions, and the server says it cannot be revised) shows nothing at all.
 */
export function hasRevisionSurface(history: RevisionHistory | undefined): history is RevisionHistory {
  if (!history || !Array.isArray(history.versions)) return false;
  return history.versions.length > 0 || history.availability !== 'Unavailable';
}

/** How the video's own change is carried out for the chosen cut: the replay that applies, whether the owner picks the cut, and the price. */
export function filmPlan(history: RevisionHistory, cut: RevisionCutPolicy) {
  const replay = history.replay?.available ? history.replay : null;
  const chooseCut = !!replay?.hasEditDecisions;
  const estimate = replay
    ? (cut === 'ReEdit' ? replay.reEditEstimate : replay.keepCutEstimate) ?? history.estimate
    : history.estimate;
  return { replay, chooseCut, estimate };
}
