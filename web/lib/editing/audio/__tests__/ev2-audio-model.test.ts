/** Wave 2 (R08.2-R08.7): the pure parts of the audio model. */
import { describe, expect, it } from 'vitest';
import { FADER_MAX_DB, FADER_MIN_DB, clampFaderDb, formatFaderDb, formatPan, parseFaderDb } from '../fader';
import { LOUDNESS_TARGETS, compileMatchTarget, describeVerdict, judgeLoudness, targetById } from '../loudness-target';
import { auditionRange, isJobDone, isJobFailed, waitForAudioJob, type AudioJob } from '../audio-api';
import { compileDuck, compileRemoveDuck, defaultDuckConfig, duckIsStale, guessDuckTracks, loadDuckConfig, resolveDuck, saveDuckConfig } from '../duck-state';
import { compilePlaceVoiceover, meterFill, micErrorSentence } from '../vo-recorder';
import { compileCreateMulticam } from '../../structure/multicam';
import { apply as applyOp } from '../../engine';
import { CLEAN_VOICE, compileCleanVoice, isCleanVoice } from '../../../../components/editor/audio/CleanVoice';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';

describe('R08.3 fader', () => {
  it('parses typed dB, clamps to the scale, and maps -inf to the bottom', () => {
    expect(parseFaderDb('-6')).toBe(-6);
    expect(parseFaderDb('+3.5 dB')).toBe(3.5);
    expect(parseFaderDb('−6')).toBe(-6);
    expect(parseFaderDb('99')).toBe(FADER_MAX_DB);
    expect(parseFaderDb('-inf')).toBe(FADER_MIN_DB);
    expect(parseFaderDb('-500')).toBe(FADER_MIN_DB);
    expect(parseFaderDb('loud')).toBeNull();
    expect(parseFaderDb('')).toBeNull();
    expect(clampFaderDb(Number.NaN)).toBe(FADER_MIN_DB);
  });
  it('formats the readout', () => {
    expect(formatFaderDb(0)).toBe('0.0');
    expect(formatFaderDb(2)).toBe('+2.0');
    expect(formatFaderDb(-3.54)).toBe('-3.5');
    expect(formatFaderDb(-60)).toBe('−∞');
    expect(formatPan(undefined)).toBe('C');
    expect(formatPan(-0.35)).toBe('L35');
    expect(formatPan(0.2)).toBe('R20');
  });
});

describe('R08.4 loudness target', () => {
  it('offers the three named targets', () => {
    expect(LOUDNESS_TARGETS.map((t) => [t.label, t.lufs])).toEqual([['Social', -14], ['Podcast', -16], ['Broadcast', -23]]);
    expect(targetById('nope').lufs).toBe(-14);
  });
  it('judges a measurement against the target within 1 LU', () => {
    expect(judgeLoudness(-14.8, -14).kind).toBe('on');
    expect(judgeLoudness(-20, -14)).toEqual({ kind: 'quiet', delta: 6 });
    expect(judgeLoudness(-9, -14)).toEqual({ kind: 'loud', delta: -5 });
    expect(judgeLoudness(Number.NEGATIVE_INFINITY, -14).kind).toBe('unmeasurable');
    expect(describeVerdict({ kind: 'quiet', delta: 6 }, 'Social')).toBe('6.0 dB too quiet for Social.');
  });
  it('Match target shifts every unmuted audio fader (and keys) and clamps at +12', () => {
    const d = doc([
      track(TRACKS.a1, 'audio', [media('a', 0, 100)], { gainDb: { value: -3, keys: null } }),
      track(TRACKS.a2, 'audio', [media('b', 0, 100)], { gainDb: { value: 10, keys: [{ frame: 0, value: 10, interp: 'linear' }] } }),
      track('muted', 'audio', [media('c', 0, 100)], { mute: true }),
    ]);
    const ops = compileMatchTarget(d, 6);
    expect(ops).toHaveLength(2);
    expect(ops[0]).toMatchObject({ trackId: TRACKS.a1, key: 'gainDb', value: { value: 3, keys: null } });
    expect(ops[1]).toMatchObject({ trackId: TRACKS.a2, value: { value: 12, keys: [{ frame: 0, value: 12 }] } });
  });
});

describe('audio jobs', () => {
  it('centres the audition window on the playhead and clamps it to the film', () => {
    expect(auditionRange(150, 1000, 30)).toEqual({ inFrame: 0, outFrame: 300 });
    expect(auditionRange(600, 1000, 30)).toEqual({ inFrame: 450, outFrame: 750 });
    expect(auditionRange(990, 1000, 30)).toEqual({ inFrame: 700, outFrame: 1000 });
    expect(auditionRange(10, 100, 30)).toEqual({ inFrame: 0, outFrame: 100 });
    expect(auditionRange(0, 0, 30)).toBeNull();
  });
  it('polls until completed and throws the failure sentence', async () => {
    const seq: AudioJob[] = [
      { executionId: 'e', kind: 'loudness', status: 'Queued', revision: 1 },
      { executionId: 'e', kind: 'loudness', status: 'Running', revision: 1 },
      { executionId: 'e', kind: 'loudness', status: 'Passed', revision: 1, loudness: { integratedLufs: -14, truePeakDbtp: -1, loudnessRange: 5, normalised: false } },
    ];
    let i = 0;
    const job = await waitForAudioJob('p', 't', 'e', { intervalMs: 1, get: async () => seq[i++] });
    expect(isJobDone(job)).toBe(true);
    expect(job.loudness?.integratedLufs).toBe(-14);
    await expect(waitForAudioJob('p', 't', 'e', { intervalMs: 1, get: async () => ({ executionId: 'e', kind: 'audition', status: 'Failed', revision: 1, errorMessage: 'No audio in range.' }) })).rejects.toThrow('No audio in range.');
    expect(isJobFailed({ executionId: 'e', kind: 'audition', status: 'Cancelled', revision: 1 })).toBe(true);
  });
});

describe('R08.5 duck state', () => {
  const d = () =>
    doc([
      track(TRACKS.a1, 'audio', [media('v1', 10, 40, 0), media('v2', 200, 230, 0)], { name: 'Voice' }),
      track(TRACKS.a2, 'audio', [media('m', 0, 300, 0)], { name: 'Music', gainDb: { value: -6, keys: null } }),
    ]);
  it('guesses music and voice from names', () => {
    expect(guessDuckTracks(d().tracks)).toEqual({ music: TRACKS.a2, voice: [TRACKS.a1] });
    expect(guessDuckTracks([track('x', 'audio', [], { name: 'Track 1' })]).music).toBeNull();
  });
  it('writes derived keys, goes stale when a voice clip moves, and re-derives', () => {
    const cfg = defaultDuckConfig(TRACKS.a2, [TRACKS.a1]);
    const op = compileDuck(d(), cfg);
    let docA = applyOp(d(), op).doc;
    expect(duckIsStale(docA, cfg)).toBe(false);
    const music = () => docA.tracks[1].gainDb!;
    expect(music().keys!.length).toBeGreaterThan(0);
    const before = JSON.stringify(music().keys);
    docA = { ...docA, tracks: docA.tracks.map((t) => (t.id === TRACKS.a1 ? { ...t, items: t.items.map((i) => (i.id === 'v1' ? { ...i, recordIn: 100, recordOut: 130 } : i)) } : t)) };
    expect(duckIsStale(docA, cfg)).toBe(true);
    docA = applyOp(docA, compileDuck(docA, cfg)).doc;
    expect(JSON.stringify(music().keys)).not.toBe(before);
    expect(duckIsStale(docA, cfg)).toBe(false);
    docA = applyOp(docA, compileRemoveDuck(docA, cfg)).doc;
    expect(music()).toMatchObject({ value: -6, keys: null });
  });
  it('a saved config is live only while its tracks exist; storage round-trips', () => {
    const store = new Map<string, string>();
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v), removeItem: (k: string) => void store.delete(k) };
    const cfg = defaultDuckConfig(TRACKS.a2, [TRACKS.a1]);
    saveDuckConfig('t1', cfg, storage);
    expect(loadDuckConfig('t1', storage)).toEqual(cfg);
    expect(resolveDuck(d(), cfg)).not.toBeNull();
    expect(resolveDuck(d(), { ...cfg, musicTrackId: 'gone' })).toBeNull();
    expect(resolveDuck(d(), { ...cfg, dialogueTrackIds: ['gone'] })).toBeNull();
    saveDuckConfig('t1', null, storage);
    expect(loadDuckConfig('t1', storage)).toBeNull();
  });
});

describe('R08.6 clean voice', () => {
  it('sets denoise and leveler together and clears them together', () => {
    const t = track(TRACKS.a1, 'audio', []);
    const on = compileCleanVoice(t, true);
    expect(on.map((o) => ('key' in o ? o.key : ''))).toEqual(['denoise', 'leveler']);
    let d = doc([t]);
    for (const op of on) d = applyOp(d, op).doc;
    expect(d.tracks[0].denoise).toEqual(CLEAN_VOICE.denoise);
    expect(isCleanVoice(d.tracks[0])).toBe(true);
    for (const op of compileCleanVoice(d.tracks[0], false)) d = applyOp(d, op).doc;
    expect(isCleanVoice(d.tracks[0])).toBe(false);
  });
});

describe('R08.7 recorder helpers', () => {
  it('always places onto a new Voiceover track when asked, at the requested index', () => {
    const d = doc([track(TRACKS.a1, 'audio', [])]);
    const free = compilePlaceVoiceover(d, '00000000-0000-4000-8000-000000000001', 0, 30, { itemId: 'i', trackId: 't' });
    expect(free[0]).toMatchObject({ type: 'placeOnTop' });
    const fresh = compilePlaceVoiceover(d, '00000000-0000-4000-8000-000000000001', 0, 30, { itemId: 'i', trackId: 't' }, 'Voiceover', true, 0);
    expect(fresh[0]).toMatchObject({ type: 'addTrack', index: 0, track: { name: 'Voiceover', kind: 'audio' } });
  });
  it('turns microphone errors into sentences with a next step', () => {
    expect(micErrorSentence({ name: 'NotAllowedError' })).toMatch(/blocked.*upload a recording/);
    expect(micErrorSentence({ name: 'NotFoundError' })).toMatch(/No microphone/);
    expect(micErrorSentence({ name: 'NotReadableError' })).toMatch(/Another app/);
    expect(micErrorSentence(new Error('boom'))).toBe('The microphone could not be opened.');
    expect(meterFill(0)).toBe(0);
    expect(meterFill(1)).toBe(100);
    expect(meterFill(0.25)).toBe(50);
  });
});

describe('R08.2 multicam audio source', () => {
  it('stores the chosen angle (a separate recorder clip) or none on the new multicam clip', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('cam1', 0, 100), media('cam2', 0, 100)]), track(TRACKS.a1, 'audio', [media('rec', 0, 100)])]);
    const create = (from: 'program' | 'none' | string) => {
      const ops = compileCreateMulticam(d, ['cam1', 'cam2', 'rec'], { multicamId: 'mc', angleIds: ['a1', 'a2', 'a3'] }, {}, 'Multicam', from);
      const placed = ops.find((o) => o.type === 'placeOnTop');
      return placed && 'item' in placed ? (placed.item as { audioSource?: string }).audioSource : 'x';
    };
    expect(create('program')).toBeUndefined();
    expect(create('none')).toBe('none');
    expect(create('rec')).toBe('a3');
  });
});
