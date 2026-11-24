import type { Locale } from './config';

export type DateStyle = 'short' | 'medium' | 'long' | 'time' | 'datetime';

const DATE_OPTIONS: Record<DateStyle, Intl.DateTimeFormatOptions> = {
  short: { day: 'numeric', month: 'short' },
  medium: { dateStyle: 'medium' },
  long: { dateStyle: 'long' },
  time: { timeStyle: 'short' },
  datetime: { dateStyle: 'medium', timeStyle: 'short' },
};

export interface Format {
  number(n: number, options?: Intl.NumberFormatOptions): string;
  date(d: Date | string | number, style?: DateStyle): string;
  /** Credits are stored as millicredits; shown to at most two decimals. */
  credits(millis: number): string;
  currency(amount: number, currency: string): string;
  relative(d: Date | string | number, now?: Date | number): string;
}

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 31_536_000_000],
  ['month', 2_592_000_000],
  ['day', 86_400_000],
  ['hour', 3_600_000],
  ['minute', 60_000],
  ['second', 1000],
];

/** Pure (no React): the same code serves server components, client components and tests. UTC, so server and browser agree. */
export function makeFormat(locale: Locale): Format {
  const asDate = (d: Date | string | number) => (d instanceof Date ? d : new Date(d));
  return {
    number: (n, options) => new Intl.NumberFormat(locale, options).format(n),
    date: (d, style = 'medium') =>
      new Intl.DateTimeFormat(locale, { timeZone: 'UTC', ...DATE_OPTIONS[style] }).format(asDate(d)),
    credits: (millis) =>
      new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(millis / 1000),
    currency: (amount, currency) =>
      new Intl.NumberFormat(locale, { style: 'currency', currency }).format(amount),
    relative: (d, now = Date.now()) => {
      const diff = asDate(d).getTime() - (typeof now === 'number' ? now : now.getTime());
      const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
      for (const [unit, ms] of UNITS) {
        if (Math.abs(diff) >= ms || unit === 'second') return rtf.format(Math.round(diff / ms), unit);
      }
      return rtf.format(0, 'second');
    },
  };
}
