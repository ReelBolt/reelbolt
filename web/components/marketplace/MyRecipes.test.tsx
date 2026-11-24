// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { MineRecipe } from '@/lib/types/marketplace';

const state = vi.hoisted(() => ({ items: undefined as MineRecipe[] | undefined, mutate: vi.fn() }));
vi.mock('@/lib/hooks/use-marketplace-owner', () => ({
  useMyRecipes: () => ({ items: state.items, error: undefined, isLoading: false, mutate: state.mutate }),
}));
vi.mock('./PublishDialog', () => ({ PublishDialog: ({ existing }: { existing?: MineRecipe }) => <div role="dialog">update {existing?.name}</div> }));
const api = vi.hoisted(() => ({ unpublish: vi.fn() }));
vi.mock('@/lib/api/marketplace', async (orig) => ({ ...(await orig<typeof import('@/lib/api/marketplace')>()), unpublishRecipe: api.unpublish }));

import { MyRecipes } from './MyRecipes';

const rec = (over: Partial<MineRecipe>): MineRecipe => ({ id: 'r1', name: 'Teaser', status: 'listed', version: 2, previewStatus: 'ready', copyCount: 12, runCount: 30, ratingAverage: 4.5, ratingCount: 9, ...over });

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  state.items = [rec({}), rec({ id: 'r2', name: 'Bad one', status: 'rejected', reasonCategory: 'copyright', previewStatus: 'failed', ratingCount: 1 })];
  state.mutate.mockReset().mockResolvedValue(undefined);
  api.unpublish.mockReset();
});
afterEach(cleanup);
const mount = () => render(<MantineProvider><MyRecipes /></MantineProvider>);

describe('MyRecipes', () => {
  it('shows status, reason category, preview status and stats', () => {
    mount();
    expect(screen.getByText('Live')).toBeTruthy();
    expect(screen.getByText('Not accepted')).toBeTruthy();
    expect(screen.getByText('Reason: It uses someone else\'s work.')).toBeTruthy();
    expect(screen.getAllByText('12 copies')[0]).toBeTruthy();
    expect(screen.getAllByText('30 runs')[0]).toBeTruthy();
    expect(screen.getByText('4.5 out of 5 (9)')).toBeTruthy();
    expect(screen.getByText('New')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'View public page' }).getAttribute('href')).toBe('/community/r1');
  });

  it('reopens the dialog to update', () => {
    mount();
    fireEvent.click(screen.getAllByRole('button', { name: 'Update' })[0]);
    expect(screen.getByRole('dialog').textContent).toBe('update Teaser');
  });

  it('unpublishes only after a confirmation', async () => {
    api.unpublish.mockResolvedValueOnce(undefined);
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Unpublish' }));
    expect(api.unpublish).not.toHaveBeenCalled();
    const d = await screen.findByRole('dialog');
    fireEvent.click(within(d).getByRole('button', { name: 'Unpublish' }));
    await waitFor(() => expect(api.unpublish).toHaveBeenCalledWith('r1'));
    await waitFor(() => expect(state.mutate).toHaveBeenCalled());
  });

  it('names only the author-safe reasons and never shows a raw code', () => {
    const fuzz = ['csam_match', 'safety', 'text_blocked', 'digest_match', 'CSAM', 'x9f3-RAW', '<b>x</b>', '', 'constructor', '__proto__'];
    for (const code of fuzz) {
      state.items = [rec({ id: 'x', name: 'Row', status: 'removed', reasonCategory: code })];
      const { container, unmount } = mount();
      expect(container.textContent).toContain('Reason: It does not follow the community rules.');
      if (code) expect(container.textContent).not.toContain(code);
      unmount();
    }
    for (const [code, words] of [['content', 'not acceptable'], ['policy', 'community rules'], ['preview', 'preview video'], ['copyright', "someone else's work"], ['quality', 'too low'], ['other', 'Another reason']]) {
      state.items = [rec({ id: 'y', name: 'Row', status: 'rejected', reasonCategory: code })];
      const { container, unmount } = mount();
      expect(container.textContent).toContain(words);
      unmount();
    }
  });

  it('shows a chip while the recipe is hidden for a report review', () => {
    state.items = [rec({ hiddenByReports: true })];
    mount();
    expect(screen.getByText('Hidden while we review a report')).toBeTruthy();
  });

  it('hides the public link for a recipe hidden by reports', () => {
    state.items = [rec({ hiddenByReports: true })];
    mount();
    expect(screen.queryByRole('link', { name: 'View public page' })).toBeNull();
  });

  it('explains a failed preview and offers the update dialog', () => {
    state.items = [rec({ status: 'draft', previewStatus: 'failed' })];
    mount();
    expect(screen.getByText('Preview failed', { selector: 'span' })).toBeTruthy();
    expect(screen.getByRole('alert').textContent).toBe('The preview could not be made. Update the recipe with another video.');
    fireEvent.click(screen.getByRole('button', { name: 'Update' }));
    expect(screen.getByRole('dialog').textContent).toBe('update Teaser');
  });

  it('says so when nothing was shared yet', () => {
    state.items = [];
    mount();
    expect(screen.getByText('You have not shared a recipe yet')).toBeTruthy();
  });
});
