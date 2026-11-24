import { rawMsg } from '@/lib/editing/labels-msg';
import { reasonMsg } from '@/lib/editing/reason-msg';
import { describeSaveStateMsg } from '@/lib/editing/sync/labels-msg';
import type { SaveState } from '@/lib/editing/sync';
import { msg, type T } from '@/i18n';

/** The English place words `placeOf` (lib/editing/sync) puts in front of a reason, mapped to their catalogue word. */
const PLACE_WORD: Record<string, ReturnType<typeof msg>> = {
  track: msg('editor-lib.sync.place.tracks'),
  clip: msg('editor-lib.sync.place.items'),
  sequence: msg('editor-lib.sync.place.sequences'),
  bus: msg('editor-lib.sync.place.buses'),
  marker: msg('editor-lib.sync.place.markers'),
  angle: msg('editor-lib.sync.place.angles'),
  transition: msg('editor-lib.sync.place.transitions'),
  keyframe: msg('editor-lib.sync.place.keys'),
  'colour group': msg('editor-lib.sync.place.colorGroups'),
};
// "Track 1, clip 2: The fade is longer than the clip."
const PLACE_PREFIX = /^((?:[A-Za-z]+(?: group)? \d+)(?:, [a-z]+(?: group)? \d+)*): ([\s\S]*)$/;

/** One sync-client reason in the person's language: the "Track 1, clip 2" prefix and the sentence are both mapped. */
export function syncReasonText(t: Pick<T, 'msg'>, reason: string): string {
  const m = PLACE_PREFIX.exec(reason);
  if (!m) return t.msg(reasonMsg('sync', reason));
  const parts = m[1].split(', ').map((part) => {
    const at = part.lastIndexOf(' ');
    const word = PLACE_WORD[part.slice(0, at).toLowerCase()];
    return word ? t.msg(msg('editor-lib.sync.place.line', { place: t.msg(word), n: Number(part.slice(at + 1)) })) : part;
  });
  const prefix = parts.join(', ');
  return `${prefix.charAt(0).toLocaleUpperCase()}${prefix.slice(1)}: ${t.msg(reasonMsg('sync', m[2]))}`;
}

/** `describeSaveState` in the person's language (the first reason the sync client gave is mapped through the sync table). */
export function saveStateText(t: Pick<T, 'msg'>, state: SaveState): string {
  const first = state.reasons && state.reasons.length > 0 ? state.reasons[0] : null;
  return t.msg(describeSaveStateMsg(state, first ? rawMsg(syncReasonText(t, first)) : null));
}
