// @vitest-environment jsdom
/** The author line marks comments written by a public-API key with the key name; people stay plain. */

import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ReviewComment } from '@/lib/editing/api/editor-p9';
import { ReviewPanel } from '../ReviewPanel';

const state = vi.hoisted(() => ({ comments: [] as unknown[] }));
vi.mock('@/lib/hooks/use-editor-p9', () => ({
  useReviewComments: () => ({ data: state.comments, mutate: vi.fn(), isLoading: false, error: null }),
}));

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };

const comment = (over: Partial<ReviewComment> = {}): ReviewComment => ({
  id: 'c1',
  timelineId: 't',
  parentId: null,
  authorUserId: 'u1',
  authorName: 'Nightly bot',
  frame: 0,
  durationFrames: null,
  body: 'Looks good',
  resolved: false,
  createdAt: '2026-11-01T00:00:00Z',
  updatedAt: '2026-11-01T00:00:00Z',
  ...over,
});

const mount = () =>
  render(
    <MantineProvider>
      <ReviewPanel projectId="p" timelineId="t" rate={{ num: 24, den: 1 }} playheadFrame={0} onSeek={vi.fn()} />
    </MantineProvider>,
  );

beforeEach(() => {
  state.comments = [];
});

describe('ReviewPanel actorKind marker', () => {
  it('shows an API-key comment as "API key: <name>"', () => {
    state.comments = [comment({ id: 'c1', actorKind: 'apiKey', authorName: 'Nightly bot' })];
    mount();
    expect(screen.getByTestId('review-author-c1').textContent).toBe('API key: Nightly bot');
  });

  it('marks a reply written by an API key too', () => {
    state.comments = [comment({ id: 'c1' }), comment({ id: 'c2', parentId: 'c1', actorKind: 'apiKey', authorName: 'Nightly bot', createdAt: '2026-11-01T00:01:00Z' })];
    mount();
    expect(screen.getByTestId('review-author-c2').textContent).toBe('API key: Nightly bot');
  });

  it('leaves a person and a missing/unknown actorKind unmarked', () => {
    state.comments = [comment({ id: 'c1', actorKind: 'person', authorName: 'Ada' }), comment({ id: 'c2', actorKind: undefined, authorName: 'Bob' }), comment({ id: 'c3', actorKind: 'serviceAccount' as never, authorName: 'Carol' })];
    mount();
    expect(screen.getByTestId('review-author-c1').textContent).toBe('Ada');
    expect(screen.getByTestId('review-author-c2').textContent).toBe('Bob');
    expect(screen.getByTestId('review-author-c3').textContent).toBe('Carol');
  });

  it('still marks a guest as "(guest)" ahead of any actorKind', () => {
    state.comments = [comment({ id: 'c1', guest: true, guestName: 'Dee', actorKind: 'apiKey', authorName: 'Nightly bot' })];
    mount();
    expect(screen.getByTestId('review-author-c1').textContent).toBe('Dee (guest)');
  });
});
