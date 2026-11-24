// @vitest-environment jsdom

/**
 * The workspace's storage bar (docs/design/local-folder-sync-and-storage.md Part 2, "Shown to
 * people"): "4.2 GB of 15 GB used", the top projects underneath, and "Unlimited" for a plan with no
 * limit — the one sentence the Billing page and Settings both render.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { StorageUsageBar } from './StorageUsageBar';
import { isStorageNearlyFull, storageFraction, storageUsageSentence } from '@/lib/types/storage';

// Mantine's provider reads `matchMedia` for the colour scheme, which jsdom does not implement.
beforeEach(() => {
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
    addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
});

const renderBar = (usage: Parameters<typeof StorageUsageBar>[0]['usage'], withProjects = false) =>
  render(
    <MantineProvider>
      <StorageUsageBar usage={usage} withProjects={withProjects} />
    </MantineProvider>,
  );

afterEach(cleanup);

describe('StorageUsageBar', () => {
  it('reads "4.2 GB of 15 GB used" and prints how much is free', () => {
    renderBar({ usedBytes: 4_509_715_660, limitBytes: 16_106_127_360, byProject: [] });

    const bar = screen.getByTestId('storage-usage');
    expect(bar.textContent).toContain('4.2 GB of 15 GB used');
    expect(bar.textContent).toContain('free of 15 GB');
    const labelled = screen.getByTestId('storage-usage-bar').querySelector('[aria-label]');
    expect(labelled?.getAttribute('aria-label') ?? '').toMatch(/Storage used: \d+%/);
  });

  it('says Unlimited, and claims no limit, when limitBytes is null', () => {
    renderBar({ usedBytes: 4_509_715_660, limitBytes: null, byProject: [] });

    const bar = screen.getByTestId('storage-usage');
    expect(bar.textContent).toContain('4.2 GB used');
    expect(bar.textContent).toContain('Unlimited');
    expect(bar.textContent).toContain('no storage limit');
  });

  it('lists the largest projects underneath, with their own sizes', () => {
    renderBar({
      usedBytes: 4_509_715_660,
      limitBytes: 16_106_127_360,
      byProject: [
        { projectId: 'p1', name: 'Launch reel', usedBytes: 3_221_225_472 },
        { projectId: 'p2', name: 'B-roll', usedBytes: 1_610_612_736 },
      ],
    }, true);

    const projects = screen.getByTestId('storage-top-projects');
    expect(projects.textContent).toContain('Largest projects');
    expect(within(projects).getByText('Launch reel')).toBeTruthy();
    expect(projects.textContent).toContain('3 GB');
    expect(projects.textContent).toContain('B-roll');
  });

  it('turns the bar red once 90 % of the plan is used', () => {
    renderBar({ usedBytes: 9_500_000_000, limitBytes: 10_000_000_000, byProject: [] });
    const bar = screen.getByTestId('storage-usage-bar');
    // Mantine sets the root's data attribute for a coloured progress bar.
    expect(bar.getAttribute('data-color') ?? bar.getAttribute('color') ?? bar.innerHTML).toContain('red');
  });
});

describe('storage figures', () => {
  it('clamps the fraction to [0, 1] and answers 0 for an unlimited plan', () => {
    expect(storageFraction({ usedBytes: 5, limitBytes: 10 })).toBe(0.5);
    expect(storageFraction({ usedBytes: 20, limitBytes: 10 })).toBe(1);
    expect(storageFraction({ usedBytes: 20, limitBytes: null })).toBe(0);
  });

  it('warns at exactly 90 %, not at 89 %, and never when unlimited', () => {
    expect(isStorageNearlyFull({ usedBytes: 9, limitBytes: 10 })).toBe(true);
    expect(isStorageNearlyFull({ usedBytes: 10, limitBytes: 10 })).toBe(true);
    expect(isStorageNearlyFull({ usedBytes: 8.9, limitBytes: 10 })).toBe(false);
    expect(isStorageNearlyFull({ usedBytes: 900, limitBytes: null })).toBe(false);
    expect(isStorageNearlyFull(undefined)).toBe(false);
  });

  it('builds the one sentence both pages use', () => {
    expect(storageUsageSentence({ usedBytes: 4_509_715_660, limitBytes: 16_106_127_360 })).toBe('4.2 GB of 15 GB used');
    expect(storageUsageSentence({ usedBytes: 1_000, limitBytes: null })).toBe('1000 B used · Unlimited');
    expect(storageUsageSentence({ usedBytes: 4_509_715_660, limitBytes: null })).toBe('4.2 GB used · Unlimited');
  });
});
