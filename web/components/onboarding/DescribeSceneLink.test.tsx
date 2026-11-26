// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import { DescribeSceneLink } from './FirstFilmCard';

const hoisted = vi.hoisted(() => ({ push: vi.fn(), apply: vi.fn(), summary: { data: undefined as unknown } }));
vi.mock('@/lib/hooks/use-billing', () => ({ useBillingSummary: () => hoisted.summary }));
const lacks = [{ feature: 'video_generation', planKey: 'pro', planName: 'Pro' }];
const plan = (videoGenAllowed: boolean) => ({ data: { entitlements: { maxOutputHeight: 1080, unlocks: videoGenAllowed ? [] : lacks } } });
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: hoisted.push }) }));
vi.mock('@/lib/api/workflows', () => ({ applyWorkflowTemplate: hoisted.apply }));

beforeEach(() => {
  hoisted.push.mockReset();
  hoisted.apply.mockReset();
  hoisted.summary = plan(true);
  window.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

const mount = () => render(<MantineProvider><Notifications /><DescribeSceneLink projectId="p1" /></MantineProvider>);

describe('DescribeSceneLink', () => {
  it.each([
    ['a plan without video generation (Trial, Creator)', plan(false)],
    ['a plan not read yet', { data: undefined }],
    ['billing off (no summary)', { data: undefined }],
    ['an older API that sends no unlocks', { data: { entitlements: { maxOutputHeight: 1080 } } }],
  ])('is not drawn for %s, so nobody lands on an app Run refuses', (_name, summary) => {
    hoisted.summary = summary as typeof hoisted.summary;
    mount();
    expect(screen.queryByRole('button', { name: 'Describe a scene instead' })).toBeNull();
  });


  it('applies ai-clip-from-idea with skipIfExists and opens that app page', async () => {
    hoisted.apply.mockResolvedValue({ id: 'w9' });
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Describe a scene instead' }));
    await waitFor(() => expect(hoisted.push).toHaveBeenCalledWith('/projects/p1/workflows/w9'));
    expect(hoisted.apply).toHaveBeenCalledWith('p1', 'ai-clip-from-idea', true);
  });

  it('shows a failure toast, stays put and lets the person retry', async () => {
    hoisted.apply.mockRejectedValue(new ApiError('boom', 500, []));
    mount();
    fireEvent.click(screen.getByRole('button', { name: 'Describe a scene instead' }));
    expect(await screen.findByText('Could not open the scene app')).toBeTruthy();
    expect(hoisted.push).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Describe a scene instead' })).toBeTruthy();
  });
});
