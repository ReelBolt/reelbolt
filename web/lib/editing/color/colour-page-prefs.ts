'use client';

/**
 * Colour depth (pro UI wave 4): what the Colour page remembers PER DEVICE — the scopes layout (one scope,
 * two side by side, all four), which scopes those are, the docked height, and whether the grade chain band
 * is open. Kept in localStorage under `reelbolt:editor:`; never in the document, the account or a render.
 * A blocked or full storage is a soft failure: the page works with the defaults and forgets on reload.
 */

import { useSyncExternalStore } from 'react';

export const SCOPES_PREFS_KEY = 'reelbolt:editor:scopes-layout';
export const GRADE_GRAPH_KEY = 'reelbolt:editor:colour-graph';
const CHANGE_EVENT = 'reelbolt:colour-page-prefs-change';

export const SCOPE_KINDS = ['waveform', 'parade', 'vectorscope', 'histogram'] as const;
export type ScopeKind = (typeof SCOPE_KINDS)[number];
export const SCOPE_LAYOUTS = ['single', 'two', 'four'] as const;
export type ScopesLayout = (typeof SCOPE_LAYOUTS)[number];

export const SCOPES_HEIGHT_MIN = 72;
export const SCOPES_HEIGHT_MAX = 320;
export const SCOPES_HEIGHT_DEFAULT = 112;

export interface ScopesPrefs {
  layout: ScopesLayout;
  /** The scope of the single layout. */
  single: ScopeKind;
  /** The two scopes of the 2-up layout, left then right. */
  pair: readonly [ScopeKind, ScopeKind];
  /** The docked scopes' height in CSS px. */
  height: number;
}

export const DEFAULT_SCOPES_PREFS: ScopesPrefs = Object.freeze({ layout: 'single', single: 'waveform', pair: ['waveform', 'vectorscope'] as const, height: SCOPES_HEIGHT_DEFAULT });

const isKind = (v: unknown): v is ScopeKind => typeof v === 'string' && (SCOPE_KINDS as readonly string[]).includes(v);
const isLayout = (v: unknown): v is ScopesLayout => typeof v === 'string' && (SCOPE_LAYOUTS as readonly string[]).includes(v);

export const clampScopesHeight = (px: number): number => (Number.isFinite(px) ? Math.round(Math.min(SCOPES_HEIGHT_MAX, Math.max(SCOPES_HEIGHT_MIN, px))) : SCOPES_HEIGHT_DEFAULT);

/** Stored text to preferences; anything unreadable or out of range falls back field by field. */
export function parseScopesPrefs(raw: string | null): ScopesPrefs {
  if (!raw) return DEFAULT_SCOPES_PREFS;
  try {
    const v = JSON.parse(raw) as Record<string, unknown> | null;
    if (typeof v !== 'object' || v === null) return DEFAULT_SCOPES_PREFS;
    const pair = Array.isArray(v.pair) && v.pair.length === 2 && isKind(v.pair[0]) && isKind(v.pair[1]) ? ([v.pair[0], v.pair[1]] as const) : DEFAULT_SCOPES_PREFS.pair;
    return {
      layout: isLayout(v.layout) ? v.layout : DEFAULT_SCOPES_PREFS.layout,
      single: isKind(v.single) ? v.single : DEFAULT_SCOPES_PREFS.single,
      pair,
      height: typeof v.height === 'number' ? clampScopesHeight(v.height) : DEFAULT_SCOPES_PREFS.height,
    };
  } catch {
    return DEFAULT_SCOPES_PREFS;
  }
}

/** The scopes a layout shows, in drawing order. */
export function scopesShown(prefs: ScopesPrefs): readonly ScopeKind[] {
  return prefs.layout === 'four' ? SCOPE_KINDS : prefs.layout === 'two' ? prefs.pair : [prefs.single];
}

function readRaw(key: string): string | null {
  try {
    return typeof localStorage !== 'undefined' ? localStorage.getItem(key) : null;
  } catch {
    return null;
  }
}

function writeRaw(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage blocked or full: the choice holds for this visit only (the in-memory copy below).
  }
  memory.set(key, value);
  if (typeof window !== 'undefined') window.dispatchEvent(new Event(CHANGE_EVENT));
}

/** The last value written here, so a blocked storage still remembers within the visit. */
const memory = new Map<string, string>();
const rawOf = (key: string): string | null => readRaw(key) ?? memory.get(key) ?? null;

function subscribe(onChange: () => void): () => void {
  window.addEventListener(CHANGE_EVENT, onChange);
  window.addEventListener('storage', onChange);
  return () => {
    window.removeEventListener(CHANGE_EVENT, onChange);
    window.removeEventListener('storage', onChange);
  };
}

// useSyncExternalStore needs the same object for the same stored text.
let scopesCache: { raw: string | null; value: ScopesPrefs } = { raw: null, value: DEFAULT_SCOPES_PREFS };

export function readScopesPrefs(): ScopesPrefs {
  const raw = rawOf(SCOPES_PREFS_KEY);
  if (raw !== scopesCache.raw) scopesCache = { raw, value: parseScopesPrefs(raw) };
  return scopesCache.value;
}

export function writeScopesPrefs(patch: Partial<ScopesPrefs>): void {
  const next = { ...readScopesPrefs(), ...patch };
  writeRaw(SCOPES_PREFS_KEY, JSON.stringify({ ...next, height: clampScopesHeight(next.height) }));
}

/** This device's scopes layout, live. */
export function useScopesPrefs(): ScopesPrefs {
  return useSyncExternalStore(subscribe, readScopesPrefs, () => DEFAULT_SCOPES_PREFS);
}

/** Whether the grade chain band is open on this device (open unless it was closed). */
export const readGraphOpen = (): boolean => rawOf(GRADE_GRAPH_KEY) !== 'closed';
export const writeGraphOpen = (open: boolean): void => writeRaw(GRADE_GRAPH_KEY, open ? 'open' : 'closed');

export function useGraphOpen(): boolean {
  return useSyncExternalStore(subscribe, readGraphOpen, () => true);
}

/** Tests only: forget what this module remembers outside localStorage. */
export function resetColourPagePrefsForTests(): void {
  memory.clear();
  scopesCache = { raw: null, value: DEFAULT_SCOPES_PREFS };
}
