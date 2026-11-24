// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { WorkflowAppsResponse } from '@/lib/types/workflow';

const push = vi.fn();
vi.mock('next/navigation', () => ({ useRouter: () => ({ push, replace: vi.fn() }) }));
const show = vi.fn();
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => show(...a) } }));

const gallery = vi.hoisted(() => ({
  state: { data: undefined, error: undefined, isLoading: false, mutate: vi.fn() } as {
    data: WorkflowAppsResponse | undefined;
    error: unknown;
    isLoading: boolean;
    mutate: () => unknown;
  },
}));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({ useWorkflowApps: () => gallery.state }));
const projectFiles = vi.hoisted(() => ({ data: [] as { originalFileName: string; mimeType: string }[] }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: projectFiles.data }) }));
const applyTemplate = vi.hoisted(() => vi.fn());
// The welcome answer ("what do you have"): none by default, as for anyone who was never asked.
const onboarding = vi.hoisted(() => ({ profile: null as { material: string | null } | null }));
vi.mock('@/lib/hooks/use-onboarding', () => ({ useOnboarding: () => ({ profile: onboarding.profile }) }));
vi.mock('@/lib/api/workflows', () => ({ applyWorkflowTemplate: applyTemplate }));

import { AppsGallery } from './AppsGallery';

const NOW = new Date().toISOString();

function response(overrides: Partial<WorkflowAppsResponse> = {}): WorkflowAppsResponse {
  return {
    apps: [
      {
        workflowId: 'w1', title: 'Explainer video', tagline: 'A narrated walkthrough', category: 'promo',
        costClass: 'Uses credits and a voice', inputsCount: 5, lastRun: { status: 'Passed', finishedAt: NOW }, source: 'promo',
      },
      {
        workflowId: 'w2', title: 'Highlight edit', tagline: 'Tight cut from your clips', category: 'footage',
        costClass: 'No credits', inputsCount: 3, lastRun: { status: 'Failed', finishedAt: NOW }, source: 'custom',
      },
      {
        workflowId: 'w3', title: 'Brand profile', tagline: null, category: 'brand',
        costClass: 'Uses credits and AI shots', inputsCount: 1, lastRun: null, source: 'custom',
      },
    ],
    recipes: [
      {
        key: 'quick-reel', title: 'Quick vertical reel', tagline: 'A fast reel', category: 'promo', costClass: 'Uses credits',
        inputsCount: 2, fit: 'ready', missing: [],
      },
      {
        key: 'dub', title: 'Dubbed video', tagline: 'Translate your video', category: 'footage', costClass: 'Uses credits and a voice',
        inputsCount: 4, fit: 'needs', missing: [{ need: 'video', message: 'Add clips', action: 'add-files' }],
      },
    ],
    ...overrides,
  };
}

function renderGallery() {
  return render(<MantineProvider><AppsGallery projectId="p1" /></MantineProvider>);
}

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  gallery.state = { data: response(), error: undefined, isLoading: false, mutate: vi.fn() };
  push.mockClear();
  show.mockClear();
  applyTemplate.mockReset();
  projectFiles.data = [];
  onboarding.profile = null;
});
afterEach(cleanup);

describe('AppsGallery goal chooser', () => {
  const empty = () => {
    gallery.state = {
      ...gallery.state,
      data: { apps: [], recipes: [{ key: 'video-subtitles', title: 'Subtitles app', tagline: null, category: 'footage', costClass: 'No credits', inputsCount: 1, fit: 'ready', missing: [] }] },
    };
  };

  it('shows eight goal tiles in a labelled group when there are no apps yet', () => {
    empty();
    renderGallery();
    const group = within(screen.getByRole('group', { name: 'What do you want to make?' }));
    expect(group.queryByRole('button', { name: /Thumbnail|Miniature/i })).toBeNull();
    expect(group.getAllByRole('button')).toHaveLength(8);
    expect(group.getByRole('button', { name: /Something else/ })).toBeTruthy();
  });

  it('a goal tile sets up its recipe and opens it', async () => {
    empty();
    applyTemplate.mockResolvedValue({ id: 'wf9' });
    renderGallery();
    fireEvent.click(within(screen.getByRole('group', { name: 'What do you want to make?' })).getByRole('button', { name: /Subtitles/ }));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/wf9'));
    expect(applyTemplate).toHaveBeenCalledWith('p1', 'video-subtitles', true);
  });

  it('a goal without a recipe here is marked and does nothing', () => {
    empty();
    renderGallery();
    const tile = screen.getByRole('button', { name: /Cut my footage/ });
    expect(tile.getAttribute('aria-disabled')).toBe('true');
    fireEvent.click(tile);
    expect(applyTemplate).not.toHaveBeenCalled();
  });

  it('leads, open, when the only app is the untouched auto-made promo', () => {
    gallery.state = { ...gallery.state, data: { ...response(), apps: [{ workflowId: 'w0', title: 'Promo video from your code', tagline: null, category: 'promo', costClass: 'No credits', inputsCount: 1, lastRun: null, source: 'promo' }] } };
    renderGallery();
    expect(screen.getByRole('group', { name: 'What do you want to make?' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'What do you want to make?' })).toBeNull();
  });

  it('stays at the top but collapsed once an app has run, and opens on its heading', () => {
    renderGallery();
    const toggle = screen.getByRole('button', { name: 'What do you want to make?' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByRole('group', { name: 'What do you want to make?' })).toBeTruthy();
  });
});

describe('AppsGallery', () => {
  it('shows a labelled loading status while the one request is in flight', () => {
    gallery.state = { ...gallery.state, data: undefined, isLoading: true };
    renderGallery();
    expect(screen.getByRole('status', { name: 'Loading your apps' })).toBeTruthy();
  });

  it('lists apps, then recipes, with cost badges in words and a custom entry last', () => {
    renderGallery();
    const apps = within(screen.getByRole('region', { name: 'Your apps' }));
    expect(apps.getByText('Explainer video')).toBeTruthy();
    expect(apps.getByText('Uses credits and a voice')).toBeTruthy();
    expect(apps.getByText('Uses credits and AI-made clips')).toBeTruthy();
    expect(apps.getByText('No credits')).toBeTruthy();
    const recipes = within(screen.getByRole('region', { name: 'All apps' }));
    expect(recipes.getByText('Quick vertical reel')).toBeTruthy();
    const items = recipes.getAllByRole('listitem');
    expect(within(items[items.length - 1]).getByText('Build your own (advanced)')).toBeTruthy();
    expect(recipes.getByRole('link', { name: 'Build your own app' }).getAttribute('href')).toBe('/projects/p1/workflows/new');
  });

  it('shows Finished and Failed with an icon and a word, and "Not run yet" for a fresh app', () => {
    renderGallery();
    expect(screen.getByText(/^Finished/)).toBeTruthy();
    expect(screen.getByText(/^Failed/)).toBeTruthy();
    expect(screen.getByText('Not run yet')).toBeTruthy();
  });

  it('opens an app on its page', () => {
    renderGallery();
    expect(screen.getByRole('link', { name: 'Open Explainer video' }).getAttribute('href')).toBe('/projects/p1/workflows/w1');
  });

  it('turns a missing need into an action link and marks a ready recipe in words', () => {
    renderGallery();
    expect(screen.getByRole('link', { name: 'Add clips' }).getAttribute('href')).toBe('/projects/p1?tab=files');
    expect(screen.getByText(/Ready: your project has what this needs/)).toBeTruthy();
  });

  it('filters by category chip (a radiogroup) and by search, and announces the count', () => {
    renderGallery();
    const group = screen.getByRole('radiogroup', { name: 'Category' });
    fireEvent.click(within(group).getByRole('radio', { name: 'Footage' }));
    expect(within(group).getByRole('radio', { name: 'Footage' }).getAttribute('aria-checked')).toBe('true');
    expect(screen.queryByText('Explainer video')).toBeNull();
    expect(screen.getByText('Highlight edit')).toBeTruthy();
    expect(screen.queryByText('Quick vertical reel')).toBeNull();
    expect(screen.getByText('Dubbed video')).toBeTruthy();
    fireEvent.change(screen.getByRole('textbox', { name: 'Search apps' }), { target: { value: 'tight' } });
    expect(screen.getByText('Highlight edit')).toBeTruthy();
    expect(screen.queryByText('Dubbed video')).toBeNull();
    expect(screen.getByText('1 result')).toBeTruthy();
  });

  it('says so when nothing matches', () => {
    renderGallery();
    fireEvent.change(screen.getByRole('textbox', { name: 'Search apps' }), { target: { value: 'zzzz' } });
    expect(screen.getByText('No apps match your search.')).toBeTruthy();
    expect(screen.getByText('We do not make that yet. Try the assistant, or browse all apps.')).toBeTruthy();
  });

  it('leads an empty project with up to three suggested recipes, the ready ones first', () => {
    gallery.state = {
      ...gallery.state,
      data: response({
        apps: [],
        recipes: [
          { key: 'a', title: 'Needs recipe', tagline: null, category: 'promo', costClass: 'Uses credits', inputsCount: 1, fit: 'needs', missing: [{ need: 'code', message: 'Add your code', action: 'add-files' }] },
          { key: 'b', title: 'Ready recipe', tagline: null, category: 'promo', costClass: 'Uses credits', inputsCount: 1, fit: 'ready', missing: [] },
        ],
      }),
    };
    renderGallery();
    expect(screen.getByText('No apps yet')).toBeTruthy();
    const titles = screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent);
    expect(titles.slice(0, 2)).toEqual(['Ready recipe', 'Needs recipe']);
    expect(screen.getAllByText('Suggested for your project')).toHaveLength(2);
  });

  it('provisions a picked recipe with a skeleton, then opens its page', async () => {
    let resolve!: (w: { id: string; name: string }) => void;
    applyTemplate.mockReturnValue(new Promise((r) => { resolve = r; }));
    renderGallery();
    fireEvent.click(screen.getByRole('button', { name: 'Use this recipe for Quick vertical reel' }));
    expect(applyTemplate).toHaveBeenCalledWith('p1', 'quick-reel', true);
    expect(screen.getByRole('status', { name: 'Setting up Quick vertical reel' })).toBeTruthy();
    // A second press while provisioning is ignored.
    fireEvent.click(screen.getByRole('button', { name: 'Use this recipe for Dubbed video' }));
    expect(applyTemplate).toHaveBeenCalledTimes(1);
    await act(async () => { resolve({ id: 'new-wf', name: 'Quick vertical reel' }); });
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/new-wf'));
  });

  it('keeps the gallery and says so when provisioning fails', async () => {
    applyTemplate.mockRejectedValue(new Error('Template unavailable'));
    renderGallery();
    fireEvent.click(screen.getByRole('button', { name: 'Use this recipe for Quick vertical reel' }));
    await waitFor(() => expect(show).toHaveBeenCalled());
    expect(push).not.toHaveBeenCalled();
    expect(screen.queryByRole('status', { name: /Setting up/ })).toBeNull();
  });

  it('names every recipe button with its visible text (label in name)', () => {
    renderGallery();
    for (const b of screen.getAllByRole('button', { name: /Use this recipe/ })) {
      expect(b.textContent?.startsWith('Use this recipe')).toBe(true);
    }
  });

  it('keeps the busy card named, and marks the other recipe buttons unavailable', async () => {
    applyTemplate.mockReturnValue(new Promise(() => {}));
    renderGallery();
    fireEvent.click(screen.getByRole('button', { name: 'Use this recipe for Quick vertical reel' }));
    expect(screen.getByRole('article', { name: 'Quick vertical reel' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Use this recipe for Dubbed video' }).getAttribute('aria-disabled')).toBe('true');
    expect(screen.getByText(/The others are unavailable/)).toBeTruthy();
  });

  it('shows the cost explanation in the card text, not only a tooltip', () => {
    renderGallery();
    expect(screen.getAllByText(/That spend is capped for each run/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Very large files can use processing time/).length).toBeGreaterThan(0);
  });

  it('renders an unknown need action as plain text, not a link', () => {
    gallery.state = {
      ...gallery.state,
      data: response({
        recipes: [{ key: 'x', title: 'X', tagline: null, category: 'promo', costClass: 'Uses credits', inputsCount: 1, fit: 'needs',
          missing: [{ need: 'n', message: 'Do something new', action: 'future-action' as never }] }],
      }),
    };
    renderGallery();
    expect(screen.getByText('Do something new')).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Do something new' })).toBeNull();
  });

  it('links "not included in your plan" to the plans, so it is never dead text', () => {
    gallery.state = {
      ...gallery.state,
      data: response({
        recipes: [{ key: 'broll', title: 'Footage edit with AI b-roll', tagline: null, category: 'footage', costClass: 'Uses credits and AI shots', inputsCount: 1, fit: 'needs',
          missing: [{ need: 'plan:video_generation', message: 'Video generation is not included in your plan', action: 'upgrade-plan' }] }],
      }),
    };
    renderGallery();
    expect(screen.getByRole('link', { name: 'Video generation is not included in your plan' }).getAttribute('href')).toBe('/settings/billing#plans');
  });

  it('moves the category with the arrow keys and keeps one tab stop', () => {
    renderGallery();
    const all = screen.getByRole('radio', { name: 'All' });
    expect(all.getAttribute('tabindex')).toBe('0');
    expect(screen.getByRole('radio', { name: 'Promo' }).getAttribute('tabindex')).toBe('-1');
    fireEvent.keyDown(all, { key: 'ArrowRight' });
    expect(screen.getByRole('radio', { name: 'Promo' }).getAttribute('aria-checked')).toBe('true');
    fireEvent.keyDown(screen.getByRole('radio', { name: 'Promo' }), { key: 'ArrowLeft' });
    expect(screen.getByRole('radio', { name: 'All' }).getAttribute('aria-checked')).toBe('true');
  });

  it('keeps an already loaded gallery when a refresh fails', () => {
    gallery.state = { ...gallery.state, error: new Error('refresh failed') };
    renderGallery();
    expect(screen.getByText('Explainer video')).toBeTruthy();
    expect(screen.getByText(/could not refresh/)).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ranks an empty project by the files present: video clips lead with footage recipes', () => {
    projectFiles.data = [{ originalFileName: 'clip.mp4', mimeType: 'video/mp4' }];
    gallery.state = {
      ...gallery.state,
      data: response({
        apps: [],
        recipes: [
          { key: 'p', title: 'Promo ready', tagline: null, category: 'promo', costClass: 'Uses credits', inputsCount: 1, fit: 'ready', missing: [] },
          { key: 'f', title: 'Footage needs', tagline: null, category: 'footage', costClass: 'Uses credits', inputsCount: 1, fit: 'needs', missing: [{ need: 'x', message: 'Connect a voice provider', action: 'connect-provider' }] },
        ],
      }),
    };
    renderGallery();
    const titles = screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent);
    expect(titles.slice(0, 2)).toEqual(['Footage needs', 'Promo ready']);
  });

  it.each([
    ['a server failure', Object.assign(new Error('Internal Server Error'), { status: 500 }), 'Something went wrong on our side', true],
    ['a network failure', new TypeError('Failed to fetch'), 'Could not reach ReelBolt', true],
    ['a missing gallery', Object.assign(new Error(''), { status: 404 }), 'Not found', false],
    ['a refused gallery', Object.assign(new Error(''), { status: 403 }), 'No permission', false],
  ])('says %s plainly, never the raw message, and offers Retry only when retrying can help', (_n, err, title, retry) => {
    gallery.state = { ...gallery.state, data: undefined, error: err };
    renderGallery();
    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain(title);
    expect(alert.textContent).not.toContain('Internal Server Error');
    const button = screen.queryByRole('button', { name: 'Retry' });
    expect(Boolean(button)).toBe(retry);
    if (button) {
      fireEvent.click(button);
      expect(gallery.state.mutate).toHaveBeenCalled();
    }
  });

  it('keeps the loading status and offers Retry once loading is slow', () => {
    vi.useFakeTimers();
    try {
      gallery.state = { ...gallery.state, data: undefined, isLoading: true };
      renderGallery();
      act(() => { vi.advanceTimersByTime(10_500); });
      expect(screen.getByText(/taking longer than usual/)).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });

  describe('clips and no code', () => {
    const recipe = (key: string, title: string, category: 'promo' | 'footage', fit: 'ready' | 'needs') => ({
      key, title, tagline: null, category, costClass: 'Uses credits' as const, inputsCount: 1, fit,
      missing: fit === 'ready' ? [] : [{ need: 'code', message: 'Add your code', action: 'add-files' as const }],
    });
    const promoApp = {
      workflowId: 'wp', title: 'Promo video from your code', tagline: null, category: 'promo' as const,
      costClass: 'Uses credits' as const, inputsCount: 2, lastRun: null, source: 'promo',
    };
    const recipes = [
      recipe('code-1', 'Code promo', 'promo', 'needs'),
      recipe('code-2', 'Code tour', 'promo', 'needs'),
      recipe('highlight', 'Highlight edit from footage', 'footage', 'ready'),
    ];
    const headings = () => screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent).filter((h) => h !== 'What do you want to make?');
    const recipeTitles = () =>
      within(screen.getByRole('region', { name: 'All apps' })).getAllByRole('heading', { level: 3 }).map((h) => h.textContent);

    it('puts the ready footage recipe first and the unrun promo app after the recipes', () => {
      projectFiles.data = [{ originalFileName: 'a.mp4', mimeType: 'video/mp4' }];
      gallery.state = { ...gallery.state, data: { apps: [promoApp], recipes } };
      renderGallery();
      expect(recipeTitles()[0]).toBe('Highlight edit from footage');
      expect(headings()).toEqual(['All apps', 'Your apps']);
      expect(screen.getByText('Promo video from your code')).toBeTruthy();
    });

    it('keeps apps first and Promo first for a project with code', () => {
      projectFiles.data = [{ originalFileName: 'a.mp4', mimeType: 'video/mp4' }, { originalFileName: 'app.ts', mimeType: 'text/plain' }];
      gallery.state = { ...gallery.state, data: { apps: [promoApp], recipes: [recipes[2], recipes[0], recipes[1]].map((r) => ({ ...r, fit: 'ready' as const, missing: [] })) } };
      renderGallery();
      expect(headings()).toEqual(['Your apps', 'All apps']);
      expect(recipeTitles().slice(0, 3)).toEqual(['Highlight edit from footage', 'Code promo', 'Code tour']);
    });

    it('keeps apps first for a project with no files, and ready recipes still lead', () => {
      gallery.state = { ...gallery.state, data: { apps: [promoApp], recipes } };
      renderGallery();
      expect(headings()).toEqual(['Your apps', 'All apps']);
      expect(recipeTitles()[0]).toBe('Highlight edit from footage');
    });

    it('keeps apps first once the person has run one', () => {
      projectFiles.data = [{ originalFileName: 'a.mp4', mimeType: 'video/mp4' }];
      gallery.state = { ...gallery.state, data: { apps: [{ ...promoApp, lastRun: { status: 'Passed', finishedAt: NOW } }], recipes } };
      renderGallery();
      expect(headings()).toEqual(['Your apps', 'All apps']);
    });

    // A first project of someone who starts from footage or an idea, before any file is there
    // (docs/design/new-user-funnel.md section 4): the code promos must not lead.
    for (const material of ['footage', 'idea']) {
      it(`leads a no-code project of a person who starts from ${material} with the footage recipes`, () => {
        onboarding.profile = { material };
        const needsClips = { ...recipes[2]!, fit: 'needs' as const, missing: [{ need: 'video', message: 'Add clips', action: 'add-files' as const }] };
        // No app yet (the project was made with the footage starter, which the gallery lists as a recipe until it exists).
        gallery.state = { ...gallery.state, data: { apps: [], recipes: [recipes[0]!, recipes[1]!, needsClips] } };
        renderGallery();
        expect(recipeTitles()[0]).toBe('Highlight edit from footage');
        expect(within(screen.getByRole('article', { name: 'Highlight edit from footage' })).getByText('Suggested for your project')).toBeTruthy();
        cleanup();

        // With the code promo auto-created and never run, the recipes lead and "Your apps" follows.
        gallery.state = { ...gallery.state, data: { apps: [promoApp], recipes: [recipes[0]!, recipes[1]!, needsClips] } };
        renderGallery();
        expect(headings()).toEqual(['All apps', 'Your apps']);
        expect(recipeTitles()[0]).toBe('Highlight edit from footage');
      });
    }

    it('keeps the promo-first order for a person who starts from code, and once the project holds code', () => {
      onboarding.profile = { material: 'code' };
      gallery.state = { ...gallery.state, data: { apps: [promoApp], recipes: recipes.map((r) => ({ ...r, fit: 'ready' as const, missing: [] })) } };
      renderGallery();
      expect(headings()).toEqual(['Your apps', 'All apps']);
      expect(recipeTitles().slice(0, 2)).toEqual(['Code promo', 'Code tour']);
      cleanup();

      // The files win over the answer: code in the project means the code recipes are the fit.
      onboarding.profile = { material: 'footage' };
      projectFiles.data = [{ originalFileName: 'app.ts', mimeType: 'text/plain' }];
      renderGallery();
      expect(headings()).toEqual(['Your apps', 'All apps']);
      expect(recipeTitles().slice(0, 2)).toEqual(['Code promo', 'Code tour']);
    });

    it('opens on the category it is given', () => {
      gallery.state = { ...gallery.state, data: { apps: [promoApp], recipes } };
      render(<MantineProvider><AppsGallery projectId="p1" initialCategory="footage" /></MantineProvider>);
      expect(screen.getByRole('radio', { name: 'Footage' }).getAttribute('aria-checked')).toBe('true');
      expect(screen.queryByText('Code promo')).toBeNull();
    });
  });
});
