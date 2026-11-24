// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const apps = vi.hoisted(() => ({ on: true }));
vi.mock('@/components/shell/WorkflowAppsFlag', () => ({ useWorkflowAppsEnabled: () => apps.on }));
const emit = vi.hoisted(() => vi.fn());
vi.mock('@/lib/telemetry', () => ({ emitFunnelEvent: emit, userKeyFromEmail: () => 'k' }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'a@b.c' } }) }));

import { RunNudge } from './RunNudge';

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  window.localStorage.clear();
  emit.mockReset();
  apps.on = true;
});
afterEach(cleanup);

const mount = () => render(<MantineProvider><RunNudge projectId="p1" projectName="Trip" /></MantineProvider>);

describe('RunNudge', () => {
  it('has one CTA to the project apps and fires no event', () => {
    mount();
    const link = screen.getByRole('link', { name: 'Pick an app and run it' });
    expect(link.getAttribute('href')).toBe('/projects/p1?tab=workflows');
    expect(screen.getByText(/Trip has files and no video yet/)).toBeTruthy();
    fireEvent.click(link);
    expect(emit).not.toHaveBeenCalled();
  });

  it('is dismissed for good and stays gone on the next visit', () => {
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Not now' }));
    expect(screen.queryByTestId('run-nudge')).toBeNull();
    cleanup();
    mount();
    expect(screen.queryByTestId('run-nudge')).toBeNull();
  });

  it('is hidden when apps are off', () => {
    apps.on = false;
    mount();
    expect(screen.queryByTestId('run-nudge')).toBeNull();
  });
});
