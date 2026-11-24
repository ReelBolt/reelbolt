// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

import { RunnerTable } from './RunnerTable';
import type { RunnerDevice } from '@/lib/types/runner';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

function device(overrides: Partial<RunnerDevice> = {}): RunnerDevice {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    name: 'Studio workstation',
    os: 'linux',
    arch: 'amd64',
    runnerVersion: '0.4.1',
    createdAt: '2026-10-01T09:00:00Z',
    lastSeenAt: new Date().toISOString(),
    pairedByUserId: '22222222-2222-2222-2222-222222222222',
    capabilities: {
      protocolVersion: '1.0',
      slots: 2,
      container: 'docker',
      ffmpegVersion: '7.1',
      jobTypes: ['remotion.workspace-session', 'video.compile', 'video.analyze.extract'],
    },
    ...overrides,
  };
}

function renderTable(devices: RunnerDevice[], onRevoke = vi.fn(), pairerNames?: Record<string, string>) {
  render(
    <MantineProvider>
      <RunnerTable devices={devices} onRevoke={onRevoke} pairerNames={pairerNames} />
    </MantineProvider>,
  );
  return onRevoke;
}

describe('RunnerTable', () => {
  it('shows what the device says it can run, so a user can tell a render box from a compile-only box', () => {
    renderTable([device()]);
    expect(screen.getByText('Studio workstation')).toBeTruthy();
    expect(screen.getByText('Linux (x64)')).toBeTruthy();
    expect(screen.getByText('0.4.1')).toBeTruthy();
    expect(screen.getByText('Video renders')).toBeTruthy();
    expect(screen.getByText('Docker')).toBeTruthy();
    expect(screen.getByText('2 jobs at a time')).toBeTruthy();
    expect(screen.getByText('Video tools 7.1')).toBeTruthy();
  });

  it('labels a runner with no container runtime compile only, and never as a renderer', () => {
    renderTable([
      device({
        capabilities: { container: 'none', jobTypes: ['video.compile', 'video.analyze.extract'] },
      }),
    ]);
    expect(screen.getByText('Compile only')).toBeTruthy();
    expect(screen.queryByText('Video renders')).toBeNull();
    expect(screen.getByText('No container')).toBeTruthy();
  });

  it('says capabilities are not reported yet for a device that never connected', () => {
    renderTable([device({ lastSeenAt: undefined, capabilities: undefined })]);
    expect(screen.getByText('Not reported yet')).toBeTruthy();
    expect(screen.getByText('Never')).toBeTruthy();
  });

  it('names the user who paired the device when the workspace members could be read', () => {
    renderTable([device()], vi.fn(), { '22222222-2222-2222-2222-222222222222': 'dana@example.com' });
    expect(screen.getByText('dana@example.com')).toBeTruthy();
  });

  // The device list carries only an account id, and the members endpoint is refused to a Billing
  // caller — so the fallback has to be honest rather than blank or invented.
  it('falls back to a short account id when the members could not be read', () => {
    renderTable([device()]);
    expect(screen.getByText('22222222')).toBeTruthy();
  });

  it('offers revoke on every row, because the server also allows the user who paired it', () => {
    const onRevoke = renderTable([device()]);
    const button = screen.getByRole('button', { name: 'Revoke Studio workstation' });
    fireEvent.click(button);
    expect(onRevoke).toHaveBeenCalledTimes(1);
    expect(onRevoke.mock.calls[0][0].id).toBe('11111111-1111-1111-1111-111111111111');
  });
});
