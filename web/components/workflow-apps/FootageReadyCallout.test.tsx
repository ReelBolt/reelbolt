// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

const push = vi.hoisted(() => vi.fn());
vi.mock('next/navigation', () => ({ useRouter: () => ({ push, replace: vi.fn() }) }));
const show = vi.hoisted(() => vi.fn());
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...a: unknown[]) => show(...a) } }));
const applyTemplate = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api/workflows', () => ({ applyWorkflowTemplate: applyTemplate }));

import { FootageReadyCallout, footageClipCount } from './FootageReadyCallout';

const video = (name: string, category = 'userFiles') => ({ originalFileName: name, mimeType: 'video/mp4', category });

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  push.mockReset();
  show.mockReset();
  applyTemplate.mockReset();
});
afterEach(cleanup);

const mount = (count: number) => render(<MantineProvider><FootageReadyCallout projectId="p1" count={count} /></MantineProvider>);

describe('footageClipCount', () => {
  it('counts uploaded clips when there is no code', () => {
    expect(footageClipCount([video('a.mp4'), video('b.mp4'), { originalFileName: 'n.txt', mimeType: 'text/plain' }])).toBe(2);
  });
  it('is zero when there is code, no clips, or only renders', () => {
    expect(footageClipCount([video('a.mp4'), { originalFileName: 'app.ts', mimeType: 'text/plain' }])).toBe(0);
    expect(footageClipCount([{ originalFileName: 'n.txt', mimeType: 'text/plain' }])).toBe(0);
    expect(footageClipCount([video('render.mp4', 'outputFiles')])).toBe(0);
    expect(footageClipCount(undefined)).toBe(0);
  });
});

describe('FootageReadyCallout', () => {
  it('renders nothing for no clips', () => {
    mount(0);
    expect(screen.queryByTestId('footage-ready')).toBeNull();
  });

  it('names the count and links the secondary action to Apps filtered to Footage', () => {
    mount(13);
    expect(screen.getByText('Your clips are ready')).toBeTruthy();
    expect(screen.getByText(/13 video clips are uploaded/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'See other footage apps' }).getAttribute('href')).toBe('/projects/p1?tab=workflows&category=footage');
  });

  it('the primary button creates the highlight edit app and opens it', async () => {
    applyTemplate.mockResolvedValue({ id: 'w9' });
    mount(1);
    expect(screen.getByText(/1 video clip is uploaded/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Make a highlight edit' }));
    await waitFor(() => expect(push).toHaveBeenCalledWith('/projects/p1/workflows/w9'));
    expect(applyTemplate).toHaveBeenCalledWith('p1', 'edit-my-video', true);
  });

  it('says so and stays put when setting up fails', async () => {
    applyTemplate.mockRejectedValue(new Error('No space'));
    mount(2);
    fireEvent.click(screen.getByRole('button', { name: 'Make a highlight edit' }));
    await waitFor(() => expect(show).toHaveBeenCalled());
    expect(show.mock.calls[0]![0]).toMatchObject({ title: 'We could not set that up', message: 'No space' });
    expect(push).not.toHaveBeenCalled();
  });
});
