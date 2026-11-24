import { msg, type Msg } from '@/i18n/msg';
import { ApiError } from '@/lib/api/client';
import { reasonMsg } from '../reason-msg';
import { rawMsg } from '../sentence';
import { eventTypeCounts, type AnalysisKind, type KindStatus } from './media-analysis';

/** i18n counterparts of media-analysis.ts. Catalogue `editor-lib.mediaAnalysis.*`; the `beats` kind
 * (only the analyse panel offers it) reads its two sentences and tempo words from that panel. */

/** KIND_LABEL[kind] */
export const analysisKindLabelMsg = (kind: AnalysisKind): Msg => (kind === 'beats' ? msg('editor.panels.analysePanel.beats') : msg(`editor-lib.mediaAnalysis.kinds.${kind}`));

/** The service's tempo word (`slow`, `medium`, `fast`, `very fast`) as a translated word. */
const TEMPO_WORD_MSG: Record<string, Msg> = {
  slow: msg('editor.panels.analysePanel.tempoSlow'),
  medium: msg('editor.panels.analysePanel.tempoMedium'),
  fast: msg('editor.panels.analysePanel.tempoFast'),
  'very fast': msg('editor.panels.analysePanel.tempoVeryFast'),
};
export const tempoWordMsg = (word: string): Msg => TEMPO_WORD_MSG[word] ?? rawMsg(word);

/** describeKind(kind, status). A failure `reason` from the service shows as it is. */
export function describeKindMsg(kind: AnalysisKind, status: KindStatus): Msg {
  const s = status.summary ?? {};
  const n = (key: string): number => (typeof s[key] === 'number' ? (s[key] as number) : 0);
  const K = 'editor-lib.mediaAnalysis';
  switch (status.state) {
    case 'queued':
      return msg(`${K}.queued`);
    case 'running':
      return msg(`${K}.running`);
    case 'failed':
      return status.reason && status.reason.length > 0 ? rawMsg(status.reason) : msg(`${K}.failed`);
    case 'ready':
      if (kind === 'silence') return msg(`${K}.summary.silence`, { n: n('rangeCount') });
      if (kind === 'shots') return msg(`${K}.summary.shots`, { n: n('shotCount') });
      if (kind === 'motion') return msg(`${K}.summary.motion`, { n: Math.round(n('stillSeconds')) });
      if (kind === 'events') {
        const types = eventTypeCounts(status.summary);
        return types ? msg(`${K}.summary.eventsByType`, { n: n('eventCount'), types }) : msg(`${K}.summary.events`, { n: n('eventCount') });
      }
      if (kind === 'beats') {
        const bpm = Math.round(n('bpm'));
        const word = typeof s.tempoWord === 'string' ? s.tempoWord : '';
        return word.length > 0 ? msg('editor.panels.analysePanel.beatsSummary', { bpm, tempoWord: tempoWordMsg(word) }) : msg('editor.panels.analysePanel.beatsSummaryShort', { bpm });
      }
      return n('estimatedWordCount') > 0 ? msg(`${K}.summary.wordsRough`, { n: n('wordCount'), rough: n('estimatedWordCount') }) : msg(`${K}.summary.words`, { n: n('wordCount') });
    default:
      return msg(`${K}.none`);
  }
}

/** describeAnalysisError(error): the API's own message still shows as it is where the lib shows it. */
export function describeAnalysisErrorMsg(error: unknown): Msg {
  if (error instanceof ApiError) {
    if (error.status === 402) return msg('editor-lib.mediaAnalysis.err402');
    if (error.status === 409) return error.message ? rawMsg(error.message) : msg('editor-lib.mediaAnalysis.err409');
    if (error.status === 404) return msg('editor-lib.mediaAnalysis.err404');
    if (error.status === 403) return msg('editor-lib.mediaAnalysis.err403');
    return error.message ? rawMsg(error.message) : msg('editor-lib.mediaAnalysis.errStart');
  }
  return msg('editor-lib.mediaAnalysis.errStart');
}
/** Anything already produced by the English functions (`describeAnalysisError(...)`): `analysisTextMsg(text)`. */
export const analysisTextMsg = (text: string): Msg => reasonMsg('mediaAnalysis', text);
