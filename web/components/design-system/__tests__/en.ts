import { createTranslator } from 'use-intl/core';
import type { Msg, MsgValue } from '@/i18n/msg';
import { isMsg } from '@/i18n/msg';
import { loadMessages } from '@/i18n/messages';

const translate = createTranslator({ locale: 'en', messages: loadMessages('en') as never }) as unknown as (
  key: string,
  values?: Record<string, string | number | boolean | Date | null | undefined>,
) => string;

function resolveValue(v: MsgValue): string | number | boolean | Date | null | undefined {
  return isMsg(v) ? en(v) : v;
}

/** The English text of a copy `Msg`, so a test can assert on what the user reads. */
export function en(m: Msg): string {
  const values = m.values
    ? Object.fromEntries(Object.entries(m.values).map(([k, v]) => [k, resolveValue(v)]))
    : undefined;
  return translate(m.$msg, values);
}
