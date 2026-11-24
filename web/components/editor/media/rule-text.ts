import { describeRuleMsgs, listText, poolEverythingMsg } from '@/lib/editing/labels-msg';
import { isEmptySmartRule } from '@/lib/editing/media/pool-browse';
import type { MediaSmartFilter } from '@/lib/editing/media/types';
import type { T } from '@/i18n';

/** `describeRule` in the person's language: the rule as one sentence, capitalised. */
export function ruleText(t: Pick<T, 'msg'>, locale: string, filter: MediaSmartFilter): string {
  if (isEmptySmartRule(filter)) return t.msg(poolEverythingMsg());
  const text = describeRuleMsgs({ ...filter, kinds: filter.kinds ?? undefined, keywords: filter.keywords ?? undefined }, (tags) => listText(tags, locale)).map((m) => t.msg(m)).join(', ');
  return text.charAt(0).toLocaleUpperCase(locale) + text.slice(1);
}
