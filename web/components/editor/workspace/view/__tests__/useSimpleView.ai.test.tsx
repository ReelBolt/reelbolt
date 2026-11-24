// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/i18n', async (orig) => ({ ...(await orig<Record<string, unknown>>()), useT: () => Object.assign((key: string) => key, { msg: (m: unknown) => String(m) }) }));
vi.mock('../../../background/PendingResults', () => ({ PendingResults: () => null }));
vi.mock('../../../panels/TimelineSettingsPanel', () => ({ TimelineSettingsPanel: () => null, timelineHasClips: () => false }));
vi.mock('../../../phone/AudioSheet', () => ({ AudioSheet: () => null }));
vi.mock('../../../phone/LookSheet', () => ({ LookSheetBody: () => <div>look body</div>, TitlesSection: () => null }));
vi.mock('../../../simple/AskBox', () => ({ AskBox: () => null }));
vi.mock('../../../simple/ShellSwitch', () => ({ ShellSwitch: () => null }));
vi.mock('../../../simple/SimpleSelection', () => ({ SimpleSelection: () => null }));
vi.mock('../../../simple/SimpleTasks', () => ({ SimpleTasks: () => <div>tasks body</div> }));
vi.mock('../../../prefs/live-chords', () => ({ LiveChordsProvider: ({ children }: { children: React.ReactNode }) => <>{children}</> }));
vi.mock('../../live-panels', () => ({ LiveSubtitlesPanel: () => null, LiveVoRecorderPanel: () => null }));
vi.mock('../../../panels/ProposalPanel', () => ({
  ProposalPanel: function ProposalPanel() {
    const [n, setN] = React.useState(0);
    return (
      <button type="button" onClick={() => setN(n + 1)}>
        {`proposal ${n}`}
      </button>
    );
  },
}));

import { useSimpleView } from '../useSimpleView';

global.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;
global.matchMedia ??= ((query: string) =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => false }) as MediaQueryList) as typeof matchMedia;

let setPlaceOuter: (p: 'edit' | 'look' | 'export') => void = () => undefined;

function Driver() {
  const [simplePlace, setSimplePlace] = React.useState<'edit' | 'look' | 'export'>('edit');
  const [simpleView, setSimpleView] = React.useState<string>('ask');
  React.useEffect(() => {
    setPlaceOuter = setSimplePlace;
  }, []);
  const ctx = {
    store: { getState: () => ({ doc: { tracks: [] }, ui: { playheadFrame: 0 } }) },
    sync: {},
    simple: true,
    shell: 'simple',
    setShell: () => undefined,
    simplePlace,
    setSimplePlace,
    simpleView,
    setSimpleView,
    askSeed: null,
    setAskSeed: () => undefined,
    openInFull: () => undefined,
    simpleIntroOpen: false,
    dismissIntro: () => undefined,
    isTablet: false,
    doc: { name: 'T', tracks: [], settings: {} },
    save: { status: 'saved' },
    projectId: 'p',
    timelineId: 't',
    readOnly: false,
    ui: { selection: [], inOut: null },
    deliverWorkspaceElement: () => <div>export body</div>,
    attachViewerZone: () => undefined,
    fullscreenOn: false,
    runningChords: [],
    gradePreviewValue: null,
    setGhostState: () => undefined,
  } as unknown as Parameters<typeof useSimpleView>[0];
  return <>{useSimpleView(ctx).simpleShell}</>;
}

describe('useSimpleView AI panel', () => {
  afterEach(cleanup);

  it('keeps the suggestions panel mounted under other places and reopens it from the AI tools button', () => {
    render(
      <MantineProvider env="test">
        <Driver />
      </MantineProvider>,
    );
    fireEvent.click(screen.getByText('proposal 0'));
    expect(screen.getByText('proposal 1')).toBeTruthy();
    expect(screen.getByTestId('simple-ai-tools').getAttribute('aria-pressed')).toBe('true');

    React.act(() => setPlaceOuter('look'));
    expect(screen.getByTestId('simple-ai-panel').hidden).toBe(true);
    React.act(() => setPlaceOuter('export'));
    expect(screen.getByTestId('simple-ai-panel').hidden).toBe(true);
    expect(screen.getByTestId('simple-ai-tools').getAttribute('aria-pressed')).toBe('false');

    fireEvent.click(screen.getByTestId('simple-ai-tools'));
    expect(screen.getByTestId('simple-ai-panel').hidden).toBe(false);
    expect(screen.getByText('proposal 1')).toBeTruthy();
    expect(screen.getByTestId('simple-ai-tools').getAttribute('aria-pressed')).toBe('true');
  });
});
