// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MarketplaceRecipeCard } from '@/lib/types/marketplace';
import { RecipeCardView } from './RecipeCardView';

function media(over: { coarse?: boolean; reduced?: boolean } = {}) {
  global.matchMedia = ((q: string) => ({
    matches: q.includes('pointer: coarse') ? Boolean(over.coarse) : q.includes('prefers-reduced-motion') ? Boolean(over.reduced) : false,
    media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
  })) as unknown as typeof matchMedia;
}

const recipe = (over: Partial<MarketplaceRecipeCard> = {}): MarketplaceRecipeCard => ({
  id: 'r1', name: 'Launch teaser', description: 'A short teaser <b>bold</b>', category: 'promo', language: 'en', costClass: 'Uses credits',
  stepCount: 3, stepTypes: ['Narration'], authorLabel: 'Ada', copyCount: 12, ratingAverage: 4.5, ratingCount: 8, previewSeconds: 6,
  listedAt: '2026-11-01T00:00:00Z', version: 1, ...over,
});

const play = vi.fn(() => Promise.resolve());
beforeEach(() => {
  media();
  play.mockClear();
  HTMLMediaElement.prototype.play = play as unknown as () => Promise<void>;
});
afterEach(cleanup);

const mount = (r: MarketplaceRecipeCard = recipe()) => render(<MantineProvider><ul><RecipeCardView recipe={r} /></ul></MantineProvider>);

describe('RecipeCardView preview', () => {
  it('shows only the poster on load: no video, nothing plays', () => {
    const { container } = mount();
    expect(container.querySelector('video')).toBeNull();
    expect(container.querySelector('img')?.getAttribute('src')).toBe('/api/v1/public/marketplace/recipes/r1/poster');
    expect(play).not.toHaveBeenCalled();
  });

  it('plays a muted, looping preview on hover and removes it on leave', () => {
    const { container } = mount();
    const card = screen.getByRole('article');
    fireEvent.mouseEnter(card);
    const video = container.querySelector('video') as HTMLVideoElement;
    expect(video.getAttribute('src')).toBe('/api/v1/public/marketplace/recipes/r1/preview');
    expect(video.muted).toBe(true);
    expect(video.loop).toBe(true);
    expect(video.hasAttribute('autoplay')).toBe(false);
    expect(play).toHaveBeenCalledTimes(1);
    fireEvent.mouseLeave(card);
    expect(container.querySelector('video')).toBeNull();
  });

  it('plays on keyboard focus too', () => {
    const { container } = mount();
    fireEvent.focus(screen.getByRole('link', { name: 'Launch teaser' }));
    expect(container.querySelector('video')).not.toBeNull();
  });

  it('never plays on a touch device', () => {
    media({ coarse: true });
    const { container } = mount();
    fireEvent.mouseEnter(screen.getByRole('article'));
    fireEvent.focus(screen.getByRole('link', { name: 'Launch teaser' }));
    expect(container.querySelector('video')).toBeNull();
    expect(play).not.toHaveBeenCalled();
  });

  it('never plays with reduced motion', () => {
    media({ reduced: true });
    const { container } = mount();
    fireEvent.mouseEnter(screen.getByRole('article'));
    expect(container.querySelector('video')).toBeNull();
  });

  it('opens the detail page from the title link', () => {
    mount();
    expect(screen.getByRole('link', { name: 'Launch teaser' }).getAttribute('href')).toBe('/community/r1');
  });
});

describe('RecipeCardView facts', () => {
  it('shows the average from five ratings and hides it below', () => {
    const { unmount } = mount();
    expect(screen.getByText('4.5')).toBeTruthy();
    expect(screen.queryByText('New')).toBeNull();
    unmount();
    mount(recipe({ ratingAverage: 5, ratingCount: 4 }));
    expect(screen.queryByText('5.0')).toBeNull();
    expect(screen.getByText('New')).toBeTruthy();
  });

  it('shows author, copies, steps and the cost badge, and renders text as text', () => {
    mount(recipe({ costClass: 'Uses credits and AI shots' }));
    expect(screen.getByText('By Ada')).toBeTruthy();
    expect(screen.getByText('Copied 12 times')).toBeTruthy();
    expect(screen.getByText('3 steps')).toBeTruthy();
    expect(screen.getByText('Uses credits and AI-made clips')).toBeTruthy();
    expect(screen.getByText('A short teaser <b>bold</b>')).toBeTruthy();
  });
});
