// @vitest-environment jsdom
/**
 * The `alpha.first_export_initiated` call-site (alpha-launch T-018). What this protects: the funnel
 * event is emitted from the real transition — the Download action — and not on a page load.
 * `emitFunnelEvent` enforces once-per-user and the consent gate (see
 * `web/lib/telemetry/funnel.test.ts`); this test proves the component calls it at all.
 *
 * The event is "export initiated", not "export completed": a bare `<a download>` gives the page no
 * success signal, which is stated in docs/observability.md.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { userKeyFromEmail } from '@/lib/telemetry';

const mocks = vi.hoisted(() => ({ emitFunnelEvent: vi.fn() }));

vi.mock('@/lib/telemetry', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/telemetry')>()),
  emitFunnelEvent: mocks.emitFunnelEvent,
}));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'Ada@Example.com' } }) }));
vi.mock('next/link', () => ({
  default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a>,
}));

import { OutputPlayer } from './OutputPlayer';

const OUTPUT = { stepResultId: 's1', projectId: 'p1', fileName: 'x.mp4', producedAt: '2026-10-04T12:00:00Z' };

const wrap = (ui: React.ReactElement) => render(<MantineProvider><Notifications />{ui}</MantineProvider>);

beforeEach(() => {
  mocks.emitFunnelEvent.mockClear();
  global.matchMedia = ((q: string) => ({
    matches: false,
    media: q,
    addListener() {},
    removeListener() {},
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('OutputPlayer funnel call-site', () => {
  it('does not emit anything merely because the render appeared', () => {
    wrap(<OutputPlayer output={OUTPUT} />);
    expect(mocks.emitFunnelEvent).not.toHaveBeenCalled();
  });

  it('emits alpha.first_export_initiated when Download is clicked', () => {
    wrap(<OutputPlayer output={OUTPUT} />);

    // Download renders as an anchor (`component="a"` with `download`), so its role is "link".
    fireEvent.click(screen.getByRole('link', { name: /Download/ }));

    expect(mocks.emitFunnelEvent).toHaveBeenCalledWith('alpha.first_export_initiated', userKeyFromEmail('Ada@Example.com'));
  });
});
