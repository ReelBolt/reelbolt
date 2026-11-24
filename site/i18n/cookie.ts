/** The functional preference cookie that remembers the visitor's language (read only at `/`). */
export const LOCALE_COOKIE = 'rb_locale';
const ONE_YEAR_SECONDS = 365 * 24 * 60 * 60;

/** The `document.cookie` assignment for choosing `locale`: site-wide, same-site, one year. */
export function localeCookie(locale: string, secure = false): string {
  return `${LOCALE_COOKIE}=${encodeURIComponent(locale)}; Path=/; Max-Age=${ONE_YEAR_SECONDS}; SameSite=Lax${secure ? '; Secure' : ''}`;
}
