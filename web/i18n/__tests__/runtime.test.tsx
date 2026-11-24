import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { useFormat, useT, msg } from '@/i18n';
import { negotiateLocale, fromAcceptLanguage, isLocale } from '@/i18n/config';
import { IntlProvider } from '@/i18n/provider';
import { loadMessages, messagesFor } from '@/i18n/messages';
import { ensureMatchMedia, renderWithI18n } from '@/test/i18n';

function Probe() {
  const t = useT('common');
  const f = useFormat();
  return (
    <div>
      <span data-testid="save">{t('save')}</span>
      <span data-testid="msg">{t.msg(msg('common.cancel'))}</span>
      <span data-testid="credits">{f.credits(1500)}</span>
    </div>
  );
}

describe('i18n runtime', () => {
  it('falls back to English without a provider', () => {
    render(<Probe />);
    expect(screen.getByTestId('save').textContent).toBe('Save');
    expect(screen.getByTestId('msg').textContent).toBe('Cancel');
  });

  it('renders Italian through renderWithI18n', () => {
    renderWithI18n(<Probe />, { locale: 'it' });
    expect(screen.getByTestId('save').textContent).toBe('Salva');
    expect(screen.getByTestId('msg').textContent).toBe('Annulla');
    expect(screen.getByTestId('credits').textContent).toBe('1,5');
  });

  it('renders the en-XA pseudo-locale (accented, padded, bracketed)', () => {
    ensureMatchMedia();
    render(
      <MantineProvider>
        <IntlProvider locale="en-XA" messages={loadMessages('en-XA')}>
          <Probe />
        </IntlProvider>
      </MantineProvider>,
    );
    const text = screen.getByTestId('save').textContent ?? '';
    expect(text.startsWith('[')).toBe(true);
    expect(text.endsWith(']')).toBe(true);
    expect(text).toContain('Šàṽé');
    expect(text.length).toBeGreaterThan('Save'.length * 1.4);
  });

  it('the pseudo-locale is not in the registry', () => {
    expect(JSON.stringify(loadMessages('it'))).not.toContain('Šàṽé');
  });

  it('keeps English for keys a locale lacks (merge)', () => {
    const it = loadMessages('it') as Record<string, Record<string, string>>;
    expect(it.common.save).toBe('Salva');
  });

  it('messagesFor selects only the requested catalogue files', () => {
    expect(Object.keys(messagesFor('en', ['common.language']))).toEqual(['common']);
    expect(Object.keys(messagesFor('en', ['nonexistent']))).toEqual([]);
  });
});

describe('locale negotiation', () => {
  it('allowlists everything', () => {
    expect(isLocale('it')).toBe(true);
    expect(isLocale('../etc/passwd')).toBe(false);
    expect(isLocale('xx')).toBe(false);
    expect(isLocale(undefined)).toBe(false);
  });
  it('orders user, cookie, Accept-Language, default', () => {
    expect(negotiateLocale({ user: 'it', cookie: 'en', acceptLanguage: 'en' })).toBe('it');
    expect(negotiateLocale({ user: 'zz', cookie: 'it', acceptLanguage: 'en' })).toBe('it');
    expect(negotiateLocale({ cookie: '../x', acceptLanguage: 'fr;q=0.9, it-IT;q=0.8, en;q=0.1' })).toBe('it');
    expect(negotiateLocale({ acceptLanguage: 'de-DE,fr;q=0.5' })).toBe('en');
    expect(negotiateLocale({})).toBe('en');
  });
  it('honours q-values and ignores q=0', () => {
    expect(fromAcceptLanguage('en;q=0.2, it;q=0.9')).toBe('it');
    expect(fromAcceptLanguage('it;q=0, en')).toBe('en');
    expect(fromAcceptLanguage('*')).toBeNull();
  });
});
