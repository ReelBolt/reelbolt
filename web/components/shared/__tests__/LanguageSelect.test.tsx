import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';

const refresh = vi.fn();
const update = vi.fn();
let signedIn = true;
const show = vi.fn();

vi.mock('next/navigation', () => ({ useRouter: () => ({ refresh }) }));
vi.mock('@/lib/api/auth-settings', () => ({ updateAuthSettings: (b: unknown) => update(b) }));
vi.mock('@/lib/auth/user-cookie', () => ({ readUserCookie: () => (signedIn ? { email: 'a@b.c' } : null) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: (a: unknown) => show(a) } }));

import { LanguageSelect } from '../LanguageSelect';

function pick(code: string) {
  fireEvent.change(screen.getByTestId('language-select'), { target: { value: code } });
}

describe('LanguageSelect', () => {
  beforeEach(() => {
    refresh.mockReset();
    update.mockReset();
    show.mockReset();
    signedIn = true;
    document.cookie = 'rb_locale=; Max-Age=0; Path=/';
  });

  it('lists the registered languages by their own name', () => {
    renderWithI18n(<LanguageSelect />, { locale: 'en' });
    const labels = Array.from(screen.getByTestId('language-select').querySelectorAll('option')).map((o) => o.textContent);
    expect(labels).toEqual(expect.arrayContaining(['English', 'Italiano']));
  });

  it('signed in: saves to the account first, then sets the cookie and refreshes', async () => {
    update.mockResolvedValue({});
    renderWithI18n(<LanguageSelect />, { locale: 'en' });
    pick('it');
    await waitFor(() => expect(refresh).toHaveBeenCalled());
    expect(update).toHaveBeenCalledWith({ locale: 'it' });
    expect(document.cookie).toContain('rb_locale=it');
  });

  it('signed in, save fails: nothing changes and the user is told', async () => {
    update.mockRejectedValue(new Error('boom'));
    renderWithI18n(<LanguageSelect />, { locale: 'en' });
    pick('it');
    await waitFor(() => expect(show).toHaveBeenCalled());
    expect(refresh).not.toHaveBeenCalled();
    expect(document.cookie).not.toContain('rb_locale=it');
  });

  it('signed out: sets the cookie and refreshes without calling the API', async () => {
    signedIn = false;
    renderWithI18n(<LanguageSelect />, { locale: 'en' });
    pick('it');
    await waitFor(() => expect(refresh).toHaveBeenCalled());
    expect(update).not.toHaveBeenCalled();
    expect(document.cookie).toContain('rb_locale=it');
  });

  it('ignores a value that is not a registered language', async () => {
    renderWithI18n(<LanguageSelect />, { locale: 'en' });
    pick('../x');
    expect(update).not.toHaveBeenCalled();
    expect(refresh).not.toHaveBeenCalled();
  });
});
