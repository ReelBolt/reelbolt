// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clampScopesHeight, DEFAULT_SCOPES_PREFS, GRADE_GRAPH_KEY, parseScopesPrefs, readGraphOpen, readScopesPrefs, resetColourPagePrefsForTests, SCOPE_KINDS, SCOPES_HEIGHT_MAX, SCOPES_HEIGHT_MIN, SCOPES_PREFS_KEY, scopesShown, writeGraphOpen,
  writeScopesPrefs,
} from '../colour-page-prefs';

beforeEach(() => {
  localStorage.clear();
  resetColourPagePrefsForTests();
});
afterEach(() => vi.restoreAllMocks());

describe('scopes preferences', () => {
  it('keeps its keys under the editor prefix', () => {
    expect(SCOPES_PREFS_KEY.startsWith('reelbolt:editor:')).toBe(true);
    expect(GRADE_GRAPH_KEY.startsWith('reelbolt:editor:')).toBe(true);
  });

  it('starts on one waveform at the default height', () => {
    expect(readScopesPrefs()).toEqual(DEFAULT_SCOPES_PREFS);
    expect(scopesShown(readScopesPrefs())).toEqual(['waveform']);
  });

  it('remembers the layout, the scopes and the height on this device', () => {
    writeScopesPrefs({ layout: 'two', pair: ['parade', 'histogram'] });
    writeScopesPrefs({ height: 200 });
    expect(JSON.parse(localStorage.getItem(SCOPES_PREFS_KEY) ?? '{}')).toMatchObject({ layout: 'two', pair: ['parade', 'histogram'], height: 200, single: 'waveform' });
    // A fresh read (another visit) gets the stored choice.
    resetColourPagePrefsForTests();
    expect(readScopesPrefs()).toMatchObject({ layout: 'two', pair: ['parade', 'histogram'], height: 200 });
    expect(scopesShown(readScopesPrefs())).toEqual(['parade', 'histogram']);
  });

  it('shows all four scopes in the 4-up layout, in a fixed order', () => {
    writeScopesPrefs({ layout: 'four' });
    expect(scopesShown(readScopesPrefs())).toEqual(SCOPE_KINDS);
    expect(SCOPE_KINDS).toEqual(['waveform', 'parade', 'vectorscope', 'histogram']);
  });

  it('returns the same object for the same stored text (a stable snapshot)', () => {
    writeScopesPrefs({ layout: 'four' });
    expect(readScopesPrefs()).toBe(readScopesPrefs());
  });

  it('falls back field by field on unreadable or out-of-range values', () => {
    expect(parseScopesPrefs('not json')).toEqual(DEFAULT_SCOPES_PREFS);
    expect(parseScopesPrefs('null')).toEqual(DEFAULT_SCOPES_PREFS);
    expect(parseScopesPrefs(JSON.stringify({ layout: 'nine', single: 'parade', pair: ['parade'], height: 99999 }))).toEqual({ layout: 'single', single: 'parade', pair: DEFAULT_SCOPES_PREFS.pair, height: SCOPES_HEIGHT_MAX });
    expect(clampScopesHeight(1)).toBe(SCOPES_HEIGHT_MIN);
    expect(clampScopesHeight(Number.NaN)).toBe(DEFAULT_SCOPES_PREFS.height);
  });

  it('still works for the visit when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    expect(readScopesPrefs()).toEqual(DEFAULT_SCOPES_PREFS);
    expect(() => writeScopesPrefs({ layout: 'four' })).not.toThrow();
    expect(readScopesPrefs().layout).toBe('four');
  });
});

describe('grade chain band', () => {
  it('is open until it is closed, and remembers that', () => {
    expect(readGraphOpen()).toBe(true);
    writeGraphOpen(false);
    expect(localStorage.getItem(GRADE_GRAPH_KEY)).toBe('closed');
    resetColourPagePrefsForTests();
    expect(readGraphOpen()).toBe(false);
    writeGraphOpen(true);
    expect(readGraphOpen()).toBe(true);
  });
});
