'use client';

import React, { createContext, useContext } from 'react';
import { ariaChord, displayChord, type KeyAction } from '@/lib/editing/keymap';

/**
 * T-014: the editor's live chords (defaults plus the user's remaps) for controls deep in the tree. A control asks
 * for its action and gets the chord to show (`display`, for tooltips) and to announce (`aria`, for
 * `aria-keyshortcuts`); an unbound action yields nothing. Outside a provider every answer is empty.
 */
export type LiveChords = Readonly<Partial<Record<KeyAction, readonly string[]>>>;

const LiveChordsContext = createContext<LiveChords>({});

const IS_MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform ?? '');

export function LiveChordsProvider({ chords, children }: { chords: LiveChords; children: React.ReactNode }) {
  return <LiveChordsContext.Provider value={chords}>{children}</LiveChordsContext.Provider>;
}

export interface LiveChord {
  display: string | undefined;
  aria: string | undefined;
  /** `label (chord)`, or the label alone when the action has no chord. */
  tip: (label: string) => string;
}

export function useLiveChord(action: KeyAction): LiveChord {
  const chord = useContext(LiveChordsContext)[action]?.[0];
  const display = chord ? displayChord(chord, IS_MAC) : undefined;
  return { display, aria: chord ? ariaChord(chord, IS_MAC) : undefined, tip: (label) => (display ? `${label} (${display})` : label) };
}

/**
 * T-018: one lookup for a control that lists many actions (a context menu, a stepper row). It answers
 * the first live chord as display text; an action another remap beat, or one that is unbound, answers nothing.
 */
export function useLiveChordLookup(): (action: KeyAction) => string | undefined {
  const chords = useContext(LiveChordsContext);
  return (action) => {
    const chord = chords[action]?.[0];
    return chord ? displayChord(chord, IS_MAC) : undefined;
  };
}
