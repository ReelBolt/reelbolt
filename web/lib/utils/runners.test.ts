import { describe, it, expect } from 'vitest';
import {
  RUNNER_ACTIVE_WINDOW_MS,
  canRenderRemotion,
  capabilityChips,
  localModelChip,
  formatRelative,
  parseRunnerCapabilities,
  runnerPlatformLabel,
  runnerPresence,
  shortId,
} from './runners';

// The blob reaches this code straight from a device we have not verified (the API relays it after
// checking only that it is JSON), so every case here is about not trusting it.
describe('parseRunnerCapabilities', () => {
  it('narrows a real hello payload', () => {
    const caps = parseRunnerCapabilities({
      protocolVersion: '1.0',
      agentVersion: '0.4.1',
      slots: 2,
      container: 'docker',
      containerMode: 'rootless',
      runtimeImageDigest: 'sha256:abc',
      ffmpegVersion: '7.1',
      fontsetVersion: '2026-01',
      jobTypes: ['remotion.workspace-session', 'video.compile'],
      maxOutputBytes: 1073741824,
    });
    expect(caps).not.toBeNull();
    expect(caps!.container).toBe('docker');
    expect(caps!.slots).toBe(2);
    expect(canRenderRemotion(caps)).toBe(true);
  });

  it('returns null for a blob that is not an object at all', () => {
    expect(parseRunnerCapabilities(undefined)).toBeNull();
    expect(parseRunnerCapabilities(null)).toBeNull();
    expect(parseRunnerCapabilities('docker')).toBeNull();
    expect(parseRunnerCapabilities([1, 2])).toBeNull();
  });

  it('drops fields of the wrong type instead of coercing them', () => {
    const caps = parseRunnerCapabilities({ slots: 'lots', container: 7, jobTypes: ['video.compile', 12, null] });
    expect(caps).not.toBeNull();
    expect(caps!.slots).toBeNull();
    expect(caps!.container).toBeNull();
    expect(caps!.jobTypes).toEqual(['video.compile']);
  });
});

describe('canRenderRemotion', () => {
  it('is the advertised job list, not the container, that decides', () => {
    expect(canRenderRemotion(parseRunnerCapabilities({ jobTypes: ['video.compile'] }))).toBe(false);
    expect(canRenderRemotion(parseRunnerCapabilities({ jobTypes: ['remotion.workspace-session'] }))).toBe(true);
    expect(canRenderRemotion(null)).toBe(false);
  });
});

describe('capabilityChips', () => {
  it('says compile only for a runner with no container runtime', () => {
    const chips = capabilityChips(
      parseRunnerCapabilities({ container: 'none', jobTypes: ['video.compile', 'video.analyze.extract'] }),
    );
    const labels = chips.map((c) => c.label);
    expect(labels).toContain('Compile only');
    expect(labels).toContain('No container');
    expect(labels).not.toContain('Video renders');
  });

  it('renders nothing when the device has never reported anything', () => {
    expect(capabilityChips(parseRunnerCapabilities(undefined))).toEqual([]);
  });

  it('never claims more than the device said: an unknown runtime keeps the device own word', () => {
    const chips = capabilityChips(parseRunnerCapabilities({ container: 'containerd' }));
    expect(chips.map((c) => c.label)).toContain('containerd');
  });
});

// The dot is an inference from a heartbeat, never a live check — the gateway's authoritative
// 'is a runner online?' answer is per-replica and unreachable from the dashboard.
describe('runnerPresence', () => {
  const now = Date.parse('2026-10-08T12:00:00Z');

  it('is never for a device that has not connected', () => {
    expect(runnerPresence(undefined, now)).toBe('never');
    expect(runnerPresence(null, now)).toBe('never');
    expect(runnerPresence('not a date', now)).toBe('never');
  });

  it('is online inside the active window and offline past it', () => {
    expect(runnerPresence(new Date(now - 60_000).toISOString(), now)).toBe('online');
    expect(runnerPresence(new Date(now - RUNNER_ACTIVE_WINDOW_MS).toISOString(), now)).toBe('online');
    expect(runnerPresence(new Date(now - RUNNER_ACTIVE_WINDOW_MS - 1).toISOString(), now)).toBe('offline');
    expect(runnerPresence(new Date(now - 86_400_000).toISOString(), now)).toBe('offline');
  });
});

describe('formatRelative', () => {
  const now = Date.parse('2026-10-08T12:00:00Z');

  it('reads both directions, because the approval screen counts down as well as back', () => {
    expect(formatRelative(new Date(now - 10_000).toISOString(), now)).toBe('less than a minute ago');
    expect(formatRelative(new Date(now - 5 * 60_000).toISOString(), now)).toBe('5 minutes ago');
    expect(formatRelative(new Date(now + 9 * 60_000).toISOString(), now)).toBe('in 9 minutes');
    expect(formatRelative(new Date(now - 3 * 3600_000).toISOString(), now)).toBe('3 hours ago');
  });

  it('says never and unknown rather than NaN', () => {
    expect(formatRelative(undefined, now)).toBe('never');
    expect(formatRelative('nonsense', now)).toBe('unknown');
  });
});

describe('runnerPlatformLabel', () => {
  it('names the platforms decision D26 allows', () => {
    expect(runnerPlatformLabel('windows', 'amd64')).toBe('Windows (x64)');
    expect(runnerPlatformLabel('linux', 'amd64')).toBe('Linux (x64)');
    expect(runnerPlatformLabel('linux', 'arm64')).toBe('Linux (arm64)');
  });

  it('shows a platform it does not support rather than hiding it', () => {
    expect(runnerPlatformLabel('darwin', 'arm64')).toBe('macOS (arm64)');
    expect(runnerPlatformLabel(undefined, undefined)).toBe('Not reported');
  });
});

describe('shortId', () => {
  it('shortens a uuid and leaves anything short alone', () => {
    expect(shortId('123e4567-e89b-12d3-a456-426614174000')).toBe('123e4567');
    expect(shortId('abc')).toBe('abc');
  });
});

describe('localModelChip', () => {
  it('shows nothing unless the device says sharing is on', () => {
    expect(localModelChip(parseRunnerCapabilities({ localLlm: { enabled: false, models: ['a'] } }))).toBeNull();
    expect(localModelChip(parseRunnerCapabilities({}))).toBeNull();
    expect(localModelChip(parseRunnerCapabilities({ localLlm: 'yes' }))).toBeNull();
  });

  it('counts the shared models', () => {
    const one = localModelChip(parseRunnerCapabilities({ localLlm: { enabled: true, models: ['a'] } }));
    expect(one?.label).toBe('Shares 1 model');
    const many = localModelChip(
      parseRunnerCapabilities({ localLlm: { enabled: true, models: ['a', 'b', 3] } }),
    );
    expect(many?.label).toBe('Shares 2 models');
    const none = localModelChip(parseRunnerCapabilities({ localLlm: { enabled: true } }));
    expect(none?.label).toBe('Shares a model');
  });

  it('is part of the chips a device earns', () => {
    const chips = capabilityChips(
      parseRunnerCapabilities({ localLlm: { enabled: true, models: ['a'] } }),
    );
    expect(chips.map((c) => c.label)).toContain('Shares 1 model');
  });
});
