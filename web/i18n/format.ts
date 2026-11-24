/** Locale-aware number/date/credit/currency/relative formatting, all through Intl with the active locale. */

export type DateStyle = 'short' | 'medium' | 'long' | 'time' | 'dateTime';

const DATE_OPTIONS: Record<DateStyle, Intl.DateTimeFormatOptions> = {
  short: { day: 'numeric', month: 'short', year: 'numeric' },
  medium: { day: 'numeric', month: 'long', year: 'numeric' },
  long: { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' },
  time: { hour: '2-digit', minute: '2-digit' },
  dateTime: { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' },
};

export interface Format {
  readonly locale: string;
  number(value: number, options?: Intl.NumberFormatOptions): string;
  percent(fraction: number, options?: Intl.NumberFormatOptions): string;
  date(value: Date | number | string, style?: DateStyle): string;
  /** Millicredits (1,000 = 1 credit, decision D8) as a credits figure, up to 3 decimals. */
  credits(millis: number): string;
  currency(amount: number, currency: string, options?: Intl.NumberFormatOptions): string;
  relative(value: Date | number | string, now?: Date | number): string;
  list(items: string[], type?: 'conjunction' | 'disjunction'): string;
}

const finite = (n: number): number => (Number.isFinite(n) ? n : 0);
const toDate = (v: Date | number | string): Date => (v instanceof Date ? v : new Date(v));

// Intl constructors are the expensive part; one per (locale, options) is enough.
const cache = new Map<string, Intl.NumberFormat | Intl.DateTimeFormat>();
function numberFormat(locale: string, o?: Intl.NumberFormatOptions): Intl.NumberFormat {
  const key = `n|${locale}|${JSON.stringify(o ?? {})}`;
  let f = cache.get(key) as Intl.NumberFormat | undefined;
  if (!f) cache.set(key, (f = new Intl.NumberFormat(locale, o)));
  return f;
}
function dateFormat(locale: string, o: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = `d|${locale}|${JSON.stringify(o)}`;
  let f = cache.get(key) as Intl.DateTimeFormat | undefined;
  if (!f) cache.set(key, (f = new Intl.DateTimeFormat(locale, o)));
  return f;
}

const RELATIVE_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
  ['second', 1],
];

export function createFormat(locale: string): Format {
  // The pseudo-locale formats like English.
  const intlLocale = locale === 'en-XA' ? 'en' : locale;
  return {
    locale,
    number: (value, options) => numberFormat(intlLocale, options).format(finite(value)),
    percent: (fraction, options) => numberFormat(intlLocale, { style: 'percent', maximumFractionDigits: 1, ...options }).format(finite(fraction)),
    date: (value, style = 'short') => {
      const d = toDate(value);
      return Number.isNaN(d.getTime()) ? '' : dateFormat(intlLocale, DATE_OPTIONS[style]).format(d);
    },
    credits: (millis) => numberFormat(intlLocale, { maximumFractionDigits: 3 }).format(finite(millis) / 1000),
    currency: (amount, currency, options) => numberFormat(intlLocale, { style: 'currency', currency, ...options }).format(finite(amount)),
    relative: (value, now = Date.now()) => {
      const d = toDate(value);
      if (Number.isNaN(d.getTime())) return '';
      const diffSec = Math.round((d.getTime() - toDate(now).getTime()) / 1000);
      const abs = Math.abs(diffSec);
      const [unit, secs] = RELATIVE_UNITS.find(([, s]) => abs >= s) ?? ['second', 1];
      return new Intl.RelativeTimeFormat(intlLocale, { numeric: 'auto' }).format(Math.round(diffSec / secs), unit);
    },
    list: (items, type = 'conjunction') => new Intl.ListFormat(intlLocale, { style: 'long', type }).format(items),
  };
}
