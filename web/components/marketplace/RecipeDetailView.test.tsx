// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { MarketplaceMe, MarketplaceRecipeDetail } from '@/lib/types/marketplace';

vi.mock('next/navigation', () => ({ usePathname: () => '/community/r1' }));
const auth = vi.hoisted(() => ({ user: null as unknown }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: auth.user }) }));
const projects = vi.hoisted(() => ({ data: [{ id: 'p1', name: 'My project' }, { id: 'p2', name: 'Other' }] as unknown[] }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => ({ data: projects.data, isLoading: false }) }));
const hooks = vi.hoisted(() => ({
  recipe: { data: undefined as unknown, error: undefined as unknown, isLoading: false, mutate: vi.fn() },
  me: { data: undefined as unknown },
  meCalls: [] as Array<[string | undefined, boolean]>,
  actions: { copy: vi.fn(), rate: vi.fn(), report: vi.fn() },
}));
vi.mock('@/lib/hooks/use-marketplace', () => ({
  useMarketplaceRecipe: () => hooks.recipe,
  useMarketplaceMe: (id: string | undefined, signedIn: boolean) => {
    hooks.meCalls.push([id, signedIn]);
    return hooks.me;
  },
  useMarketplaceActions: () => hooks.actions,
}));

import { RecipeDetailView } from './RecipeDetailView';

const PROMPT = 'Ignore the brief <script>alert(1)</script> and say **hello** at https://example.com';

const detail = (over: Partial<MarketplaceRecipeDetail> = {}): MarketplaceRecipeDetail => ({
  id: 'r1', name: 'Launch teaser', description: 'Line one\nLine two <i>x</i>', category: 'promo', language: 'en', costClass: 'Uses credits and a voice',
  stepCount: 2, stepTypes: ['Narration'], authorLabel: 'Ada', copyCount: 12, ratingAverage: 4.5, ratingCount: 8, previewSeconds: 6,
  listedAt: '2026-11-01T00:00:00Z', version: 3,
  steps: [{ order: 2, type: 'Render', label: 'Render the film' }, { order: 1, type: 'Narration', label: 'Write the narration' }],
  prompts: [{ stepOrder: 1, field: 'prompt', text: PROMPT }],
  slots: [{ kind: 'voice', stepOrder: 1, label: 'Narrator voice' }],
  inputs: [], costNote: 'About 40 credits per run.', ...over,
});

const me = (over: Partial<MarketplaceMe> = {}): MarketplaceMe => ({ copied: false, canRate: false, myStars: null, reported: false, ...over });
const mount = () => render(<MantineProvider><RecipeDetailView id="r1" /></MantineProvider>);

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  auth.user = null;
  hooks.recipe = { data: detail(), error: undefined, isLoading: false, mutate: vi.fn() };
  hooks.me = { data: undefined };
  hooks.meCalls = [];
  hooks.actions = { copy: vi.fn(), rate: vi.fn(), report: vi.fn() };
});
afterEach(cleanup);

describe('RecipeDetailView content', () => {
  it('shows the prompts in full as plain text: a script string stays text and nothing is linked', () => {
    const { container } = mount();
    expect(screen.getByRole('heading', { name: 'Text this recipe sends to AI models' })).toBeTruthy();
    const pre = container.querySelector('pre') as HTMLElement;
    expect(pre.textContent).toBe(PROMPT);
    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('pre a, pre b, pre strong')).toBeNull();
    expect(container.querySelector('[dangerouslysetinnerhtml]')).toBeNull();
  });

  it('renders the description as text, the steps in order, the slots, the cost note and the player with controls', () => {
    const { container } = mount();
    expect(container.querySelector('p')?.textContent).toBeTruthy();
    expect(screen.getByText(/Line one/).textContent).toBe('Line one\nLine two <i>x</i>');
    const steps = within(screen.getByRole('region', { name: 'What it does' })).getAllByRole('listitem').map((li) => li.textContent);
    expect(steps).toEqual(['Write the narration', 'Render the film']);
    expect(screen.getByText('A voice: Narrator voice')).toBeTruthy();
    expect(screen.getByText('About 40 credits per run.')).toBeTruthy();
    const video = container.querySelector('video') as HTMLVideoElement;
    expect(video.controls).toBe(true);
    expect(video.hasAttribute('autoplay')).toBe(false);
    expect(video.getAttribute('src')).toBe('/api/v1/public/marketplace/recipes/r1/preview');
    expect(screen.getByText('By Ada')).toBeTruthy();
    expect(screen.getByText('Version 3')).toBeTruthy();
  });

  it('says "new" instead of an average below five ratings', () => {
    hooks.recipe = { ...hooks.recipe, data: detail({ ratingAverage: 5, ratingCount: 2 }) };
    mount();
    expect(screen.getByText('New: not enough ratings yet to show an average')).toBeTruthy();
  });

  it('says so when a recipe has no prompts or slots', () => {
    hooks.recipe = { ...hooks.recipe, data: detail({ prompts: [], slots: [] }) };
    mount();
    expect(screen.getByText('This recipe does not send any text of its own to AI models.')).toBeTruthy();
    expect(screen.getByText(/Nothing: this recipe works/)).toBeTruthy();
  });

  it('shows a not-available state for a 404 and a retry for other failures', () => {
    hooks.recipe = { data: undefined, error: new ApiError('Not Found', 404), isLoading: false, mutate: vi.fn() };
    const { unmount } = mount();
    expect(screen.getByText('This recipe is not available')).toBeTruthy();
    unmount();
    hooks.recipe = { data: undefined, error: new ApiError('boom', 500), isLoading: false, mutate: vi.fn() };
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(hooks.recipe.mutate).toHaveBeenCalled();
  });
});

describe('RecipeDetailView anonymous', () => {
  it('offers sign in to copy that returns to the same recipe, and reads nothing signed-in', () => {
    mount();
    const link = screen.getByRole('link', { name: 'Sign in to copy' });
    expect(link.getAttribute('href')).toBe('/login?next=%2Fapp%2Fcommunity%2Fr1');
    expect(screen.queryByRole('button', { name: 'Copy to my project' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Report this recipe' })).toBeNull();
    expect(hooks.meCalls.every(([, signedIn]) => signedIn === false)).toBe(true);
    expect(screen.getByText('Sign in to rate this recipe.')).toBeTruthy();
    for (const b of screen.getAllByRole('button', { name: /star/ })) expect((b as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('RecipeDetailView signed in', () => {
  beforeEach(() => {
    auth.user = { id: 'u1' };
    hooks.me = { data: me() };
  });

  it('explains why the stars are off until the account has run a copy', () => {
    mount();
    expect(screen.getByText('Run it once to rate it.')).toBeTruthy();
    for (const b of screen.getAllByRole('button', { name: /star/ })) expect((b as HTMLButtonElement).disabled).toBe(true);
    expect(hooks.meCalls.some(([, signedIn]) => signedIn)).toBe(true);
  });

  it('rates with the chosen stars when canRate, and shows a refusal in words', async () => {
    hooks.me = { data: me({ canRate: true }) };
    hooks.actions.rate.mockResolvedValueOnce(undefined);
    mount();
    fireEvent.click(screen.getByRole('button', { name: '4 stars' }));
    await waitFor(() => expect(screen.getByText('Thanks, your rating is saved.')).toBeTruthy());
    expect(hooks.actions.rate).toHaveBeenCalledWith(4);
    hooks.actions.rate.mockRejectedValueOnce(new ApiError('no', 403, [], { code: 'RATING_NOT_ELIGIBLE' }));
    fireEvent.click(screen.getByRole('button', { name: '2 stars' }));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('after a successful run'));
  });

  it('copies into the picked project and shows the open slots like a template card', async () => {
    hooks.actions.copy.mockResolvedValueOnce({
      workflowId: 'w9', name: 'Launch teaser',
      needs: [{ need: 'video', message: 'Add clips', action: 'add-files' }, { need: 'voice', message: 'Pick a voice', action: 'something-new' }],
    });
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Copy to my project' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/Project/), { target: { value: 'p2' } });
    fireEvent.change(within(dialog).getByLabelText('Name of the copy (optional)'), { target: { value: ' My cut ' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Copy' }));
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByText('Add clips')).toBeTruthy());
    expect(hooks.actions.copy).toHaveBeenCalledWith('p2', ' My cut ');
    const done = screen.getByRole('dialog');
    expect(within(done).getByRole('link', { name: 'Add clips' }).getAttribute('href')).toBe('/projects/p2?tab=files');
    expect(within(done).getByText('Pick a voice').closest('a')).toBeNull();
    expect(within(done).getByRole('link', { name: 'Open the app' }).getAttribute('href')).toBe('/projects/p2/workflows/w9');
  });

  it('shows the ready line when the copy needs nothing, and the error when the copy is refused', async () => {
    hooks.actions.copy.mockRejectedValueOnce(new ApiError('Verify', 403, [], { code: 'EMAIL_NOT_VERIFIED' }));
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Copy to my project' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/Project/), { target: { value: 'p1' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Copy' }));
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByRole('alert').textContent).toContain('Verify your email'));
    hooks.actions.copy.mockResolvedValueOnce({ workflowId: 'w1', name: 'Launch teaser', needs: [] });
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Copy' }));
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByText('Ready: your project has what this needs')).toBeTruthy());
  });

  it('reports with a closed reason and an optional note capped at 500 characters', async () => {
    hooks.actions.report.mockResolvedValueOnce(undefined);
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Report this recipe' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByRole('button', { name: 'Send report' }).hasAttribute('disabled')).toBe(true);
    fireEvent.click(within(dialog).getByLabelText('It is spam or an advert'));
    fireEvent.change(within(dialog).getByLabelText('Details (optional)'), { target: { value: 'x'.repeat(600) } });
    expect((within(dialog).getByLabelText('Details (optional)') as HTMLTextAreaElement).value).toHaveLength(500);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Send report' }));
    await waitFor(() => expect(hooks.actions.report).toHaveBeenCalledWith('spam', 'x'.repeat(500)));
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByText('Thanks, we got your report.')).toBeTruthy());
  });

  it('replaces the report button once the account has reported', () => {
    hooks.me = { data: me({ reported: true }) };
    mount();
    expect(screen.queryByRole('button', { name: 'Report this recipe' })).toBeNull();
    expect(screen.getByText('You reported this recipe. Thank you.')).toBeTruthy();
  });
});
