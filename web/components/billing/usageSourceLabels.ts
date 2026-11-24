import { msg, type Msg } from '@/i18n/msg';
import type { UsageSource } from '@/lib/types/billing';

/** Catalogue keys for the plain names of the usage sources (`groupBy=source`), as a customer would describe them. */
export const USAGE_SOURCE_MSGS: Record<UsageSource, Msg> = {
  Workflow: msg('billing.usage.source.workflow'),
  Assistant: msg('billing.usage.source.assistant'),
  Mcp: msg('billing.usage.source.mcp'),
  Editor: msg('billing.usage.source.editor'),
  TimelineAssist: msg('billing.usage.source.timelineAssist'),
  FileSummary: msg('billing.usage.source.fileSummary'),
  Indexing: msg('billing.usage.source.indexing'),
  DocsIndex: msg('billing.usage.source.docsIndex'),
  Storage: msg('billing.usage.source.storage'),
  Unattributed: msg('billing.usage.source.unattributed'),
};

/** A source's plain name; a value this build has never heard of is a newer backend and is shown by name. */
export function usageSourceLabel(source: string): Msg | string {
  return Object.prototype.hasOwnProperty.call(USAGE_SOURCE_MSGS, source)
    ? USAGE_SOURCE_MSGS[source as UsageSource]
    : source;
}
