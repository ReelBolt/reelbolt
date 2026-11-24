'use client';

import React from 'react';
import Link from 'next/link';
import { Button } from '@mantine/core';
import { IconExternalLink } from '@tabler/icons-react';
import { useT } from '@/i18n';

/**
 * S11: the panels that read a footage analysis (Transcript, Subtitles, Takes, Remove silence, Cut > boring shots)
 * explain WHY they are empty on a timeline that was not made from an analysed run; this is the next step — the
 * project's workflows, where a Watch footage run is started and a timeline is made from its result.
 */
export function AnalysisHelpLink({ projectId }: { projectId: string | null }) {
  const t = useT('editor.panels.analysisHelpLink');
  if (!projectId) return null;
  return (
    <Button component={Link} href={`/projects/${projectId}/workflows`} size="compact-xs" fz="xs" variant="light" leftSection={<IconExternalLink size={12} />} data-testid="analysis-open-workflows">
      {t('openThisProjectsWorkflows')}
    </Button>
  );
}
