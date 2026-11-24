/**
 * VF1: the one place the timeline's clip colours come from. The values live in `app/global.css` as
 * `--rb-clip-*` custom properties (light + dark); {@link readClipColors} reads them for the canvas, and
 * {@link CLIP_FALLBACK} is the same table for contexts with no stylesheet (tests, server render). The
 * contrast test (`__tests__/contrast.test.ts`) holds the stylesheet and this table to AA.
 */

import type { PaintTheme } from './paint';

export const CLIP_TOKENS = ['video', 'audio', 'title', 'adjustment', 'generator', 'compound', 'caption', 'transition', 'insert', 'muted', 'selected', 'text', 'chip'] as const;
export type ClipToken = (typeof CLIP_TOKENS)[number];
export type ClipColors = Readonly<Record<ClipToken, string>>;
export type ClipScheme = 'light' | 'dark';

export const CLIP_FALLBACK: Readonly<Record<ClipScheme, ClipColors>> = {
  light: {
    video: '#3f51c7', audio: '#0f7a5a', title: '#7a3fb5', adjustment: '#5b6270', generator: '#586070', compound: '#4a5060',
    caption: '#8a5a00', transition: '#b34f0d', insert: '#0b7285', muted: '#666b75', selected: '#ffd43b', text: '#ffffff', chip: '#15171c',
  },
  dark: {
    video: '#4a5ddb', audio: '#12805f', title: '#8548c4', adjustment: '#5f6675', generator: '#616a7a', compound: '#555c6b',
    caption: '#8f5f00', transition: '#b8530a', insert: '#0b7589', muted: '#454952', selected: '#ffd43b', text: '#ffffff', chip: '#15171c',
  },
};

/** The clip colours for `scheme`, read from the stylesheet's `--rb-clip-*` properties (fallback: the table). */
export function readClipColors(scheme: ClipScheme, root?: Element): ClipColors {
  const fallback = CLIP_FALLBACK[scheme];
  const el = root ?? (typeof document === 'undefined' ? undefined : document.documentElement);
  if (!el || typeof getComputedStyle === 'undefined') return fallback;
  // The stylesheet answers for the attribute's scheme; while it lags `scheme` by a render, the table is right.
  const active = el.getAttribute('data-mantine-color-scheme');
  if (active !== null && active !== scheme) return fallback;
  const style = getComputedStyle(el);
  const out = { ...fallback };
  for (const token of CLIP_TOKENS) {
    const value = style.getPropertyValue(`--rb-clip-${token}`).trim();
    if (value.length > 0) out[token] = value;
  }
  return out;
}

/** The roles of a {@link PaintTheme} that come from the clip tokens. */
export type ClipRoles = 'item' | 'audioClip' | 'mutedClip' | 'itemText' | 'selected' | 'chip';

/** `base` with its clip roles painted from `colors`. */
export function withClipColors(base: Omit<PaintTheme, ClipRoles>, colors: ClipColors): PaintTheme {
  return {
    ...base,
    item: {
      mediaClip: colors.video,
      titleClip: colors.title,
      generatorClip: colors.generator,
      insertClip: colors.insert,
      caption: colors.caption,
      transition: colors.transition,
      compoundClip: colors.compound,
      multicamClip: colors.compound,
      adjustmentClip: colors.adjustment,
    },
    audioClip: colors.audio,
    mutedClip: colors.muted,
    itemText: colors.text,
    selected: colors.selected,
    chip: colors.chip,
  };
}
