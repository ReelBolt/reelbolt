// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SimpleWorkspace, type SimpleWorkspaceProps } from '../SimpleWorkspace';

global.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;
global.matchMedia ??= ((query: string) =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false }) as MediaQueryList) as typeof matchMedia;

function Counter() {
  const [n, setN] = React.useState(0);
  return (
    <button type="button" onClick={() => setN(n + 1)}>
      {`count ${n}`}
    </button>
  );
}

const props = (over: Partial<SimpleWorkspaceProps>): SimpleWorkspaceProps => ({
  name: 'T',
  save: { status: 'saved' } as SimpleWorkspaceProps['save'],
  backHref: '/',
  onRetry: () => undefined,
  onSignIn: () => undefined,
  layout: 'desktop',
  place: 'edit',
  onPlace: () => undefined,
  shellSwitch: null,
  pending: null,
  onShortcuts: () => undefined,
  introOpen: false,
  onDismissIntro: () => undefined,
  onOpenFull: () => undefined,
  banner: null,
  dialogs: null,
  media: null,
  picture: null,
  transport: null,
  attachViewer: () => undefined,
  fullscreenOn: false,
  viewerTheme: undefined,
  ask: null,
  timeline: null,
  panelTitle: 'Look',
  panel: <div>look body</div>,
  aiPanel: <Counter />,
  aiActive: false,
  onOpenAi: () => undefined,
  ...over,
});

const ui = (over: Partial<SimpleWorkspaceProps>) => (
  <MantineProvider env="test">
    <SimpleWorkspace {...props(over)} />
  </MantineProvider>
);

describe('Simple shell AI tools button', () => {
  afterEach(cleanup);

  it('is always rendered, pressed only while the AI panel shows, and calls onOpenAi', () => {
    const onOpenAi = vi.fn();
    const { rerender } = render(ui({ aiActive: false, onOpenAi }));
    const button = screen.getByTestId('simple-ai-tools');
    expect(button.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(button);
    expect(onOpenAi).toHaveBeenCalledTimes(1);
    rerender(ui({ aiActive: true, onOpenAi }));
    expect(screen.getByTestId('simple-ai-tools').getAttribute('aria-pressed')).toBe('true');
  });

  it('keeps the AI panel mounted but hidden while another panel shows', () => {
    render(ui({ aiActive: false }));
    const ai = screen.getByTestId('simple-ai-panel');
    expect(ai.hidden).toBe(true);
    expect(screen.getByTestId('simple-panel-body').hidden).toBe(false);
    expect(screen.getByText('look body')).toBeTruthy();
  });

  it('keeps a stateful child state across active -> hidden -> active', () => {
    const { rerender } = render(ui({ aiActive: true, panel: null }));
    fireEvent.click(screen.getByText('count 0'));
    expect(screen.getByText('count 1')).toBeTruthy();
    rerender(ui({ aiActive: false }));
    expect(screen.getByTestId('simple-ai-panel').hidden).toBe(true);
    rerender(ui({ aiActive: true, panel: null }));
    expect(screen.getByTestId('simple-ai-panel').hidden).toBe(false);
    expect(screen.getByText('count 1')).toBeTruthy();
  });
});
