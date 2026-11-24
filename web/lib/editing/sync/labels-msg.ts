import { msg, type Msg } from '@/i18n/msg';
import type { SaveState } from './sync-client';

/** i18n counterpart of sync-client.ts `describeSaveState` / `MEMORY_ONLY` / `SIGN_IN_AGAIN`. Catalogue: `editor-lib.sync.state.*`. */

const K = 'editor-lib.sync.state';

export const memoryOnlyMsg = (): Msg => msg(`${K}.memoryOnly`);
export const signInAgainMsg = (): Msg => msg(`${K}.signInAgain`);

/**
 * The chip label for a save state, as a message. `state.reasons[0]` (the first reason, English sentences
 * built by `reasonsFor`) is mapped with `reasonMsg('sync', reason)` by the caller and passed as `whyMsg`
 * (already a Msg, or null); it is placed after the sentence the way the English one is.
 */
export function describeSaveStateMsg(state: SaveState, whyMsg: Msg | null): Msg {
  const memory = state.durability === 'memory' ? msg(`${K}.memorySuffix`) : '';
  if (state.viewOnly) return msg(`${K}.viewOnly`);
  switch (state.status) {
    case 'saved':
      return msg(`${K}.saved`);
    case 'dirty':
      return msg(`${K}.dirty`, { memory });
    case 'saving':
      return msg(`${K}.saving`);
    case 'retrying':
      return msg(`${K}.retrying`, { memory });
    case 'invalid':
      return whyMsg ? msg(`${K}.invalid`, { why: whyMsg }) : msg(`${K}.invalidDefault`);
    case 'rejected':
      if (state.problem === 'tooLarge') return msg(`${K}.tooLarge`);
      return whyMsg ? msg(`${K}.rejectedWhy`, { why: whyMsg }) : msg(`${K}.rejected`);
    case 'conflict':
      return msg(`${K}.conflict`);
    case 'error':
      if (state.problem === 'noPermission') return msg(`${K}.noPermission`);
      if (state.problem === 'deleted') return msg(`${K}.deleted`);
      return msg(`${K}.error`, { memory });
    case 'offline':
      return msg(`${K}.offline`, { n: state.queuedOps ?? 0, memory });
    case 'reconnecting':
      return msg(`${K}.reconnecting`);
    case 'authExpired':
      return signInAgainMsg();
    case 'otherTab':
      return msg(`${K}.otherTab`);
    case 'takenOver':
      return msg(`${K}.takenOver`);
    default:
      return msg('editor-lib.common.raw', { text: '' });
  }
}
