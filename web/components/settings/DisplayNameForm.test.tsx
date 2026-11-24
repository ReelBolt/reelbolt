// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';

const H = vi.hoisted(() => ({ update: vi.fn(), notify: vi.fn() }));
vi.mock('@/lib/api/auth-settings', () => ({
  AUTH_SETTINGS_PATH: '/api/v1/auth/settings',
  updateAuthSettings: (...a: unknown[]) => H.update(...a),
}));
vi.mock('@/lib/hooks/use-auth', () => ({ notifyAuthChange: () => H.notify() }));
vi.mock('swr', () => ({ mutate: vi.fn().mockResolvedValue(undefined) }));

import { DisplayNameForm } from './DisplayNameForm';

vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }));
afterEach(() => { cleanup(); H.update.mockReset(); H.notify.mockReset(); });

const renderForm = () =>
  render(<MantineProvider><Notifications /><DisplayNameForm currentName="Ada" /></MantineProvider>);

describe('DisplayNameForm', () => {
  it('keeps Save disabled until the name changes, then saves and refreshes the session', async () => {
    H.update.mockResolvedValue({ mcpAdminToolsEnabled: false, displayName: 'Ada L' });
    renderForm();
    const save = screen.getByRole('button', { name: 'Save name' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Ada L' } });
    expect(save.disabled).toBe(false);
    fireEvent.click(save);
    await waitFor(() => expect(H.update).toHaveBeenCalledWith({ displayName: 'Ada L' }));
    await waitFor(() => expect(H.notify).toHaveBeenCalled());
  });

  it('shows the server refusal inline and does not refresh the session', async () => {
    H.update.mockRejectedValue(new Error('display name must be 1 to 100 characters'));
    renderForm();
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save name' }));
    expect(await screen.findByText('display name must be 1 to 100 characters')).toBeTruthy();
    expect(H.notify).not.toHaveBeenCalled();
  });
});
