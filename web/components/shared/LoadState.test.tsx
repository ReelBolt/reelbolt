// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { LoadState } from './LoadState';
import { ErrorScreen } from './ErrorScreen';
import { OfflineBanner } from '../shell/OfflineBanner';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); });

const wrap = (ui: React.ReactNode) => render(<MantineProvider>{ui}</MantineProvider>);
const retry = vi.fn();
function failed(error: unknown, extra: { backHref?: string; resource?: string } = {}) {
  wrap(<LoadState loading={false} error={error} onRetry={retry} backHref="/projects" {...extra}>ok</LoadState>);
  return screen.getByRole('alert');
}

describe('LoadState', () => {
  it('shows a skeleton, then the slow line with Retry after 10 s', () => {
    vi.useFakeTimers();
    wrap(<LoadState loading onRetry={retry}>ok</LoadState>);
    expect(screen.getByTestId('load-skeleton')).toBeTruthy();
    expect(screen.queryByRole('status')).toBeNull();
    act(() => { vi.advanceTimersByTime(10000); });
    expect(screen.getByRole('status').textContent).toMatch(/taking longer than usual/i);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(retry).toHaveBeenCalled();
  });

  it('renders children on success', () => {
    wrap(<LoadState loading={false}>all good</LoadState>);
    expect(screen.getByText('all good')).toBeTruthy();
  });

  it('500: our side, retry, back, feedback, no raw message', () => {
    const a = failed(new ApiError('Internal Server Error', 500));
    expect(a.textContent).toMatch(/our side/i);
    expect(a.textContent).not.toMatch(/Internal Server Error/);
    expect(a.textContent).not.toMatch(/not found/i);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.getByRole('link').getAttribute('href')).toBe('/projects');
    expect(screen.getByRole('button', { name: 'Flag a bug' })).toBeTruthy();
  });

  it('502 with an HTML body: the server sentence, not markup', () => {
    const a = failed(new ApiError('<html><body>Bad Gateway</body></html>', 502));
    expect(a.textContent).toMatch(/our side/i);
    expect(a.textContent).not.toMatch(/<html/);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });

  it('network failure: could not reach, retry, back, not "gone"', () => {
    const a = failed(new TypeError('Failed to fetch'));
    expect(a.textContent).toMatch(/Could not reach ReelBolt/);
    expect(a.textContent).not.toMatch(/not found|deleted/i);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.getByRole('link')).toBeTruthy();
  });

  it('401: session ended with a sign-in link', () => {
    const a = failed(new ApiError('Unauthorized', 401));
    expect(a.textContent).toMatch(/session ended/i);
    expect(screen.getByRole('link', { name: 'Sign in' }).getAttribute('href')).toBe('/login');
  });

  it('403: no permission, no retry, a way back', () => {
    const a = failed(new ApiError('', 403));
    expect(a.textContent).toMatch(/permission/i);
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(screen.getByRole('link').getAttribute('href')).toBe('/projects');
  });

  it('404: says not found, no retry, a way back', () => {
    const a = failed(new ApiError('', 404), { resource: 'Project' });
    expect(a.textContent).toMatch(/not found/i);
    expect(a.textContent).toMatch(/Project/);
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
    expect(screen.getByRole('link').getAttribute('href')).toBe('/projects');
  });

  it('403 and 404 with no back link given still lead to the dashboard', () => {
    for (const status of [403, 404]) {
      wrap(<LoadState loading={false} error={new ApiError('', status)} onRetry={retry}>ok</LoadState>);
      expect(screen.getByRole('link').getAttribute('href')).toBe('/dashboard');
      cleanup();
    }
  });

  it('402: links to billing and does not claim the thing is gone', () => {
    const a = failed(new ApiError('Payment Required', 402));
    expect(a.textContent).not.toMatch(/not found|deleted/i);
    expect(screen.getByRole('link', { name: /plan and billing/i }).getAttribute('href')).toBe('/settings/billing');
  });

  it('429: wait and retry', () => {
    const a = failed(new ApiError('', 429));
    expect(a.textContent).toMatch(/too many requests/i);
    expect(a.textContent).not.toMatch(/not found/i);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });
});

describe('ErrorScreen', () => {
  it('explains, offers Try again (reset) and a way back, and logs', () => {
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    const reset = vi.fn();
    wrap(<ErrorScreen error={new Error('boom secret')} reset={reset} />);
    const a = screen.getByRole('alert');
    expect(a.textContent).toMatch(/Something went wrong showing this page/);
    expect(a.textContent).not.toMatch(/boom secret/);
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(reset).toHaveBeenCalled();
    expect(screen.getByRole('link', { name: 'Back to the dashboard' }).getAttribute('href')).toBe('/dashboard');
    expect(screen.getByRole('button', { name: 'Flag a bug' })).toBeTruthy();
    expect(log).toHaveBeenCalled();
  });
});

describe('OfflineBanner', () => {
  const setOnline = (v: boolean) => Object.defineProperty(navigator, 'onLine', { configurable: true, value: v });
  it('is hidden online and shows a status line when offline', () => {
    setOnline(true);
    wrap(<OfflineBanner />);
    expect(screen.queryByRole('status')).toBeNull();
    act(() => { setOnline(false); window.dispatchEvent(new Event('offline')); });
    expect(screen.getByRole('status').textContent).toBe('You are offline. What you see may be out of date.');
    act(() => { setOnline(true); window.dispatchEvent(new Event('online')); });
    expect(screen.queryByRole('status')).toBeNull();
  });
});
