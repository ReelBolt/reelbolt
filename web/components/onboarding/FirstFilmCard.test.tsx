import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { DashboardOverview } from '@/lib/types/dashboard';
import { FirstFilmCard } from './FirstFilmCard';

vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));

const ov = (runs: number) => ({ recentExecutions: new Array(runs).fill({}), activeExecutions: [] }) as unknown as DashboardOverview;
const ui = (runs: number) => render(<MantineProvider><FirstFilmCard projects={[]} overview={ov(runs)} /></MantineProvider>);

beforeEach(() => {
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});
afterEach(() => { cleanup(); window.localStorage.clear(); });

describe('FirstFilmCard', () => {
  it('shows for a user with no run', () => {
    ui(0);
    expect(screen.getByTestId('first-film')).toBeTruthy();
  });
  it('is hidden once there is a run', () => {
    ui(1);
    expect(screen.queryByTestId('first-film')).toBeNull();
  });
});
