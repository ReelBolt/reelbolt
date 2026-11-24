// @vitest-environment jsdom
/**
 * `/app/welcome` reads its two switches on the server per request: `WELCOME_FLOW_ENABLED` (off =
 * the page answers with the dashboard) and `ONBOARDING_SAMPLE_CLIP_URLS` (empty by default = no
 * sample-clip offer). The query can only reopen a shot list for a well-formed project id.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactElement } from 'react';
import type { WelcomeFlowProps } from '@/components/welcome/WelcomeFlow';

const mocks = vi.hoisted(() => ({
  redirect: vi.fn((to: string) => {
    throw new Error(`NEXT_REDIRECT:${to}`);
  }),
}));
vi.mock('next/navigation', () => ({ redirect: mocks.redirect }));
vi.mock('@/components/welcome/WelcomeFlow', () => ({ WelcomeFlow: () => null }));

import WelcomePage, { dynamic } from './page';

const PROJECT_ID = '2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40';
const open = async (params: Record<string, string | string[] | undefined> = {}) =>
  ((await WelcomePage({ searchParams: Promise.resolve(params) })) as ReactElement<WelcomeFlowProps>).props;

beforeEach(() => {
  mocks.redirect.mockClear();
});
afterEach(() => {
  vi.unstubAllEnvs();
});

describe('/welcome', () => {
  it('is rendered per request, because it reads the environment', () => {
    expect(dynamic).toBe('force-dynamic');
  });

  it('renders the flow by default, with no sample clips and no shot list to reopen', async () => {
    expect(await open()).toEqual({ sampleClipUrls: [], shotsForProjectId: null });
    expect(mocks.redirect).not.toHaveBeenCalled();
  });

  it('answers with the dashboard when the flow is switched off', async () => {
    vi.stubEnv('WELCOME_FLOW_ENABLED', 'false');
    await expect(open()).rejects.toThrow('NEXT_REDIRECT:/dashboard');
    expect(mocks.redirect).toHaveBeenCalledWith('/dashboard');
  });

  it('hands the operator\'s sample clips to the flow', async () => {
    vi.stubEnv('ONBOARDING_SAMPLE_CLIP_URLS', 'https://media.example.test/a.mp4,https://media.example.test/b.mp4');
    expect((await open()).sampleClipUrls).toEqual(['https://media.example.test/a.mp4', 'https://media.example.test/b.mp4']);
  });

  it('reopens a shot list only for step=shots with a well-formed project id', async () => {
    expect((await open({ step: 'shots', project: PROJECT_ID })).shotsForProjectId).toBe(PROJECT_ID);
    expect((await open({ project: PROJECT_ID })).shotsForProjectId).toBeNull();
    expect((await open({ step: 'shots' })).shotsForProjectId).toBeNull();
    expect((await open({ step: 'shots', project: '../../admin' })).shotsForProjectId).toBeNull();
    expect((await open({ step: 'shots', project: [PROJECT_ID, PROJECT_ID] })).shotsForProjectId).toBeNull();
  });
});
