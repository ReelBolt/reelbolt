import { msg } from '@/i18n/msg';
import { useT } from '@/i18n';

/** The translator these helpers need: `useT('changeRequests')`, or any function with this shape. */
export type CrT = (key: string, values?: Record<string, string | number>) => string;

export function useCrT(): CrT {
  return useT('changeRequests');
}

/** The entry-point buttons as messages: resolve with `useT().msg(CR_BUTTON.button)`. */
export const CR_BUTTON = {
  button: msg('changeRequests.button'),
  filmButton: msg('changeRequests.filmButton'),
} as const;

/**
 * @deprecated English literals kept only because components/workflows/StepResultPanel.tsx (another lane) still
 * reads `CR_COPY.button` / `CR_COPY.filmButton` as strings. Switch it to `t.msg(CR_BUTTON.*)`, then delete this.
 */
export const CR_COPY = {
  button: 'Request changes',
  filmButton: 'Request changes to the video',
} as const;

/** Every word the change-request surfaces show, in one place for copy edits. */
export function crCopy(t: CrT) {
  return {
    modalTitle: (label: string) => t('modalTitle', { label }),
    intro: t('intro'),
    introDirect: t('introDirect'),
    scopeItems: (kind: ItemKind) => t('scopeItems', { kind }),
    scopeAll: (kind: ItemKind) => t('scopeAll', { kind }),
    pickItems: (kind: ItemKind) => t('pickItems', { kind }),
    noItems: t('noItems'),
    keepAsIs: t('keepAsIs'),
    voicePlaceholder: t('voicePlaceholder'),
    sayItLabel: t('sayItLabel'),
    sayItHelp: t('sayItHelp'),
    sayItTerm: t('sayItTerm'),
    sayItHow: t('sayItHow'),
    sayItAdd: t('sayItAdd'),
    sayItRemove: t('sayItRemove'),
    instruction: t('instruction'),
    instructionRewrite: t('instructionRewrite'),
    instructionRewritePlaceholder: t('instructionRewritePlaceholder'),
    instructionPlaceholder: t('instructionPlaceholder'),
    redone: t('redone'),
    reused: t('reused'),
    nothingChosen: t('nothingChosen'),
    noLines: t('noLines'),
    priceAbout: (low: string, high: string) => (low === high ? t('priceAboutOne', { low }) : t('priceAboutRange', { low, high })),
    thisPart: (credits: string) => t('thisPart', { credits }),
    shortfall: t('shortfall'),
    shortfallBody: t('shortfallBody'),
    topUp: t('topUp'),
    estimating: t('estimating'),
    confirm: t('confirm'),
    cancel: t('cancel'),
    historyTitle: t('historyTitle'),
    historyChangedFrom: t('historyChangedFrom'),
    historyOwnRun: t('historyOwnRun'),
    historyRedone: (n: number) => t('historyRedone', { count: n }),
    historyReused: (n: number) => t('historyReused', { count: n }),
    historyCost: (c: string) => t('historyCost', { credits: c }),
    historyOpen: t('historyOpen'),
    lineWord: t('lineWord'),
  };
}

/** A standing cost warning per `costNote` key the server sends. An unknown key shows nothing. */
export function costNote(t: CrT, key: string): string | null {
  return key === 'script' ? t('costNotes.script') : null;
}

/** What a step's items are called: a kind that the messages turn into the right words ("Choose lines"). */
export type ItemKind = 'line' | 'clip' | 'take' | 'scene' | 'item';

const ITEM_KINDS: Record<string, ItemKind> = {
  Voiceover: 'line',
  VideoGenerate: 'clip',
  AudioGenerate: 'take',
  Line: 'line',
  Shot: 'clip',
  Take: 'take',
  Scene: 'scene',
};

export function itemKindOf(stepType: string, itemKind: string): ItemKind {
  return ITEM_KINDS[stepType] ?? ITEM_KINDS[itemKind] ?? 'item';
}

export interface KeyCopy {
  label: string;
  help?: string;
  placeholder?: string;
  /** Unit shown after a number in the help line ("seconds"). */
  unit?: string;
}

/** Plain-language label and help for each override key. A step-specific entry (`VideoGenerate.prompt`) wins over the key's own. */
const KEY_COPY: Record<string, (t: CrT) => KeyCopy> = {
  text: (t) => ({ label: t('keys.text.label') }),
  voiceId: (t) => ({ label: t('keys.voiceId.label'), placeholder: t('voicePlaceholder') }),
  speed: (t) => ({ label: t('keys.speed.label'), help: t('keys.speed.help') }),
  pronunciation: (t) => ({ label: t('sayItLabel'), help: t('sayItHelp') }),
  prompt: (t) => ({ label: t('keys.prompt.label'), help: t('keys.prompt.help') }),
  'VideoGenerate.prompt': (t) => ({ label: t('keys.videoPrompt.label'), help: t('keys.videoPrompt.help') }),
  'AudioGenerate.prompt': (t) => ({ label: t('keys.audioPrompt.label'), help: t('keys.audioPrompt.help') }),
  voiceover: (t) => ({ label: t('keys.voiceover.label'), help: t('keys.voiceover.help') }),
  onScreenText: (t) => ({ label: t('keys.onScreenText.label'), help: t('keys.onScreenText.help') }),
  visualDescription: (t) => ({ label: t('keys.visualDescription.label') }),
  retake: (t) => ({ label: t('keys.retake.label'), help: t('keys.retake.help') }),
  'AudioGenerate.retake': (t) => ({ label: t('keys.audioRetake.label'), help: t('keys.audioRetake.help') }),
  durationSec: (t) => ({ label: t('keys.durationSec.label'), unit: t('keys.durationSec.unit') }),
  overlayText: (t) => ({ label: t('keys.overlayText.label'), help: t('keys.overlayText.help') }),
  atTimeSec: (t) => ({ label: t('keys.atTimeSec.label'), help: t('keys.atTimeSec.help'), unit: t('keys.atTimeSec.unit') }),
  overlayPosition: (t) => ({ label: t('keys.overlayPosition.label') }),
  overlayFontColor: (t) => ({ label: t('keys.overlayFontColor.label') }),
  overlayBoxColor: (t) => ({ label: t('keys.overlayBoxColor.label') }),
};

const VALUE_COPY: Record<string, Record<string, (t: CrT) => string>> = {
  overlayPosition: {
    None: (t) => t('values.overlayPosition.None'), LowerThird: (t) => t('values.overlayPosition.LowerThird'),
    UpperThird: (t) => t('values.overlayPosition.UpperThird'), Center: (t) => t('values.overlayPosition.Center'),
  },
  overlayFontColor: {
    white: (t) => t('values.overlayFontColor.white'), black: (t) => t('values.overlayFontColor.black'), yellow: (t) => t('values.overlayFontColor.yellow'),
  },
  overlayBoxColor: {
    'black@0.45': (t) => t('values.overlayBoxColor.black045'),
    'black@0.6': (t) => t('values.overlayBoxColor.black06'),
    'white@0.4': (t) => t('values.overlayBoxColor.white04'),
    none: (t) => t('values.overlayBoxColor.none'),
  },
};

/** `someNewKey` -> `Some new key`: the fallback for a key the copy file does not know yet. */
export function humanise(key: string): string {
  const spaced = key
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim()
    .toLowerCase();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : key;
}

export function keyCopy(t: CrT, stepType: string, key: string): KeyCopy {
  const build = KEY_COPY[`${stepType}.${key}`] ?? KEY_COPY[key];
  return build ? build(t) : { label: humanise(key) };
}

export function valueLabel(t: CrT, key: string, value: string): string {
  const build = VALUE_COPY[key]?.[value];
  return build ? build(t) : humanise(value);
}

const STATUS_COPY: Record<string, (t: CrT) => string> = {
  completed: (t) => t('status.ready'),
  succeeded: (t) => t('status.ready'),
  failed: (t) => t('status.failed'),
  skipped: (t) => t('status.skipped'),
};

export function statusLabel(t: CrT, status: string | null): string | null {
  if (!status) return null;
  const build = STATUS_COPY[status.toLowerCase()];
  return build ? build(t) : humanise(status);
}

/** A person's name for an item id: `v2` -> "Clip 2", `v2t3` -> "Take 3", `a1` -> "Take 1". Any other id is shown as it is. */
export function itemName(t: CrT, stepType: string, id: string): string {
  if (stepType === 'VideoGenerate') {
    const take = /^v(\d+)t(\d+)$/.exec(id);
    if (take) return t('itemTake', { n: take[2] });
    const shot = /^v(\d+)$/.exec(id);
    if (shot) return t('itemClip', { n: shot[1] });
  }
  if (stepType === 'AudioGenerate') {
    const take = /^a(\d+)$/.exec(id);
    if (take) return t('itemTake', { n: take[1] });
  }
  return id;
}

/** The item's name for a message that must stand alone: "Clip 2, take 3". */
export function itemFullName(t: CrT, stepType: string, id: string): string {
  const take = stepType === 'VideoGenerate' ? /^v(\d+)t(\d+)$/.exec(id) : null;
  return take ? t('itemClipTake', { clip: take[1], take: take[2] }) : itemName(t, stepType, id);
}

/** The shot a take belongs to (`v2t3` -> `v2`); null for anything else. */
export function shotOf(stepType: string, id: string): string | null {
  if (stepType !== 'VideoGenerate') return null;
  const take = /^(v\d+)t\d+$/.exec(id);
  return take ? take[1] : null;
}
