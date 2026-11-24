import { describe, it, expect } from 'vitest';
import { validateStepConfig } from './schema-form';
import { createDefaultTimelineRenderStepConfig } from '@/components/workflows/TimelineRenderStepConfig';
import { createDefaultVideoCompileStepConfig } from '@/components/workflows/VideoCompileStepConfig';

const encode = (extra: Record<string, unknown>) => ({
  ...createDefaultTimelineRenderStepConfig(),
  encode: { width: null, height: null, codec: 'H264', crf: 18, speed: 'Medium', audioBitrateKbps: 192, ...extra },
});
const timelineValid = (extra: Record<string, unknown>) => validateStepConfig('TimelineRender', encode(extra)).valid;

describe('TimelineRender encode rate control', () => {
  it('keeps a config without rate control valid', () => {
    expect(timelineValid({})).toBe(true);
    expect(timelineValid({ rateControl: 'Quality' })).toBe(true);
  });
  it('accepts Capped and Average with an in-range bitrate', () => {
    expect(timelineValid({ rateControl: 'Capped', videoBitrateKbps: 5000 })).toBe(true);
    expect(timelineValid({ rateControl: 'Average', videoBitrateKbps: 200 })).toBe(true);
    expect(timelineValid({ rateControl: 'Average', videoBitrateKbps: 100000 })).toBe(true);
  });
  it('refuses a missing, out-of-range or unexpected bitrate and unknown names', () => {
    expect(timelineValid({ rateControl: 'Average' })).toBe(false);
    expect(timelineValid({ rateControl: 'Capped', videoBitrateKbps: 199 })).toBe(false);
    expect(timelineValid({ rateControl: 'Capped', videoBitrateKbps: 100001 })).toBe(false);
    expect(timelineValid({ rateControl: 'Quality', videoBitrateKbps: 5000 })).toBe(false);
    expect(timelineValid({ videoBitrateKbps: 5000 })).toBe(false);
    expect(timelineValid({ rateControl: 'Fast' })).toBe(false);
    expect(timelineValid({ rateControl: 1 })).toBe(false);
  });
});

describe('TimelineRender audio-only rate control', () => {
  const audio = (extra: Record<string, unknown>) =>
    validateStepConfig('TimelineRender', { ...encode(extra), preset: 'AudioOnly' }).valid;
  it('refuses both rate fields and keeps the plain audio render valid', () => {
    expect(audio({})).toBe(true);
    expect(audio({ rateControl: 'Quality' })).toBe(true);
    expect(audio({ rateControl: 'Capped', videoBitrateKbps: 5000 })).toBe(false);
    expect(audio({ rateControl: 'Average', videoBitrateKbps: 5000 })).toBe(false);
  });
});

describe('VideoCompile rate control', () => {
  const compile = (extra: Record<string, unknown>) => {
    const config = { ...(createDefaultVideoCompileStepConfig() as unknown as Record<string, unknown>), ...extra };
    return validateStepConfig('VideoCompile', config).valid;
  };
  it('has the same rules; the base config is unchanged by them', () => {
    const base = compile({});
    expect(compile({ rateControl: 'Average', videoBitrateKbps: 8000 })).toBe(base);
    expect(compile({ rateControl: 'Average' })).toBe(false);
    expect(compile({ rateControl: 'Capped', videoBitrateKbps: 100001 })).toBe(false);
    expect(compile({ rateControl: 'Quality', videoBitrateKbps: 5000 })).toBe(false);
    expect(compile({ videoBitrateKbps: 5000 })).toBe(false);
    expect(compile({ rateControl: null, videoBitrateKbps: 5000 })).toBe(false);
  });
});
