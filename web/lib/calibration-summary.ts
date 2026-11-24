import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import type {
  CalibrationVerdict,
  DecisionCalibrationSite,
  ThresholdSweepPoint,
} from './types/decision-calibration';

/** A decision site is named `Agent.Question` (e.g. `Colorist.Look`); the agent is what an operator gates. */
export const agentOf = (site: string) => site.split('.')[0];

export interface AgentSummary {
  agent: string;
  pairs: DecisionCalibrationSite[];
  observationCount: number;
  /** Observation-weighted mean of the pairs' agreement rates. */
  agreementRate: number;
  /** Share the model would handle alone at the report's thresholds (1 - weighted escalation). */
  acceptRate: number;
  /** Projected, read from the sweep point nearest the selected acceptAt. */
  tokensSaved: number;
  verdict: CalibrationVerdict;
  suggestedAcceptAt: number | null;
  reason: string;
}

const nearest = (sweep: ThresholdSweepPoint[], acceptAt: number) =>
  sweep.reduce<ThresholdSweepPoint | null>(
    (best, p) => (best === null || Math.abs(p.acceptAt - acceptAt) < Math.abs(best.acceptAt - acceptAt) ? p : best),
    null
  );

const weighted = (pairs: DecisionCalibrationSite[], pick: (s: DecisionCalibrationSite) => number) => {
  const n = pairs.reduce((a, s) => a + s.observationCount, 0);
  return n === 0 ? 0 : pairs.reduce((a, s) => a + pick(s) * s.observationCount, 0) / n;
};

/**
 * Rolls the per-question entries up to one summary per agent. The verdict is the most cautious one among
 * the agent's questions: Gate replaces the agent for *every* question it asks (all-or-nothing), so one
 * unready question keeps the whole agent in Shadow.
 */
export function summarizeAgents(sites: DecisionCalibrationSite[], acceptAt: number, t: T = enT): AgentSummary[] {
  const groups = new Map<string, DecisionCalibrationSite[]>();
  for (const s of sites) groups.set(agentOf(s.site), [...(groups.get(agentOf(s.site)) ?? []), s]);
  return [...groups.entries()].map(([agent, pairs]) => {
    const verdicts = pairs.map((p) => p.recommendation);
    const worst: CalibrationVerdict = verdicts.some((v) => v.verdict === 'NotEnoughData')
      ? 'NotEnoughData'
      : verdicts.some((v) => v.verdict === 'KeepShadow')
        ? 'KeepShadow'
        : 'TryGate';
    const lead = verdicts.find((v) => v.verdict === worst) ?? verdicts[0];
    const suggested =
      worst === 'TryGate'
        ? Math.max(...verdicts.map((v) => v.suggestedAcceptAt ?? 0))
        : null;
    return {
      agent,
      pairs,
      observationCount: pairs.reduce((a, s) => a + s.observationCount, 0),
      agreementRate: weighted(pairs, (s) => s.agreementRate),
      acceptRate: 1 - weighted(pairs, (s) => s.escalationRate),
      tokensSaved: pairs.reduce((a, s) => a + (nearest(s.thresholdSweep, acceptAt)?.tokensSaved ?? 0), 0),
      verdict: worst,
      suggestedAcceptAt: suggested,
      reason: worst === 'TryGate' ? t('libLabels.calibrationTryGate', { value: (suggested ?? 0).toFixed(2) }) : lead.reason,
    };
  });
}

/**
 * English verdict labels; components use `verdictLabel(t, verdict)`.
 * @deprecated Use `verdictLabel(t, verdict)` (translator-taking) so the text follows the active language.
 */
export const VERDICT_LABEL: Record<CalibrationVerdict, string> = {
  NotEnoughData: enT('libLabels.verdicts.NotEnoughData'),
  KeepShadow: enT('libLabels.verdicts.KeepShadow'),
  TryGate: enT('libLabels.verdicts.TryGate'),
};

export function verdictLabel(t: T, verdict: CalibrationVerdict): string {
  return t(`libLabels.verdicts.${verdict}`);
}
export const VERDICT_COLOR: Record<CalibrationVerdict, string> = {
  NotEnoughData: 'gray',
  KeepShadow: 'orange',
  TryGate: 'teal',
};
