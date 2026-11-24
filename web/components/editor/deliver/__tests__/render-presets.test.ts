/** Wave 5: the preset model: plan availability, the settings a preset of your own holds, and the two per-device stores. */

import { describe, expect, it } from 'vitest';
import {
  DEFAULT_FORM_SETTINGS,
  MAX_MY_PRESETS,
  MAX_SENT_RENDERS,
  MY_PRESETS_KEY,
  SENT_RENDERS_KEY,
  addMyPreset,
  cleanPresetName,
  presetAvailability,
  presetNameProblem,
  presetPicture,
  presetShortSide,
  readMyPresets,
  readSentRenders,
  rememberSentRender,
  removeMyPreset,
  renameMyPreset,
  sameFormSettings,
  sanitizeFormSettings,
  writeMyPresets,
  type PresetStorage,
  type RenderFormSettings,
} from '../render-presets';

const settings = (over: Partial<RenderFormSettings> = {}): RenderFormSettings => ({ preset: 'Hd1080p16x9', ...DEFAULT_FORM_SETTINGS, ...over });

function memoryStorage(initial: Record<string, string> = {}): PresetStorage & { data: Record<string, string> } {
  const data = { ...initial };
  return { data, getItem: (k) => (k in data ? data[k] : null), setItem: (k, v) => void (data[k] = v) };
}

const refusingStorage: PresetStorage = {
  getItem: () => {
    throw new Error('denied');
  },
  setItem: () => {
    throw new Error('denied');
  },
};

describe('what the plan does with a preset', () => {
  it('refuses only the explicit 4K choice above the plan; shapes and Master are scaled down', () => {
    expect(presetAvailability('Hd1080p16x9', null, 720)).toBe('clamped');
    expect(presetAvailability('Vertical9x16', null, 720)).toBe('clamped');
    expect(presetAvailability('Uhd4k16x9', null, 720)).toBe('refused');
    expect(presetAvailability('Uhd4k16x9', null, 1080)).toBe('refused');
    expect(presetAvailability('Uhd4k16x9', null, 2160)).toBe('ok');
    expect(presetAvailability('Master', 2160, 1080)).toBe('clamped');
    expect(presetAvailability('Master', 1080, 1080)).toBe('ok');
    expect(presetAvailability('AudioOnly', null, 720)).toBe('ok');
  });

  it('has no cap to apply when the plan is unlimited or unknown', () => {
    for (const preset of ['Hd1080p16x9', 'Uhd4k16x9', 'Master', 'AudioOnly'] as const) expect(presetAvailability(preset, 4320, null)).toBe('ok');
  });

  it('knows the picture each preset makes', () => {
    expect(presetPicture('Hd1080p16x9', null)).toEqual({ width: 1920, height: 1080 });
    expect(presetPicture('Portrait4x5', null)).toEqual({ width: 1080, height: 1350 });
    expect(presetPicture('Master', { width: 2048, height: 858 })).toEqual({ width: 2048, height: 858 });
    expect(presetPicture('Master', null)).toBeNull();
    expect(presetPicture('AudioOnly', { width: 1920, height: 1080 })).toBeNull();
    expect(presetShortSide('Vertical9x16', null)).toBe(1080);
  });
});

describe('stored settings are put back inside the form’s limits', () => {
  it('keeps allowed words and clamps every number', () => {
    const cleaned = sanitizeFormSettings({ preset: 'Square1x1', quality: 'best', custom: true, codec: 'H265', crf: 99, speed: 'Slow', rateControl: 'Capped', videoKbps: 5, audioKbps: 9000, normalise: true, targetLufs: -80, truePeak: 4, loudnessRange: 99 });
    expect(cleaned).toEqual({ preset: 'Square1x1', quality: 'best', custom: true, codec: 'H265', crf: 51, speed: 'Slow', rateControl: 'Capped', videoKbps: 200, audioKbps: 320, normalise: true, targetLufs: -36, truePeak: 0, loudnessRange: 30 });
  });

  it('falls back to the defaults for unknown words and refuses an unknown format', () => {
    expect(sanitizeFormSettings({ preset: 'Hd1080p16x9', quality: 'ultra', codec: 'AV1', speed: 7, rateControl: null })).toEqual(settings());
    expect(sanitizeFormSettings({ preset: 'Imax' })).toBeNull();
    expect(sanitizeFormSettings(null)).toBeNull();
    expect(sanitizeFormSettings('Hd1080p16x9')).toBeNull();
  });

  it('compares settings by what they would send', () => {
    expect(sameFormSettings(settings(), settings())).toBe(true);
    expect(sameFormSettings(settings(), settings({ quality: 'best' }))).toBe(false);
  });
});

describe('my presets', () => {
  it('cleans names and says why a name cannot be used', () => {
    expect(cleanPresetName('  Client\n cut  ')).toBe('Client cut');
    expect(cleanPresetName(' \t ')).toBeNull();
    expect(cleanPresetName('x'.repeat(80))?.length).toBe(40);
    const one = addMyPreset([], 'Client cut', settings());
    expect(presetNameProblem('', one)).toBe('empty');
    expect(presetNameProblem('client CUT', one)).toBe('taken');
    expect(presetNameProblem('client cut', one, one[0].id)).toBeNull();
    const full = Array.from({ length: MAX_MY_PRESETS }, (_, i) => ({ id: `p${i}`, name: `P${i}`, settings: settings() }));
    expect(presetNameProblem('One more', full)).toBe('full');
  });

  it('adds, renames and removes, and survives a round trip through storage', () => {
    const storage = memoryStorage();
    let list = addMyPreset([], 'Social', settings({ preset: 'Vertical9x16', quality: 'best' }));
    list = addMyPreset(list, 'Archive', settings({ preset: 'Master' }));
    expect(addMyPreset(list, 'social', settings())).toHaveLength(2);
    list = renameMyPreset(list, list[0].id, 'Social tall');
    expect(writeMyPresets(list, storage)).toBe(true);
    const back = readMyPresets(storage);
    expect(back.available).toBe(true);
    expect(back.presets.map((p) => p.name)).toEqual(['Social tall', 'Archive']);
    expect(back.presets[0].settings.preset).toBe('Vertical9x16');
    expect(removeMyPreset(back.presets, back.presets[0].id).map((p) => p.name)).toEqual(['Archive']);
  });

  it('drops entries it cannot trust and never throws on bad storage', () => {
    const storage = memoryStorage({ [MY_PRESETS_KEY]: JSON.stringify([{ id: 'a', name: 'Good', settings: settings() }, { id: 'b', name: 'Bad', settings: { preset: 'Nope' } }, 'junk', { id: 'a', name: 'Twin', settings: settings() }]) });
    expect(readMyPresets(storage).presets.map((p) => p.name)).toEqual(['Good']);
    expect(readMyPresets(memoryStorage({ [MY_PRESETS_KEY]: '{not json' }))).toEqual({ presets: [], available: true });
    expect(readMyPresets(refusingStorage)).toEqual({ presets: [], available: false });
    expect(readMyPresets(null)).toEqual({ presets: [], available: false });
    expect(writeMyPresets([], refusingStorage)).toBe(false);
    expect(writeMyPresets([], null)).toBe(false);
  });
});

describe('the renders this device started', () => {
  it('remembers what a render was started with and reads it back clean', () => {
    const storage = memoryStorage();
    rememberSentRender('exec-1', { settings: settings({ preset: 'Square1x1' }), range: { inFrame: 10, outFrame: 40 }, fileName: 'teaser.mp4', captions: 'both' }, storage);
    const back = readSentRenders(storage).get('exec-1');
    expect(back).toEqual({ settings: settings({ preset: 'Square1x1' }), range: { inFrame: 10, outFrame: 40 }, fileName: 'teaser.mp4', captions: 'both' });
  });

  it('refuses a stored range or file name the form could not have sent, and keeps only the newest entries', () => {
    const storage = memoryStorage({ [SENT_RENDERS_KEY]: JSON.stringify([['x', { settings: settings(), range: { inFrame: 40, outFrame: 10 }, fileName: '../escape.mp4', captions: 'loud' }]]) });
    expect(readSentRenders(storage).get('x')).toEqual({ settings: settings(), range: null, fileName: '', captions: null });
    for (let i = 0; i < MAX_SENT_RENDERS + 5; i++) rememberSentRender(`e${i}`, { settings: settings(), range: null, fileName: '', captions: null }, storage);
    const all = readSentRenders(storage);
    expect(all.size).toBe(MAX_SENT_RENDERS);
    expect(all.has('x')).toBe(false);
    expect(all.has(`e${MAX_SENT_RENDERS + 4}`)).toBe(true);
  });

  it('reads nothing from storage that refuses, and still answers for this visit', () => {
    expect(readSentRenders(refusingStorage).size).toBe(0);
    const kept = rememberSentRender('e', { settings: settings(), range: null, fileName: '', captions: null }, refusingStorage);
    expect(kept.has('e')).toBe(true);
  });
});
