// @vitest-environment jsdom
/**
 * editor-crash-hotfix: the Preview 3 s overlay is mounted on every editor page, and the activity feed calls
 * its `useJobFor` predicate once for each job it holds. The predicate read `runningId` before the `const`
 * that declares it (temporal dead zone), so as soon as the workspace had ANY job in the feed the editor threw
 * "Cannot access 'p' before initialization" and showed "This page couldn't load". The other overlay tests
 * run with an empty feed, where `find` never calls the predicate, so they could not see it.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ActivityJob } from '@/lib/activity/types';
import { preview3sStore } from '@/lib/editing/preview/preview3s-store';

// A feed holding jobs, evaluated exactly like the real hook (`allJobs.find(predicate)`).
const feed: Partial<ActivityJob>[] = [{ id: 'a', executionId: 'someone-elses-run', state: 'running' }];
vi.mock('@/lib/activity/ActivityProvider', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useJobFor: (predicate: (job: ActivityJob) => boolean) => (feed as ActivityJob[]).find(predicate),
}));

import { Preview3sOverlay } from '../Preview3s';

if (!window.matchMedia) {
  window.matchMedia = ((query: string) =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true })) as unknown as typeof window.matchMedia;
}

afterEach(() => {
  cleanup();
  preview3sStore.close();
});

describe('Preview 3 s overlay with jobs in the activity feed', () => {
  it('mounts idle without throwing and renders nothing', () => {
    expect(() =>
      render(
        <MantineProvider>
          <Preview3sOverlay />
        </MantineProvider>,
      ),
    ).not.toThrow();
    expect(screen.queryByTestId('preview3s-overlay')).toBeNull();
  });
});
