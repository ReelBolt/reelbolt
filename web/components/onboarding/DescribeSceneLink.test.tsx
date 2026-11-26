// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import { DescribeSceneLink } from './FirstFilmCard';

const hoisted = vi.hoisted(() => ({ push: vi.fn(), apply: vi.fn() }));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: hoisted.push }) }));
vi.mock('@/lib/api/workflows', () => ({ applyWorkflowTemplate: hoisted.apply }));

beforeEach(() => {
  hoisted.push.mockReset();
  hoisted.apply.mockReset();
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

const mount = () => render(<MantineProvider><Notifications /><DescribeSceneLink projectId="p1" /></MantineProvider>);

describe('DescribeSceneLink', () => {
  it('applies animation-from-idea (a recipe every plan can run) with skipIfExists and opens that app page', async () => {
    hoisted.apply.mockResolvedValue({ id: 'w9' });
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Describe a video instead' }));
    await waitFor(() => expect(hoisted.push).toHaveBeenCalledWith('/projects/p1/workflows/w9'));
    expect(hoisted.apply).toHaveBeenCalledWith('p1', 'animation-from-idea', true);
  });

  it('shows a failure toast, stays put and lets the person retry', async () => {
    hoisted.apply.mockRejectedValue(new ApiError('boom', 500, []));
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Describe a video instead' }));
    expect(await screen.findByText('Could not open the video app')).toBeTruthy();
    expect(hoisted.push).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Describe a video instead' })).toBeTruthy();
  });
});
