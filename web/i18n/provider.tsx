'use client';

import { NextIntlClientProvider } from 'next-intl';
import { useMemo, useState, type ReactNode } from 'react';

const dev = process.env.NODE_ENV !== 'production';

// Module scope: inline callbacks would change identity on every render and with it every `t`.
function onError(error: { message: string }): void {
  if (dev) console.warn(`[i18n] ${error.message}`);
}
function getMessageFallback({ namespace, key }: { namespace?: string; key: string }): string {
  return dev ? `⟦${[namespace, key].filter(Boolean).join('.')}⟧` : '';
}

/**
 * router.refresh() sends a new `messages` object with identical content. Keep the previous reference
 * then, so `useT` consumers keep a stable `t`; a real content change still updates.
 */
function useStableMessages(messages: Record<string, unknown>): Record<string, unknown> {
  const signature = useMemo(() => JSON.stringify(messages), [messages]);
  const [kept, setKept] = useState({ signature, messages });
  if (kept.signature !== signature) {
    setKept({ signature, messages });
    return messages;
  }
  return kept.messages;
}

/**
 * The one provider. Missing keys never render a raw key in production: English is merged in by the
 * loader, and a key missing even from English renders nothing (dev: ⟦ns.key⟧ and a console warning).
 */
export function IntlProvider({
  locale,
  messages,
  children,
}: {
  locale: string;
  messages: Record<string, unknown>;
  children: ReactNode;
}) {
  const stable = useStableMessages(messages);
  return (
    <NextIntlClientProvider
      locale={locale}
      messages={stable as never}
      timeZone="UTC"
      onError={onError}
      getMessageFallback={getMessageFallback}
    >
      {children}
    </NextIntlClientProvider>
  );
}
