// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { AppViewToggle, useWorkflowView } from './AppViewToggle';

beforeAll(() => {
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({
      matches: false, media: query, onchange: null,
      addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false,
    }),
  });
});

function Harness({ user }: { user: string }) {
  const [view, setView] = useWorkflowView(user, window.location.search);
  return (
    <>
      <AppViewToggle view={view} onChange={setView} />
      <output data-testid="view">{view}</output>
    </>
  );
}

function mount(user = 'u1') {
  return render(<MantineProvider env="test"><Harness user={user} /></MantineProvider>);
}

beforeEach(() => {
  window.localStorage.clear();
  window.history.replaceState(null, '', '/projects/p1/workflows/w1');
});
afterEach(cleanup);

describe('App | Advanced control', () => {
  it('is a radiogroup with the two views', () => {
    mount();
    const group = screen.getByRole('radiogroup', { name: 'Workflow view' });
    expect(group).toBeTruthy();
    expect(screen.getAllByRole('radio').map((r) => (r as HTMLInputElement).value)).toEqual(['app', 'advanced']);
  });

  it('opens on the URL view and writes changes back to it without a new history entry', () => {
    window.history.replaceState(null, '', '/projects/p1/workflows/w1?view=advanced');
    const before = window.history.length;
    mount();
    expect(screen.getByTestId('view').textContent).toBe('advanced');

    fireEvent.click(screen.getByRole('radio', { name: 'App' }));
    expect(screen.getByTestId('view').textContent).toBe('app');
    expect(window.location.search).toBe('?view=app');

    fireEvent.click(screen.getByRole('radio', { name: 'Advanced' }));
    expect(window.location.search).toBe('?view=advanced');
    expect(window.history.length).toBe(before);
  });

  it('remembers the choice per user and lets the URL win', () => {
    const first = mount('u1');
    fireEvent.click(screen.getByRole('radio', { name: 'Advanced' }));
    first.unmount();
    window.history.replaceState(null, '', '/projects/p1/workflows/w1');

    mount('u1');
    expect(screen.getByTestId('view').textContent).toBe('advanced');
    cleanup();

    mount('u2');
    expect(screen.getByTestId('view').textContent).toBe('app');
    cleanup();

    window.history.replaceState(null, '', '/projects/p1/workflows/w1?view=app');
    mount('u1');
    expect(screen.getByTestId('view').textContent).toBe('app');
  });
});
