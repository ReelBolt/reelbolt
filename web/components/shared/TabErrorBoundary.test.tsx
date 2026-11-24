// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TabErrorBoundary } from './TabErrorBoundary';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
});
afterEach(() => cleanup());

const state = { broken: true };
function Child() {
  if (state.broken) throw new Error('boom');
  return <div>child ok</div>;
}

function renderIt() {
  render(
    <MantineProvider>
      <p>rest of the page</p>
      <TabErrorBoundary><Child /></TabErrorBoundary>
    </MantineProvider>,
  );
}

describe('TabErrorBoundary', () => {
  it('shows the fallback when a child throws and leaves the rest of the page up', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    state.broken = true;
    renderIt();
    expect(screen.getByText('Something went wrong showing this tab.')).toBeTruthy();
    expect(screen.getByText('rest of the page')).toBeTruthy();
    expect(screen.queryByText('child ok')).toBeNull();
  });

  it('re-mounts the children when Reload is pressed', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    state.broken = true;
    renderIt();
    state.broken = false;
    fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
    expect(screen.getByText('child ok')).toBeTruthy();
  });

  it('renders children untouched when nothing throws', () => {
    state.broken = false;
    renderIt();
    expect(screen.getByText('child ok')).toBeTruthy();
  });
});
