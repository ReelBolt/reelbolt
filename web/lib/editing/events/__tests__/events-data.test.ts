import { describe, expect, it } from 'vitest';
import { assetRef, doc, media, track } from '../../__tests__/fixtures';
import { laneEvents, laneFileIds, nearestEvent, needsEvents, readEventsResult, type SourceEvents } from '../events-data';
import { parseEventsLanePref } from '../lane-pref';

const FILE = assetRef(1).projectFileId;
const src = (events: SourceEvents['events']): SourceEvents => ({ fps: 30, frameCount: 900, events, truncated: false });
const ev = (id: string, type: string, timeSec: number, strength = 0.5) => ({ id, type, frame: Math.round(timeSec * 30), timeSec, strength, region: null, confidence: 0.9 });

describe('needsEvents', () => {
  it('asks again for a file with no events and for one whose events are from an earlier detector', () => {
    const stored = src([ev('e0', 'hit', 1)]);
    expect(needsEvents({ state: 'none', events: null })).toBe(true);
    expect(needsEvents({ state: 'failed', events: null })).toBe(true);
    expect(needsEvents({ state: 'ready', events: null })).toBe(true);
    expect(needsEvents({ state: 'ready', events: stored })).toBe(false);
    // Still shown on the lane (the events are there), and offered again so the fresh result can replace them.
    expect(needsEvents({ state: 'ready', events: stored, outdated: true })).toBe(true);
    expect(needsEvents({ state: 'queued', events: null })).toBe(false);
    expect(needsEvents({ state: 'running', events: null, outdated: true })).toBe(false);
  });
});

describe('readEventsResult', () => {
  it('reads the stored shape and sorts by time', () => {
    const r = readEventsResult({ fps: 30, frameCount: 900, events: [{ id: 'e1', type: 'hit', frame: 60, timeSec: 2, strength: 0.7, region: { x: 0.2, y: 0.3, w: 0.1, h: 0.1 }, confidence: 0.8 }, { id: 'e0', type: 'appear', frame: 30, timeSec: 1, strength: 2, region: null, confidence: 0.5 }], suppressed: 0, truncated: false });
    expect(r?.events.map((e) => e.id)).toEqual(['e0', 'e1']);
    expect(r?.events[0].strength).toBe(1);
    expect(r?.events[1].region).toEqual({ x: 0.2, y: 0.3, w: 0.1, h: 0.1 });
  });
  it('drops unreadable entries and refuses non-objects', () => {
    expect(readEventsResult(null)).toBeNull();
    expect(readEventsResult({ events: 'x' })).toBeNull();
    const r = readEventsResult({ fps: 25, events: [{ id: 'e0', type: 'hit', frame: 50 }, { id: 5 }, { id: 'e2', type: 'hit', frame: -1 }] });
    expect(r?.events).toHaveLength(1);
    expect(r?.events[0].timeSec).toBe(2);
  });
  it('reads the optional fields when they are there, and leaves them out when they are not or are not words', () => {
    const r = readEventsResult({ fps: 30, events: [
      { id: 'e0', type: 'appear', frame: 30, endFrame: 45, direction: 'up-left', speed: 'fast', kind: 'graphic' },
      { id: 'e1', type: 'flash', frame: 60, endFrame: 10, direction: 'LEFT<', speed: 7, kind: 'cartoon' },
    ] });
    expect(r?.events[0]).toMatchObject({ endFrame: 45, direction: 'up-left', speed: 'fast', kind: 'graphic' });
    expect(r?.events[1]).toEqual({ id: 'e1', type: 'flash', frame: 60, timeSec: 2, strength: 0, region: null, confidence: 0 });
  });
});

describe('laneEvents', () => {
  const d = doc([track('v1', 'video', [media('c1', 100, 400, 60)]), track('a1', 'audio', [media('c1a', 100, 400, 60)])]);
  it('maps source seconds through the clip source-in onto record frames, inside the clip only', () => {
    const events = laneEvents(d, new Map([[FILE, src([ev('e0', 'hit', 1), ev('e1', 'hit', 3), ev('e2', 'beat', 20)])]]));
    // source second 3 = frame 90; the clip starts at source frame 60 on record frame 100 -> 130.
    expect(events.map((e) => [e.eventId, e.frame])).toEqual([['e1', 130]]);
    expect(events[0]).toMatchObject({ clipId: 'c1', trackId: 'v1', fileId: FILE, sourceSec: 3 });
    expect('kind' in events[0]).toBe(false);
  });
  it('carries the kind of picture onto the lane when the analysis gave one', () => {
    const events = laneEvents(d, new Map([[FILE, src([{ ...ev('e0', 'appear', 3), kind: 'footage' }])]]));
    expect(events[0].kind).toBe('footage');
  });
  it('reads the video track only, and honours the clip rate', () => {
    const fast = doc([track('v1', 'video', [media('c1', 0, 100, 0, { playbackRate: 2 })])]);
    expect(laneEvents(fast, new Map([[FILE, src([ev('e0', 'hit', 2)])]])).map((e) => e.frame)).toEqual([30]);
    expect(laneFileIds(d)).toEqual([FILE]);
    expect(laneFileIds(doc([track('a1', 'audio', [media('x', 0, 10)])]))).toEqual([]);
  });
  it('finds the event nearest the playhead, else the next one', () => {
    const events = laneEvents(fast(), new Map([[FILE, src([ev('e0', 'hit', 1), ev('e1', 'hit', 2)])]]));
    expect(nearestEvent(events, 31)?.eventId).toBe('e0');
    expect(nearestEvent(events, 45)?.eventId).toBe('e1');
    expect(nearestEvent(events, 400)).toBeNull();
  });
});

const fast = () => doc([track('v1', 'video', [media('c1', 0, 300, 0)])]);

describe('lane preference', () => {
  it('starts hidden with snapping on, and falls back per key', () => {
    expect(parseEventsLanePref(null)).toEqual({ lane: false, snap: true });
    expect(parseEventsLanePref('{"lane":true}')).toEqual({ lane: true, snap: true });
    expect(parseEventsLanePref('{"snap":false,"lane":"x"}')).toEqual({ lane: false, snap: false });
    expect(parseEventsLanePref('not json')).toEqual({ lane: false, snap: true });
  });
});
