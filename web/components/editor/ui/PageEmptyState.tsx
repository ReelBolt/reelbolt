'use client';

import React from 'react';
import type { WorkspaceId } from '@/lib/editing/store';
import { useT } from '@/i18n';
import { EditorEmpty, type EditorEmptyProps } from './EditorEmpty';

export interface PageEmptyStateProps {
  /** The page whose one-line purpose sentence is the title (the same catalogue the switcher tooltip uses). */
  page: WorkspaceId;
  /** What to do here (the page's own sentence). */
  hint?: React.ReactNode;
  action?: EditorEmptyProps['action'];
  icon?: EditorEmptyProps['icon'];
  testId?: string;
  compact?: boolean;
}

/**
 * T-008 (§12): a page's default/empty state. The title is the page description from the ONE catalogue
 * (`editor.shell.workspaceBar.names.<id>.description`) — the same string as the switcher tooltip — so a
 * page never grows a second description.
 */
export function PageEmptyState({ page, hint, action, icon, testId, compact = false }: PageEmptyStateProps) {
  const t = useT('editor.shell.workspaceBar');
  return <EditorEmpty icon={icon} title={t(`names.${page}.description`)} hint={hint} action={action} testId={testId} compact={compact} />;
}
