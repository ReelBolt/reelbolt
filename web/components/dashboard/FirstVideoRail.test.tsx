// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';
import type { Project } from '@/lib/types/project';
import { FirstVideoRail, type FirstVideoRailProps } from './FirstVideoRail';

vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: () => ({ data: { entitlements: { maxOutputHeight: 2160, unlocks: [] } } }) }));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('next/link', () => ({
  default: ({ children, href, ...rest }: { children: React.ReactNode; href: string }) => <a href={href} {...rest}>{children}</a>,
}));

const project = { id: 'p1', name: 'My first video', createdAt: '2026-11-11T10:00:00Z' } as Project;
let workflows: unknown[];
let apps: unknown[];
let files: unknown[];
let canConnect: boolean;
let asked: string[];

beforeEach(() => {
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  workflows = [{ id: 'w1', name: 'Highlight edit from footage' }];
  apps = [{ workflowId: 'w1', title: 'Highlight edit from footage', category: 'footage' }];
  files = [];
  canConnect = false;
  asked = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      asked.push(url);
      const body = url === '/api/v1/projects/p1/workflows' ? workflows
        : url === '/api/v1/projects/p1/workflow-apps' ? { apps, recipes: [] }
        : url === '/api/v1/projects/p1/files' ? files
        : url === '/api/v1/github/account' ? { canConnect } : {};
      return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
    }),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const onCreate = vi.fn();
const mount = (props: Partial<FirstVideoRailProps> = {}) =>
  render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <FirstVideoRail persona={null} material="footage" project={project} addDone={false} runDone={false} welcomeEnabled onCreate={onCreate} {...props} />
      </SWRConfig>
    </MantineProvider>,
  );

const steps = () => within(screen.getByRole('list', { name: 'Three steps to your first video' })).getAllByRole('listitem');
const state = () => steps().map((li) => `${li.getAttribute('data-step')}:${li.getAttribute('data-state')}`);
const link = (li: HTMLElement) => li.querySelector('a')?.getAttribute('href') ?? null;

describe('FirstVideoRail', () => {
  it('shows the three steps in order: add clips, say what it is for, run', () => {
    mount();
    expect(screen.getByRole('heading', { level: 2 }).textContent).toBe('Your first video');
    expect(steps().map((li) => li.getAttribute('data-step'))).toEqual(['add', 'brief', 'run']);
    expect(steps()[0].textContent).toContain('Add your clips');
    expect(steps()[1].textContent).toContain('Say what it is for');
    expect(steps()[2].textContent).toContain('Press Run');
  });

  it('starts with "add" as the next step, and each step links to the exact place', async () => {
    mount();
    expect(state()).toEqual(['add:next', 'brief:later', 'run:later']);
    expect(steps()[0].getAttribute('aria-current')).toBe('step');
    expect(link(steps()[0])).toBe('/projects/p1?tab=files');
    // The project's one app: its run page, where the brief is typed and Run is pressed.
    await waitFor(() => expect(link(steps()[1])).toBe('/projects/p1/workflows/w1'));
    expect(link(steps()[2])).toBe('/projects/p1/workflows/w1');
    expect(screen.getByRole('link', { name: 'Add your clips' }).getAttribute('href')).toBe('/projects/p1?tab=files');
  });

  it('ticks "add" once the project holds something and moves on to the brief', async () => {
    mount({ addDone: true });
    expect(state()).toEqual(['add:done', 'brief:next', 'run:later']);
    expect(steps()[0].textContent).toContain('(done)');
    expect(steps()[1].textContent).toContain('(next)');
    await waitFor(() =>
      expect(screen.getByRole('link', { name: 'Say what it is for and press Run' }).getAttribute('href')).toBe('/projects/p1/workflows/w1'),
    );
  });

  it('ticks the brief only together with the run: it has no signal of its own', () => {
    mount({ addDone: true, runDone: false });
    expect(state()[1]).toBe('brief:next');
    cleanup();
    mount({ addDone: true, runDone: true });
    expect(state()).toEqual(['add:done', 'brief:done', 'run:done']);
  });

  it('falls back to the project\'s Apps tab when the project has several apps or none', async () => {
    workflows = [{ id: 'w1' }, { id: 'w2' }];
    mount({ addDone: true });
    await waitFor(() => expect(asked).toContain('/api/v1/projects/p1/workflows'));
    await new Promise((r) => setTimeout(r, 20));
    expect(link(steps()[1])).toBe('/projects/p1?tab=workflows');
    expect(link(steps()[2])).toBe('/projects/p1?tab=workflows');
  });

  it('speaks of code, and links where code is added, for a project that starts from code', async () => {
    canConnect = true;
    mount({ material: 'code', persona: 'software' });
    expect(steps()[0].textContent).toContain('Add your code');
    expect(steps()[0].textContent).toContain('Connect the repository, or upload the code as a zip.');
    await waitFor(() => expect(link(steps()[0])).toBe('/projects/p1?tab=repository'));
    cleanup();
    canConnect = false;
    mount({ material: 'code', persona: 'software' });
    await waitFor(() => expect(asked.filter((u) => u === '/api/v1/github/account')).toHaveLength(2));
    await new Promise((r) => setTimeout(r, 20));
    expect(link(steps()[0])).toBe('/projects/p1?tab=files');
  });

  it('does not ask about repositories for a project that does not start from code', async () => {
    mount({ material: 'footage' });
    await waitFor(() => expect(asked).toContain('/api/v1/projects/p1/workflows'));
    expect(asked).not.toContain('/api/v1/github/account');
  });

  it('keeps the shot list one click away for a project that starts from an idea, until clips are in', () => {
    mount({ material: 'idea' });
    expect(steps()[0].textContent).toContain('Film the five clips on your shot list, then drop them in.');
    expect(screen.getByRole('link', { name: 'See your shot list' }).getAttribute('href')).toBe('/welcome?step=shots&project=p1');
    cleanup();
    mount({ material: 'idea', addDone: true });
    expect(screen.queryByRole('link', { name: 'See your shot list' })).toBeNull();
    cleanup();
    mount({ material: 'idea', welcomeEnabled: false });
    expect(screen.queryByRole('link', { name: 'See your shot list' })).toBeNull();
  });

  it('with no project yet, starts at the welcome questions and carries the tour\'s "new project" anchor', () => {
    mount({ project: undefined });
    expect(state()).toEqual(['add:later', 'brief:later', 'run:later']);
    expect(steps().every((li) => link(li) === null)).toBe(true);
    const start = screen.getByRole('link', { name: 'Start your first video' });
    expect(start.getAttribute('href')).toBe('/welcome');
    expect(start.getAttribute('data-tour')).toBe('new-project');
    expect(asked).toEqual([]);
  });

  it('with no project and the welcome flow off, opens the plain new-project form instead', () => {
    onCreate.mockClear();
    mount({ project: undefined, welcomeEnabled: false });
    expect(screen.queryByRole('link', { name: 'Start your first video' })).toBeNull();
    const start = screen.getByRole('button', { name: 'Start your first video' });
    expect(start.getAttribute('data-tour')).toBe('new-project');
    fireEvent.click(start);
    expect(onCreate).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['creator', 'for my followers'],
    ['business', 'Our new autumn menu'],
    ['educator', 'for new students'],
    ['nonprofit', 'for our supporters'],
    ['software', 'What is new in this release'],
    ['agency', 'A first cut of the client'],
    ['other', 'What happened and why it matters'],
    [null, 'What happened and why it matters'],
  ] as const)('gives a %s an example of what to say, in their own terms', (persona, words) => {
    mount({ persona });
    expect(steps()[1].textContent).toContain('For example:');
    expect(steps()[1].textContent).toContain(words);
  });

  // No answer to "what do you have" on record (an account older than the welcome questions, a skip,
  // the flow switched off): the rail goes by what the project really is.
  describe('with no material on record', () => {
    it('speaks of clips and links the run page when the project\'s one app edits footage', async () => {
      mount({ material: null });
      await waitFor(() => expect(steps()[0].textContent).toContain('Add your clips'));
      await waitFor(() => expect(link(steps()[1])).toBe('/projects/p1/workflows/w1'));
      expect(link(steps()[2])).toBe('/projects/p1/workflows/w1');
      expect(asked).not.toContain('/api/v1/projects/p1/files');
    });

    it('never says "add your clips" and then sends the person to a promo-from-code app', async () => {
      // The automatic starter of a project made the plain way: the promo-from-code app.
      workflows = [{ id: 'w1', name: 'Promo video from your code' }];
      apps = [{ workflowId: 'w1', title: 'Promo video from your code', category: 'promo' }];
      files = [{ originalFileName: 'holiday.mp4', mimeType: 'video/mp4' }];
      mount({ material: null, addDone: true });
      await waitFor(() => expect(asked).toContain('/api/v1/projects/p1/files'));
      await new Promise((r) => setTimeout(r, 20));
      expect(steps()[0].textContent).toContain('Add what you start from');
      expect(steps()[0].textContent).not.toContain('Add your clips');
      // The Apps tab, whose order follows the project's files, not the code promo's run page.
      expect(link(steps()[1])).toBe('/projects/p1?tab=workflows');
      expect(link(steps()[2])).toBe('/projects/p1?tab=workflows');
      expect(screen.getByRole('link', { name: 'Say what it is for and press Run' }).getAttribute('href')).toBe('/projects/p1?tab=workflows');
    });

    it('speaks of code and links the promo app once the project really holds code', async () => {
      workflows = [{ id: 'w1', name: 'Promo video from your code' }];
      apps = [{ workflowId: 'w1', title: 'Promo video from your code', category: 'promo' }];
      files = [{ originalFileName: 'App.tsx', mimeType: 'text/plain' }];
      mount({ material: null, addDone: true });
      await waitFor(() => expect(steps()[0].textContent).toContain('Add your code'));
      await waitFor(() => expect(link(steps()[1])).toBe('/projects/p1/workflows/w1'));
    });

    it('stays neutral, and on the Apps tab, until the project\'s app is known', () => {
      mount({ material: null });
      expect(steps()[0].textContent).toContain('Add what you start from');
      expect(link(steps()[0])).toBe('/projects/p1?tab=files');
      expect(link(steps()[1])).toBe('/projects/p1?tab=workflows');
    });
  });

  it('promises nothing it cannot keep: no price, no count, no time saving, no score', () => {
    mount();
    const text = screen.getByTestId('first-video-rail').textContent ?? '';
    expect(text).toContain('Minutes for a small job, sometimes half an hour for a big one.');
    expect(text).not.toMatch(/\d+\s*(x|%|hours?|credits?)\b/i);
    expect(text).not.toMatch(/scored|faster|saves?/i);
  });
});

describe('FirstVideoRail no-footage actions', () => {
  it('offers a sample film and a describe-a-scene action for the idea path, not for footage', () => {
    mount({ material: 'idea' });
    expect(screen.getByRole('button', { name: 'Make your first film' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Describe a scene instead' })).toBeTruthy();
    cleanup();
    mount({ material: 'idea', showSampleFilm: false });
    expect(screen.queryByRole('button', { name: 'Make your first film' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Describe a scene instead' })).toBeTruthy();
    cleanup();
    mount({ material: 'footage' });
    expect(screen.queryByRole('button', { name: 'Describe a scene instead' })).toBeNull();
  });
});
