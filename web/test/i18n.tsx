import { render, type RenderOptions, type RenderResult } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MantineProvider } from '@mantine/core';
import { IntlProvider } from '@/i18n/provider';
import { loadMessages } from '@/i18n/messages';

/** jsdom has no matchMedia, which MantineProvider calls on mount. */
export function ensureMatchMedia(): void {
  if (typeof window !== 'undefined' && !window.matchMedia) {
    window.matchMedia = ((query: string) => ({
      matches: false, media: query, onchange: null,
      addEventListener: () => undefined, removeEventListener: () => undefined,
      addListener: () => undefined, removeListener: () => undefined, dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
  }
}

/**
 * Renders `ui` inside the real i18n provider for `locale` (default `it`, since plain `render()` already
 * gets English from the global fallback in vitest.setup.ts). Mantine is included because most components need it.
 */
export function renderWithI18n(
  ui: ReactElement,
  { locale = 'it', ...options }: { locale?: string } & Omit<RenderOptions, 'wrapper'> = {},
): RenderResult {
  ensureMatchMedia();
  return render(ui, {
    ...options,
    wrapper: ({ children }) => (
      <MantineProvider>
        <IntlProvider locale={locale} messages={loadMessages(locale)}>
          {children}
        </IntlProvider>
      </MantineProvider>
    ),
  });
}
