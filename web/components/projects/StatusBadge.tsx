'use client';

import { Badge } from '@mantine/core';
import { STATUS_COLORS } from '@/lib/utils/constants';
import { isNeedsAttention } from '@/lib/utils/run-status';
import type { NeedsAttention } from '@/lib/types/workflow';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';

const KNOWN: Record<string, Msg> = {
  Queued: msg('projects.status.known.Queued'), Running: msg('projects.status.known.Running'), Passed: msg('projects.status.known.Passed'),
  Failed: msg('projects.status.known.Failed'), Pending: msg('projects.status.known.Pending'), Processing: msg('projects.status.known.Processing'),
  Completed: msg('projects.status.known.Completed'), Done: msg('projects.status.known.Done'), Skipped: msg('projects.status.known.Skipped'),
  NotIndexed: msg('projects.status.known.NotIndexed'), Indexed: msg('projects.status.known.Indexed'),
};

export function StatusBadge({ status, needsAttention }: { status: string; needsAttention?: NeedsAttention | null }) {
  const t = useT('projects.status');
  const attention = isNeedsAttention({ status, needsAttention });
  return (
    <Badge color={attention ? 'yellow' : STATUS_COLORS[status] || 'gray'} variant={attention ? 'filled' : 'light'} autoContrast={attention} size="sm">
      {attention ? t('needsAttention') : Object.hasOwn(KNOWN, status) ? t.msg(KNOWN[status]) : status}
    </Badge>
  );
}
