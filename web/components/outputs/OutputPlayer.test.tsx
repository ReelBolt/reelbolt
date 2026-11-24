// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, renderHook, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import type { OutputVideo } from '@/lib/api/outputs';
import type { Project } from '@/lib/types/project';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.mock('next/link', () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

import { OutputPlayer, shareRenderLink } from './OutputPlayer';
import { useT } from '@/i18n';
import { ProjectRendersTab } from '@/components/projects/ProjectSections';

const SHARE_SCOPE_NOTE = 'Share copies a link that opens only for people signed in to this project. Public links are not available yet.';
const GENERIC_LOAD_ERROR = 'This video could not be loaded.';
const MISSING_LOAD_ERROR = 'This video could not be loaded. It may have been removed from storage.';
const MODERATION_LOAD_ERROR = 'This video is temporarily unavailable while it passes the publication check. Try again in a moment.';
const translator = () => renderHook(() => useT('outputs')).result.current;

const wrap = (ui: React.ReactElement) => render(<MantineProvider><Notifications />{ui}</MantineProvider>);

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  Object.defineProperty(navigator, 'share', { value: undefined, configurable: true });
  Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
});

describe('Share', () => {
  it('falls back to the clipboard when the Web Share API is missing, and says so', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'share', { value: undefined, configurable: true });
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    wrap(<OutputPlayer output={{ stepResultId: 's1', projectId: 'p1', fileName: 'x.mp4', producedAt: '2026-10-04T12:00:00Z' }} />);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Share' })); });
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('/api/v1/projects/p1/outputs/s1/download'));
    expect(await screen.findByText('Link copied')).toBeTruthy();
  });

  it('shows an error with the link when neither sharing nor copying works', async () => {
    Object.defineProperty(navigator, 'share', { value: vi.fn().mockRejectedValue(new Error('NotAllowed')), configurable: true });
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn().mockRejectedValue(new Error('denied')) }, configurable: true });
    document.execCommand = vi.fn(() => false);
    wrap(<div />);
    let outcome: string | undefined;
    const t = translator();
    await act(async () => { outcome = await shareRenderLink('Render', 'https://example.test/r', t); });
    expect(outcome).toBe('failed');
    expect(await screen.findByText('Could not share this video')).toBeTruthy();
    expect(screen.getByText(/https:\/\/example.test\/r/)).toBeTruthy();
  });

  it('treats closing the native share sheet as a choice, not a failure', async () => {
    const abort = Object.assign(new Error('cancelled'), { name: 'AbortError' });
    Object.defineProperty(navigator, 'share', { value: vi.fn().mockRejectedValue(abort), configurable: true });
    const writeText = vi.fn();
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    wrap(<div />);
    let outcome: string | undefined;
    const t = translator();
    await act(async () => { outcome = await shareRenderLink('Render', 'https://example.test/r', t); });
    expect(outcome).toBe('cancelled');
    expect(writeText).not.toHaveBeenCalled();
  });

  it('tells the customer who the link opens for', () => {
    wrap(<OutputPlayer output={{ stepResultId: 's1', projectId: 'p1', fileName: 'x.mp4', producedAt: '2026-10-04T12:00:00Z' }} />);
    expect(screen.getAllByText(SHARE_SCOPE_NOTE).some((el) => el.tagName === 'P')).toBe(true);
  });
});

describe('Player load failures', () => {
  const output = { stepResultId: 's1', projectId: 'p1', fileName: 'x.mp4', producedAt: '2026-10-04T12:00:00Z' };
  const src = '/api/v1/projects/p1/outputs/s1/download';

  async function failAndRead(fetchImpl: () => Promise<Response>, text: string | RegExp) {
    const fetchMock = vi.fn(fetchImpl);
    vi.stubGlobal('fetch', fetchMock);
    wrap(<OutputPlayer output={output} />);
    fireEvent.error(screen.getByLabelText('Video x.mp4'));
    expect(await screen.findByText(text)).toBeTruthy();
    return fetchMock;
  }

  it('says a moderated render is temporarily unavailable, not removed from storage', async () => {
    const fetchMock = await failAndRead(
      () => Promise.resolve(new Response(
        JSON.stringify({ code: 'CONTENT_MODERATION_UNAVAILABLE', message: 'Publication is temporarily unavailable.' }),
        { status: 409, headers: { 'Content-Type': 'application/json' } },
      )),
      /temporarily unavailable while it passes the publication check/,
    );

    expect(screen.getByText(MODERATION_LOAD_ERROR)).toBeTruthy();
    expect(screen.queryByText(/removed from storage/)).toBeNull();
    // One ranged probe, exactly as the download route expects — no retry loop.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(src, expect.objectContaining({ headers: { Range: 'bytes=0-0' } }));
  });

  it('keeps the removed-from-storage copy only for a genuinely missing object', async () => {
    const fetchMock = await failAndRead(
      () => Promise.resolve(new Response(
        JSON.stringify({ code: 'not_found', message: 'Output artifact not found in storage.' }),
        { status: 404, headers: { 'Content-Type': 'application/json' } },
      )),
      MISSING_LOAD_ERROR,
    );
    expect(screen.queryByText(/temporarily unavailable/)).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('keeps the generic line when the probe itself fails, without retrying', async () => {
    const fetchMock = await failAndRead(
      () => Promise.reject(new TypeError('Failed to fetch')),
      GENERIC_LOAD_ERROR,
    );
    expect(screen.queryByText(/removed from storage/)).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('Player while the video is being checked', () => {
  const output = { stepResultId: 's1', projectId: 'p1', fileName: 'x.mp4', producedAt: '2026-10-04T12:00:00Z' };

  it('says it is checking, disables Download, and shows the video by itself once ready', async () => {
    vi.useFakeTimers();
    try {
      const pending = new Response(JSON.stringify({ code: 'CONTENT_MODERATION_PENDING', message: 'm' }), { status: 409, headers: { 'Content-Type': 'application/json' } });
      const fetchMock = vi.fn().mockResolvedValueOnce(pending).mockResolvedValue(new Response(null, { status: 206 }));
      vi.stubGlobal('fetch', fetchMock);
      const { container } = wrap(<OutputPlayer output={output} />);
      fireEvent.error(screen.getByLabelText('Video x.mp4'));
      await act(async () => { await vi.advanceTimersByTimeAsync(0); });
      expect(screen.getByText('Checking your video. It will appear here by itself.')).toBeTruthy();
      expect(container.querySelector('video')).toBeNull();
      expect(screen.getByText('Download').closest('a')?.hasAttribute('data-disabled')).toBe(true);
      await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
      expect(container.querySelector('video')).not.toBeNull();
      expect(fetchMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('Renders tab', () => {
  const project = { id: 'p1', name: 'Launch' } as Project;
  const base = { workflowExecutionId: 'ex2', workflowStepId: 'st', storageKey: 'k', workflowDefinitionId: 'wf', workflowName: '60s product story', runNumber: 2, runStartedAt: '2026-10-04T12:02:00Z', executionStatus: 'Passed' };
  const outputs: OutputVideo[] = [
    // Newest first, as the API returns them. The newest belongs to a run still in progress and the
    // FINAL video is second — the tab must still open on the final video.
    { ...base, stepResultId: 'draft-late', fileName: 'aaaa-1111.mp4', producedAt: '2026-10-04T13:00:00Z', executionStatus: 'Running', workflowExecutionId: 'ex3', runNumber: 3, isFinal: false },
    { ...base, stepResultId: 'final', fileName: 'acf96fd3-d4c3.mp4', producedAt: '2026-10-04T12:40:00Z', isFinal: true, durationSec: 61.2, width: 1080, height: 1920,
      warnings: [{ code: 'narration_dropped', message: '2 narration lines could not be placed.' }] },
    { ...base, stepResultId: 'draft', fileName: 'bbbb-2222.mp4', producedAt: '2026-10-04T12:30:00Z', isFinal: false },
  ];

  it('opens on the final render with a friendly title, run, length, shape and the run warnings', () => {
    wrap(<ProjectRendersTab project={project} outputs={outputs} />);
    const player = screen.getByLabelText('Video 60s product story, Run 2');
    expect(player).toBeTruthy();
    expect(screen.queryByText('acf96fd3-d4c3.mp4')).toBeNull();
    expect(screen.getByText(/Run 2 · 4 Oct, \d\d:\d\d · 1:01 · Vertical 9:16 · 1080×1920/)).toBeTruthy();
    expect(screen.getByText('2 narration lines could not be placed.')).toBeTruthy();
    // Takes: the three renders are takes of ONE video (same workflow step). The rail shows the video
    // once, by its latest finished take; the draft and the still-running take fold under it.
    expect(screen.getByRole('listitem', { name: /Run 2 \(Final\)/ })).toBeTruthy();
    expect(screen.queryByRole('listitem', { name: /Run 2 \(Draft\)/ })).toBeNull();
    expect(screen.queryByRole('listitem', { name: /Run 3 \(In progress\)/ })).toBeNull();
    expect(screen.getByRole('button', { name: '2 earlier versions' })).toBeTruthy();
    expect(screen.getByRole('link', { name: /Open run/ }).getAttribute('href')).toBe('/projects/p1/workflows/wf/executions/ex2');
    expect(screen.getByRole('link', { name: /Download/ }).getAttribute('download')).toBe('60s product story, Run 2.mp4');
  });
});

describe('media kind', () => {
  const base = { stepResultId: 's3', projectId: 'p1', producedAt: '2026-10-04T12:00:00Z' };
  it('renders an image output as an img, not a video', () => {
    const { container } = wrap(<OutputPlayer output={{ ...base, fileName: 'step-3-image1.png' }} />);
    const img = screen.getByTestId('output-image');
    expect(img.tagName).toBe('IMG');
    expect(img.getAttribute('src')).toBe('/api/v1/projects/p1/outputs/s3/download?w=1920');
    expect(container.querySelector('video')).toBeNull();
  });
  it('still renders a video for .mp4', () => {
    const { container } = wrap(<OutputPlayer output={{ ...base, fileName: 'out.mp4' }} />);
    expect(container.querySelector('video')).not.toBeNull();
    expect(screen.queryByTestId('output-image')).toBeNull();
  });
});
