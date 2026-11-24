import type { EditTimeline } from '../types';
import { groupFit, proposalIntentOf, reviewKind, type GroupFit, type ProposalReviewKind } from './proposal';
import type { BackgroundEntry } from './results-store';

/**
 * How a landed proposal entry classifies against `doc`, always with the entry's recorded launch placements and surroundings, so the
 * "needs your OK" list and the tray's Apply never disagree (without placements every group looks changed).
 */
export function classifyProposalEntry(doc: EditTimeline, entry: BackgroundEntry): { kind: ProposalReviewKind; fit: GroupFit | null } {
  const intent = proposalIntentOf(entry);
  if (!intent.response) return { kind: 'allStale', fit: null };
  return {
    kind: reviewKind(doc, intent.response, intent.placements, intent.surroundings),
    fit: groupFit(doc, intent.response, intent.placements, undefined, intent.surroundings),
  };
}
