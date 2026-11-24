// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import type { PublishPreview } from '@/lib/types/marketplace';

vi.mock('@/lib/hooks/use-auth', () => ({ useAuth: () => ({ user: { displayName: 'Ada Lovelace' } }) }));
vi.mock('@/lib/hooks/use-projects', () => ({ useProjects: () => ({ data: [{ id: 'p1', name: 'Proj' }] }) }));
vi.mock('@/lib/hooks/use-workflows', () => ({ useWorkflows: () => ({ data: [{ id: 'w1', name: 'Teaser app' }] }) }));
vi.mock('@/lib/hooks/use-outputs', () => ({
  useOutputVideos: () => ({ data: [{ stepResultId: 'sr1', fileName: 'a.mp4', workflowName: 'Teaser app', producedAt: '2026-11-01T10:00:00Z', isFinal: true }, { stepResultId: 'sr2', fileName: 'b.mp4', producedAt: '2026-11-01T10:00:00Z', isFinal: false }] }),
}));
const state = vi.hoisted(() => ({ check: undefined as unknown, consent: undefined as unknown, refresh: vi.fn() }));
vi.mock('@/lib/hooks/use-marketplace-owner', () => ({
  usePublishPreview: () => ({ data: state.check, error: undefined, isLoading: false }),
  useMarketplaceConsent: () => ({ data: state.consent, mutate: state.refresh }),
}));
const api = vi.hoisted(() => ({ publish: vi.fn(), update: vi.fn() }));
vi.mock('@/lib/api/marketplace', async (orig) => ({ ...(await orig<typeof import('@/lib/api/marketplace')>()), publishRecipe: api.publish, updateRecipe: api.update }));

import { PublishDialog } from './PublishDialog';

const ok = (over: Partial<PublishPreview> = {}): PublishPreview => ({
  publishable: true, refusals: [], removed: [{ stepOrder: 2, field: 'voiceId', what: 'Your voice' }],
  slots: [{ kind: 'voice', stepOrder: 2, label: 'Narrator' }], steps: [], prompts: [{ stepOrder: 1, field: 'prompt', text: 'Say <b>hi</b>' }], costClass: 'Uses credits', ...over,
});

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  state.check = ok();
  state.consent = { version: 'v1', text: 'You grant a licence.', licenceSummary: 'Summary.' };
  state.refresh.mockReset().mockResolvedValue(undefined);
  api.publish.mockReset();
  api.update.mockReset();
});
afterEach(cleanup);

const mount = (props: Partial<React.ComponentProps<typeof PublishDialog>> = {}) =>
  render(<MantineProvider><PublishDialog projectId="p1" workflowId="w1" onClose={vi.fn()} {...props} /></MantineProvider>);

async function fill() {
  const d = await screen.findByRole('dialog');
  fireEvent.change(within(d).getByLabelText(/Preview video/), { target: { value: 'step_result:sr1' } });
  fireEvent.change(within(d).getByLabelText(/^Name/), { target: { value: 'Teaser' } });
  return d;
}

describe('PublishDialog', () => {
  it('shows what is removed, what the copier must choose and the prompts as plain text', async () => {
    mount();
    const d = await screen.findByRole('dialog');
    expect(within(d).getByText('Step 2: your voice (the person copying chooses their own)')).toBeTruthy();
    expect(within(d).getByText('A voice: Narrator')).toBeTruthy();
    expect(d.querySelector('pre')?.textContent).toBe('Say <b>hi</b>');
    expect((within(d).getByLabelText(/Author name shown/) as HTMLInputElement).value).toBe('Ada Lovelace');
    const options = Array.from(within(d).getByLabelText(/Preview video/).querySelectorAll('option')).map((o) => o.value);
    expect(options).toEqual(['', 'step_result:sr1']);
  });

  it('offers exactly the two supported languages', async () => {
    mount();
    const d = await screen.findByRole('dialog');
    const options = Array.from(within(d).getByLabelText('Language').querySelectorAll('option')).map((o) => o.value);
    expect(options).toEqual(['en', 'it']);
  });

  it('shows refusals with the step, blocks the form and offers no submit', async () => {
    state.check = ok({ publishable: false, refusals: [{ code: 'CUSTOM_AGENT', stepOrder: 3, message: 'Uses a custom agent' }] });
    mount();
    const d = await screen.findByRole('dialog');
    expect(within(d).getByText('Step 3: This step uses an agent you made. Only built-in agents can be shared.')).toBeTruthy();
    expect(within(d).queryByLabelText(/Preview video/)).toBeNull();
    expect(within(d).getByRole('button', { name: 'Share recipe' }).hasAttribute('disabled')).toBe(true);
  });

  it('needs the consent box before it can send, then shows the status chip', async () => {
    api.publish.mockResolvedValueOnce({ id: 'r1', status: 'pending', version: 1, previewStatus: 'preparing' });
    mount();
    const d = await fill();
    const submit = within(d).getByRole('button', { name: 'Share recipe' });
    expect(submit.hasAttribute('disabled')).toBe(false);
    fireEvent.click(submit);
    expect(within(d).getByRole('alert').textContent).toContain('Tick the box to agree');
    expect(api.publish).not.toHaveBeenCalled();
    fireEvent.click(within(d).getByLabelText(/I have read this/));
    fireEvent.click(submit);
    await waitFor(() => expect(screen.getByText('Waiting for review')).toBeTruthy());
    expect(api.publish).toHaveBeenCalledWith(expect.objectContaining({
      projectId: 'p1', workflowId: 'w1', outputRef: 'step_result:sr1', name: 'Teaser', authorLabel: 'Ada Lovelace', consentAccepted: true, consentVersion: 'v1',
    }));
  });

  it('says what is still needed when pressed too early', async () => {
    mount();
    const d = await screen.findByRole('dialog');
    fireEvent.click(within(d).getByLabelText(/I have read this/));
    fireEvent.change(within(d).getByLabelText(/Author name shown/), { target: { value: '' } });
    fireEvent.click(within(d).getByRole('button', { name: 'Share recipe' }));
    expect(within(d).getByRole('alert').textContent).toBe('Still needed: a preview video, a name, an author name.');
    expect(within(d).getByRole('button', { name: 'Share recipe' }).getAttribute('aria-describedby')).toBe('pub-error');
    expect(api.publish).not.toHaveBeenCalled();
  });

  it('starts unticked again when a newer consent version arrives', async () => {
    const { rerender } = mount();
    const d = await screen.findByRole('dialog');
    fireEvent.click(within(d).getByLabelText(/I have read this/));
    expect((within(d).getByLabelText(/I have read this/) as HTMLInputElement).checked).toBe(true);
    state.consent = { version: 'v2', text: 'New text.', licenceSummary: 'S.' };
    rerender(<MantineProvider><PublishDialog projectId="p1" workflowId="w1" onClose={vi.fn()} /></MantineProvider>);
    expect((within(screen.getByRole('dialog')).getByLabelText(/I have read this/) as HTMLInputElement).checked).toBe(false);
  });

  it('re-reads the consent and asks again on CONSENT_VERSION_STALE', async () => {
    api.publish.mockRejectedValueOnce(new ApiError('stale', 409, [], { code: 'CONSENT_VERSION_STALE' }));
    mount();
    const d = await fill();
    fireEvent.click(within(d).getByLabelText(/I have read this/));
    fireEvent.click(within(d).getByRole('button', { name: 'Share recipe' }));
    await waitFor(() => expect(state.refresh).toHaveBeenCalled());
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByRole('alert').textContent).toContain('terms changed'));
    expect((within(screen.getByRole('dialog')).getByLabelText(/I have read this/) as HTMLInputElement).checked).toBe(false);
  });

  it.each([
    ['DAILY_PUBLISH_LIMIT', "today's limit"],
    ['LIVE_RECIPE_LIMIT', 'limit of live recipes'],
    ['TEXT_BLOCKED', 'not allowed'],
    ['PREVIEW_REQUIRED', 'finished video'],
    ['EMAIL_NOT_VERIFIED', 'Verify your email'],
  ])('maps %s to words', async (code, words) => {
    api.publish.mockRejectedValueOnce(new ApiError(code, 403, [], { code }));
    mount();
    const d = await fill();
    fireEvent.click(within(d).getByLabelText(/I have read this/));
    fireEvent.click(within(d).getByRole('button', { name: 'Share recipe' }));
    await waitFor(() => expect(within(screen.getByRole('dialog')).getByRole('alert').textContent).toContain(words));
  });

  it('updates an existing recipe with PUT', async () => {
    api.update.mockResolvedValueOnce({ id: 'r9', status: 'listed', version: 2, previewStatus: 'ready' });
    mount({ existing: { id: 'r9', name: 'Old', status: 'listed', version: 1, previewStatus: 'ready', copyCount: 0, runCount: 0, ratingAverage: null, ratingCount: 0 } });
    const d = await screen.findByRole('dialog');
    fireEvent.change(within(d).getByLabelText(/Preview video/), { target: { value: 'step_result:sr1' } });
    fireEvent.click(within(d).getByLabelText(/I have read this/));
    fireEvent.click(within(d).getByRole('button', { name: 'Send new version' }));
    await waitFor(() => expect(api.update).toHaveBeenCalledWith('r9', expect.objectContaining({ name: 'Old' })));
  });
});
