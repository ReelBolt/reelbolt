// @vitest-environment jsdom
/**
 * The clip action menu: every timeline gesture the phone cannot rely on has a visible button
 * (Delete, Lift, Split, Nudge one frame either way), reachable by tap with no keyboard and no drag.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ClipActionMenu, type ClipActionMenuProps } from '../ClipActionMenu';
import { resetTestViewport, setTestViewport } from '@/lib/editing/__tests__/viewport';

let matching = new Set<string>();
beforeEach(() => {
  resetTestViewport();
  matching = new Set();
  global.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: matching.has(query),
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => true,
    }) as MediaQueryList;
});
afterEach(cleanup);

function setup(overrides: Partial<ClipActionMenuProps> = {}) {
  const props: ClipActionMenuProps = {
    selectedItems: [{ id: 'c2', recordIn: 60, recordOut: 120 }],
    playheadFrame: 90,
    readOnly: false,
    onDelete: vi.fn(),
    onLift: vi.fn(),
    onSplit: vi.fn(),
    onNudge: vi.fn(),
    ...overrides,
  };
  render(
    <MantineProvider>
      <ClipActionMenu {...props} />
    </MantineProvider>,
  );
  return props;
}

const open = async () => {
  await act(async () => {
    fireEvent.click(screen.getByTestId('clip-actions-open'));
  });
};

describe('ClipActionMenu', () => {
  it('cannot open with nothing selected', () => {
    setup({ selectedItems: [] });
    expect((screen.getByTestId('clip-actions-open') as HTMLButtonElement).disabled).toBe(true);
  });

  it('offers Delete, Lift, Split and Nudge as buttons, each wired to its action', async () => {
    const props = setup();
    await open();
    expect(screen.getByTestId('clip-actions-menu')).toBeTruthy();

    await act(async () => fireEvent.click(screen.getByTestId('clip-action-nudge-forward')));
    expect(props.onNudge).toHaveBeenCalledWith(1);
    // Nudging keeps the menu open so the clip can be stepped several frames.
    await act(async () => fireEvent.click(screen.getByTestId('clip-action-nudge-back')));
    expect(props.onNudge).toHaveBeenLastCalledWith(-1);

    await act(async () => fireEvent.click(screen.getByTestId('clip-action-split')));
    expect(props.onSplit).toHaveBeenCalledTimes(1);

    await open();
    await act(async () => fireEvent.click(screen.getByTestId('clip-action-lift')));
    expect(props.onLift).toHaveBeenCalledTimes(1);

    await open();
    await act(async () => fireEvent.click(screen.getByTestId('clip-action-delete')));
    expect(props.onDelete).toHaveBeenCalledTimes(1);
  });

  it('labels Delete as closing the gap and Lift as leaving it, in plain words', async () => {
    setup();
    await open();
    expect(screen.getByTestId('clip-action-delete').textContent).toBe('Delete');
    expect(screen.getByTestId('clip-action-lift').textContent).toBe('Lift (leave gap)');
    expect(screen.getByTestId('clip-action-nudge-back').getAttribute('aria-label')).toBe('Nudge 1 frame earlier');
    expect(screen.getByTestId('clip-action-nudge-forward').getAttribute('aria-label')).toBe('Nudge 1 frame later');
  });

  it('explains why Split is unavailable when the playhead is not over the clip', async () => {
    const props = setup({ playheadFrame: 10 });
    await open();
    const split = screen.getByTestId('clip-action-split') as HTMLButtonElement;
    expect(split.disabled).toBe(true);
    expect(screen.getByTestId('clip-action-split-hint').textContent).toBe('Move the playhead over the clip to split it.');
    fireEvent.click(split);
    expect(props.onSplit).not.toHaveBeenCalled();
  });

  it('disables every edit while the timeline is read-only', async () => {
    setup({ readOnly: true });
    expect((screen.getByTestId('clip-actions-open') as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows no unreachable-marquee note on a phone or a larger screen', async () => {
    setTestViewport('phone');
    setup();
    await act(async () => {});
    await open();
    expect(screen.queryByTestId('needs-larger-screen')).toBeNull();
  });

  it('prints the reason under a disabled Join, Fit to the gap and Replace entry (not tooltip-only)', async () => {
    const run = vi.fn();
    setup({
      extraActions: [
        { id: 'join', label: 'Join with next clip', unavailable: 'The two clips come from different files.', run },
        { id: 'fit-to-fill', label: 'Fit to the gap', unavailable: 'There is no gap after this clip to fill.', run },
        { id: 'replace', label: 'Replace with...', run },
      ],
    });
    await open();
    expect(screen.getByTestId('clip-action-join-reason').textContent).toBe('The two clips come from different files.');
    expect(screen.getByTestId('clip-action-fit-to-fill-reason').textContent).toBe('There is no gap after this clip to fill.');
    expect(screen.queryByTestId('clip-action-replace-reason')).toBeNull();
    expect((screen.getByTestId('clip-action-join') as HTMLButtonElement).disabled).toBe(true);
    await act(async () => fireEvent.click(screen.getByTestId('clip-action-replace')));
    expect(run).toHaveBeenCalledTimes(1);
  });
});
