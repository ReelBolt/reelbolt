import { NextIntlClientProvider } from 'next-intl';
import { getMessages } from 'next-intl/server';

/** Namespaces the site chrome (header, footer, banners, language switcher, CTA buttons) reads in the browser. */
export const CHROME_NAMESPACES = ['layout', 'consent', 'promo'] as const;

type Messages = Record<string, unknown>;

/** Only the namespaces a client tree needs, so a page does not ship the whole catalogue to the browser. */
export function pickNamespaces(all: Messages, namespaces: readonly string[]): Messages {
  const out: Messages = {};
  for (const ns of namespaces) if (ns in all) out[ns] = all[ns];
  return out;
}

/**
 * Wraps a page body in a client provider that carries the chrome namespaces plus `extra` (the namespaces
 * that page's client components read with `useT`). The root shell provides the chrome only.
 */
export async function PageMessages({
  locale,
  extra = [],
  children,
}: {
  locale: string;
  extra?: readonly string[];
  children: React.ReactNode;
}) {
  const all = (await getMessages()) as Messages;
  return (
    <NextIntlClientProvider locale={locale} messages={pickNamespaces(all, [...CHROME_NAMESPACES, ...extra])}>
      {children}
    </NextIntlClientProvider>
  );
}
