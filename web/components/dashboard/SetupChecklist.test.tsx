// @vitest-environment jsdom

/** A09: the setup checklist is a full card only for a platform admin; a hosted user gets one quiet line. */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SetupChecklist } from './sections';

vi.mock('next/link', () => ({
  default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a>,
}));
vi.mock('@/components/assistant/AssistantShellProvider', () => ({
  useAssistantShell: () => ({ openWithPrompt: vi.fn() }),
}));

beforeAll(() => {
  window.matchMedia ??= ((q: string) => ({
    matches: false, media: q, onchange: null, addEventListener() {}, removeEventListener() {},
    addListener() {}, removeListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  window.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(cleanup);

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('SetupChecklist', () => {
  it('shows the full card for a platform admin with core capabilities missing', () => {
    wrap(<SetupChecklist availableCapabilities={[]} />);
    expect(screen.getByText(/Finish setup/)).toBeTruthy();
  });

  it('collapses to one optional line when compact, even with core capabilities missing', () => {
    wrap(<SetupChecklist availableCapabilities={[]} compact />);
    expect(screen.queryByText(/Finish setup/)).toBeNull();
    expect(screen.getByText('Optional providers not set up')).toBeTruthy();
  });

  it('says core works without them when only optional providers are missing', () => {
    wrap(<SetupChecklist availableCapabilities={['Chat', 'Transcription', 'Vision']} />);
    expect(screen.getByText(/Everything core works without them/)).toBeTruthy();
  });

  it('does not say it when a core capability is missing (compact)', () => {
    wrap(<SetupChecklist availableCapabilities={['Chat']} compact />);
    expect(screen.getByText('Optional providers not set up')).toBeTruthy();
    expect(screen.queryByText(/Everything core works/)).toBeNull();
  });
});
