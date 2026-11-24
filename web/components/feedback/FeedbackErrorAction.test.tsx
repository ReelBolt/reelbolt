// @vitest-environment jsdom

/**
 * The error-state affordance (T-019). It must not depend on a Next router (an error boundary can
 * render outside one), so it reads the page from `window.location` at click time.
 */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/api/feedback', () => ({ submitFeedback: vi.fn() }));

import { FeedbackErrorAction } from './FeedbackErrorAction';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({
      matches: false,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => false
    })
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

const renderAction = () =>
  render(
    <MantineProvider>
      <FeedbackErrorAction />
    </MantineProvider>
  );

describe('FeedbackErrorAction', () => {
  it('renders the flag action without needing a router', () => {
    renderAction();
    expect(screen.getByRole('button', { name: 'Flag a bug' })).toBeTruthy();
  });

  it('opens the dialog and offers the current page', () => {
    renderAction();

    fireEvent.click(screen.getByRole('button', { name: 'Flag a bug' }));

    const payload = JSON.parse(screen.getByTestId('feedback-payload').textContent ?? '{}');
    expect(typeof payload.context.page).toBe('string');
  });
});
