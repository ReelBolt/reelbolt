// @vitest-environment jsdom
/**
 * "Your first video is ready": the facts are the run's own (or absent), the three next steps are
 * entry points that already exist, and nothing blocks the page.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SWRConfig } from 'swr';

const env = vi.hoisted(() => ({
  push: vi.fn(),
  notify: vi.fn(),
  apply: vi.fn(),
  emit: vi.fn(),
  guard: vi.fn(),
  detail: undefined as unknown,
  fetched: [] as string[],
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: env.push }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: env.notify } }));
vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { email: 'ada@example.com' } }) }));
vi.mock('@/lib/api/workflows', () => ({ applyWorkflowTemplate: env.apply }));
vi.mock('@/lib/api/client', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/client')>()),
  fetcher: (key: string) => {
    env.fetched.push(key);
    return env.detail instanceof Error ? Promise.reject(env.detail) : Promise.resolve(env.detail);
  },
}));
vi.mock('@/lib/telemetry', async (orig) => ({ ...(await orig<typeof import('@/lib/telemetry')>()), emitFunnelEvent: env.emit }));
vi.mock('@/lib/outputs/output-check', () => ({
  useDownloadGuard: () => (url: string, name?: string) => (e: { preventDefault: () => void }) => { e.preventDefault(); env.guard(url, name); },
  useOutputCheck: () => ({ status: 'ok', message: null, remountKey: 0, onError: () => {} }),
}));
vi.mock('@/components/outputs/CheckedVideo', () => ({
  CheckedVideo: (props: Record<string, unknown>) => <video data-testid="first-video" src={props.src as string} aria-label={props['aria-label'] as string} />,
}));

import { FirstResultMoment } from './FirstResultMoment';
import type { ThirdStep } from './first-result';

const step = (stepType: string, stepOrder: number, out: unknown) => ({
  id: `r${stepOrder}`, workflowStepId: `s${stepOrder}`, stepType, stepOrder, status: 'Completed', output: '', outputJson: JSON.stringify(out),
});
const FOOTAGE_RUN = {
  id: 'e1',
  status: 'Passed',
  stepResults: [
    step('VideoAnalyze', 1, { view: { media: { durationSec: 60 }, sources: [{ src: 0, durationSec: 150 }, { src: 1, durationSec: 60 }, { src: 2, durationSec: 42 }] }, meta: {} }),
    step('VideoCompile', 3, { status: 'completed', segments: 9, outputDurationSec: 58.4, retainedRatio: 0.23 }),
  ],
};

interface Opts {
  third?: ThirdStep;
  showVideo?: boolean;
  showTitle?: boolean;
  durationSec?: number | null;
}
const handlers = { onChange: vi.fn(), onTryShape: vi.fn(), onDismiss: vi.fn() };

function mount(opts: Opts = {}) {
  return render(
    <MantineProvider>
      <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
        <FirstResultMoment
          projectId="p1"
          executionId="e1"
          output={{ stepResultId: 'sr1', fileName: 'Highlight.mp4', durationSec: opts.durationSec ?? null }}
          third={opts.third ?? 'subtitlesOrShape'}
          {...(opts.showVideo === undefined ? {} : { showVideo: opts.showVideo })}
          {...(opts.showTitle === undefined ? {} : { showTitle: opts.showTitle })}
          {...handlers}
        />
      </SWRConfig>
    </MantineProvider>,
  );
}

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  for (const fn of [env.push, env.notify, env.apply, env.emit, env.guard, handlers.onChange, handlers.onTryShape, handlers.onDismiss]) fn.mockReset();
  env.detail = FOOTAGE_RUN;
  env.fetched.length = 0;
});
afterEach(cleanup);

describe('FirstResultMoment', () => {
  it('frames the video as theirs, in the page and not over it', async () => {
    mount();
    const moment = screen.getByRole('region', { name: 'Your first video is ready' });
    expect(within(moment).getByRole('heading', { name: 'Your first video is ready' })).toBeTruthy();
    expect(within(moment).getByText('Press play on something finished. It is yours to post, change or build on.')).toBeTruthy();
    expect(within(moment).getByTestId('first-video').getAttribute('src')).toBe('/api/v1/projects/p1/outputs/sr1/download');
    // Not a dialog: nothing traps focus or covers the page.
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(moment.getAttribute('aria-modal')).toBeNull();
  });

  it('shows the before and after of this run from its own step outputs', async () => {
    mount();
    const facts = await screen.findByTestId('first-result-facts');
    expect(facts.getAttribute('aria-label')).toBe('This run in numbers');
    await waitFor(() => expect(facts.textContent).toContain('3 clips, 4:12 of footage'));
    expect(facts.textContent).toContain('Before');
    expect(facts.textContent).toContain('After');
    expect(facts.textContent).toContain('A finished video of 0:58, cut from 9 parts of that footage');
    // One read of the run, by its own endpoint.
    expect(env.fetched).toEqual(['/api/v1/projects/p1/executions/e1']);
  });

  it('says one clip in the singular and leaves "parts" out when the compile did not count them', async () => {
    env.detail = {
      id: 'e1',
      stepResults: [
        step('VideoAnalyze', 1, { view: { media: { durationSec: 95 } }, meta: {} }),
        step('VideoCompile', 2, { status: 'completed', outputDurationSec: 30 }),
      ],
    };
    mount();
    const facts = await screen.findByTestId('first-result-facts');
    await waitFor(() => expect(facts.textContent).toContain('1 clip, 1:35 of footage'));
    expect(facts.textContent).toContain('A finished video of 0:30');
    expect(facts.textContent).not.toContain('parts');
  });

  it('shows only the length when the run says nothing about the footage (a promo from code)', async () => {
    env.detail = { id: 'e1', stepResults: [step('Agent', 1, { anything: true })] };
    mount({ durationSec: 31.2 });
    const facts = await screen.findByTestId('first-result-facts');
    expect(facts.textContent).toBe('Your video runs 0:31.');
    expect(screen.queryByText('Before')).toBeNull();
  });

  it('shows no fact at all when the run reported none: nothing is estimated', async () => {
    env.detail = { id: 'e1', stepResults: [] };
    mount();
    await waitFor(() => expect(env.fetched).toHaveLength(1));
    expect(screen.queryByTestId('first-result-facts')).toBeNull();
    // The moment itself, the video and the next steps are all still there.
    expect(screen.getByRole('link', { name: 'Download' })).toBeTruthy();
  });

  it('shows no fact when the run could not be read, or the answer is another run\'s', async () => {
    env.detail = new Error('offline');
    mount();
    await waitFor(() => expect(env.fetched).toHaveLength(1));
    expect(screen.queryByTestId('first-result-facts')).toBeNull();
    cleanup();
    env.detail = { ...FOOTAGE_RUN, id: 'someone-else' };
    mount();
    await waitFor(() => expect(env.fetched).toHaveLength(2));
    expect(screen.queryByTestId('first-result-facts')).toBeNull();
  });

  it('Download is the existing download, and reports the export step once per person', () => {
    mount();
    const download = screen.getByRole('link', { name: 'Download' });
    expect(download.getAttribute('href')).toContain('/api/v1/projects/p1/outputs/sr1/download');
    expect(download.getAttribute('href')).toContain('saveAs=Highlight.mp4');
    fireEvent.click(download);
    expect(env.emit).toHaveBeenCalledWith('alpha.first_export_initiated', expect.any(String));
    expect(env.guard).toHaveBeenCalledWith(expect.stringContaining('/outputs/sr1/download'), 'Highlight.mp4');
  });

  it('"Change something" is the page\'s own change entry point', () => {
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Change something' }));
    expect(handlers.onChange).toHaveBeenCalledTimes(1);
  });

  it('the third step offers subtitles (the existing recipe apply) and another shape', async () => {
    env.apply.mockResolvedValue({ id: 'w-sub' });
    mount({ third: 'subtitlesOrShape' });
    fireEvent.click(screen.getByRole('button', { name: 'Add subtitles or try another shape' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Try another shape' }));
    expect(handlers.onTryShape).toHaveBeenCalledTimes(1);
    expect(env.apply).not.toHaveBeenCalled();
    cleanup();

    mount({ third: 'subtitlesOrShape' });
    fireEvent.click(screen.getByRole('button', { name: 'Add subtitles or try another shape' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Add subtitles' }));
    await waitFor(() => expect(env.apply).toHaveBeenCalledWith('p1', 'video-subtitles', true));
    await waitFor(() => expect(env.push).toHaveBeenCalledWith('/projects/p1/workflows/w-sub'));
    // What each one really does is said in words: subtitles work on a clip, a new shape is a new run.
    expect(screen.getByText(/burns subtitles into a clip you choose/)).toBeTruthy();
    expect(screen.getByText(/That is a new run\./)).toBeTruthy();
  });

  it('offers only what the app has', () => {
    mount({ third: 'shape' });
    fireEvent.click(screen.getByRole('button', { name: 'Try another shape' }));
    expect(handlers.onTryShape).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: /subtitles/i })).toBeNull();
    expect(screen.queryByText(/burns subtitles/)).toBeNull();
    cleanup();

    mount({ third: 'subtitles' });
    expect(screen.getByRole('button', { name: 'Add subtitles' })).toBeTruthy();
    expect(screen.queryByText(/That is a new run/)).toBeNull();
    cleanup();

    mount({ third: 'recipes' });
    expect(screen.getByRole('link', { name: 'Try another recipe' }).getAttribute('href')).toBe('/projects/p1?tab=workflows');
    expect(screen.queryByRole('button', { name: /shape|subtitles/i })).toBeNull();
  });

  it('says so when the subtitles app could not be set up, and stays usable', async () => {
    env.apply.mockRejectedValue(new Error('Recipes are switched off.'));
    mount({ third: 'subtitles' });
    fireEvent.click(screen.getByRole('button', { name: 'Add subtitles' }));
    await waitFor(() => expect(env.notify).toHaveBeenCalledWith(expect.objectContaining({ title: 'We could not set that up', message: 'Recipes are switched off.' })));
    expect(env.push).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Add subtitles' }).getAttribute('data-loading')).toBeNull());
  });

  it('closes on request', () => {
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(handlers.onDismiss).toHaveBeenCalledTimes(1);
  });

  it('leaves the player and the heading to the page where the page already has them', () => {
    mount({ showVideo: false, showTitle: false });
    expect(screen.queryByTestId('first-video')).toBeNull();
    expect(screen.queryByRole('heading')).toBeNull();
    // Still a named region.
    expect(screen.getByRole('region', { name: 'Your first video is ready' })).toBeTruthy();
  });

  it('every target is at least 44 px tall', () => {
    mount();
    for (const el of [screen.getByRole('link', { name: 'Download' }), screen.getByRole('button', { name: 'Change something' })]) {
      expect((el as HTMLElement).style.minHeight).toBe('44px');
    }
  });
});
