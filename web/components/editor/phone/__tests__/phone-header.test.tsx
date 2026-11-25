// @vitest-environment jsdom
import React from 'react';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PhoneHeader } from '../PhoneHeader';

beforeEach(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(cleanup);

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

const base = {
  name: 'A very long timeline name that must never push the buttons off a 360 pixel screen',
  save: { status: 'saved' } as never,
  backHref: '/editor/p',
  reviewHref: '/review',
  canUndo: false,
  canRedo: false,
  onUndo: vi.fn(),
  onRedo: vi.fn(),
  onExport: vi.fn(),
  onOpenSheet: vi.fn(),
  touchDrag: false,
  onToggleTouchDrag: vi.fn(),
};

describe('PhoneHeader at 360 px', () => {
  it('keeps the save chip, disabled undo and redo and the Export badge in a 360 container; the early-preview label is on the status line, not here', () => {
    wrap(
      <div style={{ width: 360 }} data-testid="frame">
        <PhoneHeader {...base} exportBadge />
      </div>,
    );
    expect(screen.getByTestId('phone-header')).toBeTruthy();
    expect(screen.getByTestId('save-state-chip').textContent).toBe('Saved');
    expect(screen.queryByRole('button', { name: /early preview/i })).toBeNull();
    expect((screen.getByTestId('phone-undo') as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByTestId('phone-redo') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId('phone-export-badge').textContent).toContain('Ready');
  });

  it('lets the name shrink and clip instead of overflowing (the styles that decide it)', () => {
    const css = readFileSync(resolve(process.cwd(), 'components/editor/phone/phone.module.css'), 'utf8');
    expect(css).toMatch(/\.headerName \{[^}]*min-width: 0/);
    expect(css).toMatch(/\.headerTitle \{[^}]*text-overflow: ellipsis/);
    expect(css).toMatch(/\.headerSub \{[^}]*overflow: hidden/);
    expect(css).toMatch(/\.header \{[^}]*min-width: 0/);
  });
});

describe('PhoneHeader overflow menu', () => {
  it('lists the seven phone entries and Ask the Assistant opens the assistant', async () => {
    const onAskAssistant = vi.fn();
    wrap(<PhoneHeader {...base} onAskAssistant={onAskAssistant} />);
    fireEvent.click(screen.getByTestId('phone-more'));
    for (const label of ['Assistant', 'Ask the Assistant', 'Review page', 'History', 'Drag clips with your finger (beta)', 'Copy link']) {
      expect(await screen.findByText(new RegExp('^' + label.replace(/[()\\]/g, '\\$&')))).toBeTruthy();
    }
    fireEvent.click(screen.getByTestId('phone-more-ask-assistant'));
    expect(onAskAssistant).toHaveBeenCalledTimes(1);
  });

  it('Assistant opens the proposal sheet; Ask the Assistant is absent without an assistant', async () => {
    wrap(<PhoneHeader {...base} />);
    fireEvent.click(screen.getByTestId('phone-more'));
    fireEvent.click(await screen.findByTestId('phone-more-assist'));
    expect(base.onOpenSheet).toHaveBeenCalledWith('assist');
    expect(screen.queryByTestId('phone-more-ask-assistant')).toBeNull();
  });

  it('Copy link writes the page address and says so', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    wrap(<PhoneHeader {...base} />);
    fireEvent.click(screen.getByTestId('phone-more'));
    fireEvent.click(await screen.findByTestId('phone-more-copy-link'));
    expect(writeText).toHaveBeenCalledWith(window.location.href);
    expect(await screen.findByText('Link copied')).toBeTruthy();
  });
});
