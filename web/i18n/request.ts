import { cookies, headers } from 'next/headers';
import { getRequestConfig } from 'next-intl/server';
import { LOCALE_COOKIE, negotiateLocale } from './config';
import { parseUserCookie } from '@/lib/auth/user-cookie';
import { messagesFor } from './messages';

/**
 * Which language this request renders in (no locale routing in the dashboard):
 * saved user locale (`reelbolt_user` cookie `locale`) -> `rb_locale` cookie -> Accept-Language -> en.
 * `negotiateLocale` allowlists every candidate; nothing here builds a path from the value.
 */
export async function resolveRequestLocale(): Promise<string> {
  const [store, hdrs] = await Promise.all([cookies(), headers()]);
  return negotiateLocale({
    user: parseUserCookie(store.get('reelbolt_user')?.value)?.locale,
    cookie: store.get(LOCALE_COOKIE)?.value,
    acceptLanguage: hdrs.get('accept-language'),
  });
}

export default getRequestConfig(async () => {
  const locale = await resolveRequestLocale();
  const dev = process.env.NODE_ENV !== 'production';
  return {
    locale,
    messages: messagesFor(locale),
    timeZone: 'UTC',
    // Same behaviour as the client provider: a key missing from every catalogue renders empty in
    // production (never the raw `ns.key`), and shows a visible marker in development.
    onError: (error) => {
      if (dev) console.warn(`[i18n] ${error.message}`);
    },
    getMessageFallback: ({ namespace, key }) => (dev ? `⟦${[namespace, key].filter(Boolean).join('.')}⟧` : ''),
  };
});
