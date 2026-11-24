'use client';

import { useState } from 'react';
import { Button } from '@mantine/core';
import { IconShare } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { PublishDialog } from './PublishDialog';

/** The one "Share as recipe" button of an app page or the builder header. The dialog mounts only while open. */
export function ShareAsRecipeButton({ projectId, workflowId }: { projectId: string; workflowId: string }) {
  const t = useT('marketplace.publish');
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button variant="default" size="sm" leftSection={<IconShare size={16} aria-hidden />} onClick={() => setOpen(true)} styles={{ root: { minHeight: 44 } }}>
        {t('button')}
      </Button>
      {open && <PublishDialog projectId={projectId} workflowId={workflowId} onClose={() => setOpen(false)} />}
    </>
  );
}
