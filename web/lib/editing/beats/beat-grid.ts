/**
 * Beat grid for "Cut to the music": the defensive reader of the API's beat analysis, the manual
 * correction (half/double tempo, nudge, bar shift) and the grid lines at a chosen unit. Pure; all
 * times are SOURCE seconds of the music file.
 */

export interface BeatAnalysis {
  bpm: number;
  beatSec: number;
  beatPhaseSec: number;
  barPhaseSec: number;
  beatsPerBar: number;
  beatTimesSec: number[];
  firstDownbeatIndex: number;
  durationSec: number;
  analysedSec: number;
  dropSec: number | null;
  outroSec: number | null;
  confidence: number;
  steady: boolean;
  tempoWord: string;
  halfTempoScore: number;
  doubleTempoScore: number;
  truncated: boolean;
}

export const MAX_BEATS = 20000;

export type BeatTempo = 'asFound' | 'half' | 'double';

export interface BeatCorrection {
  tempo: BeatTempo;
  /** -500..500 */
  nudgeMs: number;
  barShift: 0 | 1 | 2 | 3;
}

export const NO_CORRECTION: BeatCorrection = { tempo: 'asFound', nudgeMs: 0, barShift: 0 };

export interface BeatGrid {
  /** Beat times, source seconds, increasing. */
  beats: number[];
  /** Index into `beats` of a downbeat; downbeats repeat every `beatsPerBar` beats (also backwards). */
  downbeatIndex: number;
  beatsPerBar: number;
  bpm: number;
}

export type GridUnit = 'beat' | 'twoBeats' | 'bar' | 'twoBars' | 'fourBars';

export interface GridLine {
  sec: number;
  bar: boolean;
  barNumber: number;
}

const isNum = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const num = (v: unknown, fallback: number): number => (isNum(v) ? v : fallback);
const numOrNull = (v: unknown): number | null => (isNum(v) ? v : null);

/** Reads the API JSON; null when it lacks a usable tempo. Beats are filtered to finite, sorted, capped. */
export function readBeatAnalysis(json: unknown): BeatAnalysis | null {
  if (typeof json !== 'object' || json === null) return null;
  const o = json as Record<string, unknown>;
  if (!isNum(o.bpm) || o.bpm <= 0 || !isNum(o.beatSec) || o.beatSec <= 0 || !isNum(o.durationSec) || o.durationSec < 0) return null;
  const rawBeats = Array.isArray(o.beatTimesSec) ? (o.beatTimesSec as unknown[]) : [];
  const beatTimesSec = rawBeats.filter(isNum).sort((a, b) => a - b).slice(0, MAX_BEATS);
  const bpbRaw = num(o.beatsPerBar, 4);
  const beatsPerBar = Number.isInteger(bpbRaw) && bpbRaw >= 1 && bpbRaw <= 16 ? bpbRaw : 4;
  const fdRaw = num(o.firstDownbeatIndex, 0);
  const firstDownbeatIndex = Number.isInteger(fdRaw) && fdRaw >= 0 ? fdRaw : 0;
  return {
    bpm: o.bpm,
    beatSec: o.beatSec,
    beatPhaseSec: num(o.beatPhaseSec, 0),
    barPhaseSec: num(o.barPhaseSec, 0),
    beatsPerBar,
    beatTimesSec,
    firstDownbeatIndex,
    durationSec: o.durationSec,
    analysedSec: num(o.analysedSec, o.durationSec),
    dropSec: numOrNull(o.dropSec),
    outroSec: numOrNull(o.outroSec),
    confidence: num(o.confidence, 0),
    steady: o.steady === true,
    tempoWord: typeof o.tempoWord === 'string' ? o.tempoWord : '',
    halfTempoScore: num(o.halfTempoScore, 0),
    doubleTempoScore: num(o.doubleTempoScore, 0),
    truncated: o.truncated === true,
  };
}

const mod = (a: number, n: number): number => ((a % n) + n) % n;

/** Applies the manual correction. Order: tempo, then nudge, then bar shift. */
export function buildBeatGrid(analysis: BeatAnalysis, correction: BeatCorrection = NO_CORRECTION): BeatGrid {
  let beats: number[];
  let downbeat: number;
  if (analysis.beatTimesSec.length === 0) {
    beats = [];
    if (analysis.beatSec > 0) {
      const first = mod(analysis.beatPhaseSec, analysis.beatSec);
      for (let t = first; t <= analysis.durationSec + 1e-9 && beats.length < MAX_BEATS; t = first + beats.length * analysis.beatSec) beats.push(t);
    }
    downbeat = mod(Math.round((analysis.barPhaseSec - (beats[0] ?? 0)) / (analysis.beatSec || 1)), analysis.beatsPerBar);
  } else {
    beats = [...analysis.beatTimesSec];
    downbeat = Math.min(analysis.firstDownbeatIndex, beats.length - 1);
  }
  let bpm = analysis.bpm;

  if (correction.tempo === 'half' && beats.length > 0) {
    const parity = downbeat % 2;
    beats = beats.filter((_, i) => i % 2 === parity);
    downbeat = Math.floor(downbeat / 2);
    bpm /= 2;
  } else if (correction.tempo === 'double' && beats.length > 0) {
    const out: number[] = [];
    for (let i = 0; i < beats.length; i++) {
      out.push(beats[i]);
      const interval = i + 1 < beats.length ? beats[i + 1] - beats[i] : i > 0 ? beats[i] - beats[i - 1] : analysis.beatSec;
      out.push(beats[i] + interval / 2);
    }
    beats = out;
    downbeat *= 2;
    bpm *= 2;
  }

  const nudge = Math.max(-500, Math.min(500, Math.round(correction.nudgeMs))) / 1000;
  if (nudge !== 0) beats = beats.map((b) => b + nudge);

  return { beats, downbeatIndex: downbeat + correction.barShift, beatsPerBar: analysis.beatsPerBar, bpm };
}

const UNIT_STEP: Record<GridUnit, (bpb: number) => number> = {
  beat: () => 1,
  twoBeats: () => 2,
  bar: (bpb) => bpb,
  twoBars: (bpb) => 2 * bpb,
  fourBars: (bpb) => 4 * bpb,
};

/** The grid's lines at `unit`; every unit is aligned to the downbeat. Bar 1 is the downbeat at `downbeatIndex`. */
export function gridLines(grid: BeatGrid, unit: GridUnit): GridLine[] {
  const bpb = Math.max(1, grid.beatsPerBar);
  const step = UNIT_STEP[unit](bpb);
  const out: GridLine[] = [];
  grid.beats.forEach((sec, i) => {
    const rel = i - grid.downbeatIndex;
    if (mod(rel, step) !== 0) return;
    out.push({ sec, bar: mod(rel, bpb) === 0, barNumber: Math.floor(rel / bpb) + 1 });
  });
  return out;
}
