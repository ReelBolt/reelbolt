'use client';

import { useCallback, useState } from 'react';
import { SegmentedControl } from '@mantine/core';
import { useT } from '@/i18n';
import {
  resolveWorkflowView,
  withWorkflowView,
  workflowViewStorageKey,
  type WorkflowView,
} from '@/lib/workflow-apps/view-mode';

function readRemembered(userKey: string | null | undefined): string | null {
  if (typeof window === 'undefined') return null;
  try { return window.localStorage.getItem(workflowViewStorageKey(userKey)); } catch { return null; }
}

/**
 * The workflow page's view: URL first (`?view=`), then what this user chose last time, then the app
 * (spec 4.3, 4.6). `search` is the current query string (from `useSearchParams`). A choice made here is
 * remembered, shown at once and written to the URL with `history.replaceState`, so the toggle adds no
 * history entries; it only holds while the URL it was made against is the current one.
 */
export function useWorkflowView(
  userKey: string | null | undefined,
  search: string,
): [WorkflowView, (v: WorkflowView) => void] {
  const [choice, setChoice] = useState<{ search: string; view: WorkflowView } | null>(null);
  const view = choice && choice.search === search ? choice.view : resolveWorkflowView(search, readRemembered(userKey), 'app');

  const setView = useCallback((next: WorkflowView) => {
    setChoice({ search, view: next });
    try { window.localStorage.setItem(workflowViewStorageKey(userKey), next); } catch { /* storage blocked */ }
    const nextSearch = withWorkflowView(window.location.search, next);
    window.history.replaceState(window.history.state, '', `${window.location.pathname}${nextSearch}${window.location.hash}`);
  }, [userKey, search]);

  return [view, setView];
}

/** "App | Advanced". Mantine's SegmentedControl is a `role="radiogroup"` of radio inputs (arrow keys move). */
export function AppViewToggle({ view, onChange }: { view: WorkflowView; onChange: (v: WorkflowView) => void }) {
  const t = useT('apps.viewToggle');
  return (
    <SegmentedControl
      aria-label={t('label')}
      data={[
        { value: 'app', label: t('app') },
        { value: 'advanced', label: t('advanced') },
      ]}
      value={view}
      onChange={(v) => onChange(v === 'app' ? 'app' : 'advanced')}
    />
  );
}
