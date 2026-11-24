// ReelBolt block library: caption helpers shared by the caption-* blocks (MIT).
import type {Caption} from '@remotion/captions';

export type CaptionPage = {startMs: number; endMs: number; words: Caption[]};

/** Group word-level captions into pages of at most `maxWords`, also breaking on a pause longer than `maxGapMs`. */
export const groupIntoPages = (captions: Caption[], maxWords = 4, maxGapMs = 500): CaptionPage[] => {
  const pages: CaptionPage[] = [];
  for (const word of captions) {
    const last = pages[pages.length - 1];
    if (last && last.words.length < maxWords && word.startMs - last.endMs <= maxGapMs) {
      last.words.push(word);
      last.endMs = word.endMs;
    } else {
      pages.push({startMs: word.startMs, endMs: word.endMs, words: [word]});
    }
  }
  return pages;
};

/** The page to show at `ms`: it stays up `holdMs` after its last word, until the next page starts. */
export const pageAt = (pages: CaptionPage[], ms: number, holdMs = 250): CaptionPage | null => {
  for (let i = pages.length - 1; i >= 0; i--) {
    if (ms >= pages[i].startMs) {
      return ms < pages[i].endMs + holdMs ? pages[i] : null;
    }
  }
  return null;
};

/** Index of the word being spoken at `ms` (the latest word that has started), or -1 before the first. */
export const activeWordIndex = (page: CaptionPage, ms: number): number => {
  let index = -1;
  page.words.forEach((word, i) => {
    if (ms >= word.startMs) index = i;
  });
  return index;
};

/** Word text without the leading space many caption sources carry. */
export const wordText = (word: Caption): string => word.text.trim();

/** Sample captions for previews and docs: a short sentence, one word per ~400 ms. */
export const SAMPLE_CAPTIONS: Caption[] = 'Make every second count with clear captions that people can follow'
  .split(' ')
  .map((text, i) => ({
    text: i === 0 ? text : ` ${text}`,
    startMs: 300 + i * 380,
    endMs: 300 + i * 380 + 340,
    timestampMs: null,
    confidence: null,
  }));
