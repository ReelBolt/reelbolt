/** @vitest-environment node */
/** R08.6: the parity classifier moves bus and dynamics stages to "approximate" only when the preview runs them. */
import { describe, expect, it } from 'vitest';
import { classifyParity } from '../parity';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import type { AudioBus, EditTimeline, Track } from '../../types';
import type { DspSupport } from '../../audio/dsp-preview';

const FULL: DspSupport = { nodes: true, worklet: true };
const NODES: DspSupport = { nodes: true, worklet: false };
const NONE: DspSupport = { nodes: false, worklet: false };
const aud = (extra: Partial<Track> = {}): Track => ({ ...track(TRACKS.a1, 'audio', [media('c', 0, 30)]), ...extra });
const parity = (d: EditTimeline, s: DspSupport) => classifyParity(d, { dsp: s }).items.find((i) => i.itemId === 'c')!;
const withBuses = (t: Track, buses: AudioBus[]): EditTimeline => ({ ...doc([t]), buses });

describe('audio processing parity', () => {
  it('a plain sound clip stays faithful', () => {
    expect(parity(doc([aud()]), FULL).parity).toBe('faithful');
  });
  it('every dynamics stage is approximate when the preview graph runs it', () => {
    const d = doc([aud({ dynamics: { gate: {}, deesser: {}, compressor: {}, limiter: {} } })]);
    const p = parity(d, FULL);
    expect(p.parity).toBe('approximate');
    expect(p.reason).toContain('render is exact');
  });
  it('compressor and limiter need only the nodes', () => {
    expect(parity(doc([aud({ dynamics: { compressor: {}, limiter: {} } })]), NODES).parity).toBe('approximate');
  });
  it('gate and de-esser stay render-required without AudioWorklet, naming the stage', () => {
    const p = parity(doc([aud({ dynamics: { gate: {}, compressor: {} } })]), NODES);
    expect(p.parity).toBe('renderRequired');
    expect(p.reason).toContain('Noise gate');
    expect(p.reason).not.toContain('Compressor');
    expect(parity(doc([aud({ dynamics: { deesser: {} } })]), NODES).reason).toContain('De-esser');
  });
  it('nothing is heard without the nodes', () => {
    expect(parity(doc([aud({ dynamics: { limiter: {} } })]), NONE).parity).toBe('renderRequired');
  });
  it('bus stages count through the whole chain', () => {
    const t = aud({ busId: 'child' });
    const d = withBuses(t, [{ id: 'child', name: 'c', outputBusId: 'top' }, { id: 'top', name: 't', dynamics: { gate: {} } }]);
    expect(parity(d, NODES).parity).toBe('renderRequired');
    expect(parity(d, FULL).parity).toBe('approximate');
  });
  it('a bus with gain is approximate even without stages; an empty bus changes nothing', () => {
    expect(parity(withBuses(aud({ busId: 'b' }), [{ id: 'b', name: 'b', gainDb: { value: -3 } }]), NONE).parity).toBe('approximate');
    expect(parity(withBuses(aud({ busId: 'b' }), [{ id: 'b', name: 'b' }]), NONE).parity).toBe('faithful');
  });
  it('a bus loop cannot be previewed and stays render-required', () => {
    const d = withBuses(aud({ busId: 'a' }), [{ id: 'a', name: 'a', outputBusId: 'b' }, { id: 'b', name: 'b', outputBusId: 'a' }]);
    expect(parity(d, FULL).parity).toBe('renderRequired');
  });
  it('denoise and the leveler are still render-only', () => {
    expect(parity(doc([aud({ denoise: { kind: 'fft', strength: 0.5 } })]), FULL).parity).toBe('renderRequired');
    expect(parity(doc([aud({ leveler: { kind: 'speech', targetDb: -18 } })]), FULL).parity).toBe('renderRequired');
  });
  it('a picture track is not judged by audio processing', () => {
    const v = track(TRACKS.v1, 'video', [media('c', 0, 30)]);
    expect(parity(doc([{ ...v, dynamics: { compressor: {} } }]), FULL).parity).toBe('faithful');
  });
});
