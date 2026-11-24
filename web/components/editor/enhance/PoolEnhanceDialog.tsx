'use client';

import React from 'react';
import { Button, Modal, Stack, Text } from '@mantine/core';
import { mutate as globalMutate } from 'swr';
import { mediaPoolKey } from '@/lib/editing/api/media';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { useT } from '@/i18n';
import { EnhanceRunner } from './EnhanceRunner';
import type { EnhanceJobApi } from './useEnhanceJob';

/** "Enhance..." on a media pool video: the same job, and the finished file is only added to the pool (no clip changes). */
export function PoolEnhanceDialog({ projectId, item, readOnly = false, onClose, api }: { projectId: string; item: MediaPoolItem | null; readOnly?: boolean; onClose: () => void; api?: EnhanceJobApi }) {
  const t = useT('editor-enhance.pool');
  if (!item) return null;
  const height = item.probe?.height && item.probe.width ? Math.min(item.probe.height, item.probe.width) : (item.probe?.height ?? null);
  return (
    <Modal opened onClose={onClose} title={t('title', { name: item.fileName })} centered data-testid="pool-enhance-dialog">
      <EnhanceRunner
        projectId={projectId}
        sourceMediaRef={item.mediaRef}
        readOnly={readOnly}
        phone
        api={api}
        sourceHeight={height}
        renderResult={() => <PoolDone projectId={projectId} onClose={onClose} />}
      />
    </Modal>
  );
}

function PoolDone({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const t = useT('editor-enhance.pool');
  // The new file is an ordinary project file: refresh the pool once so it shows up.
  React.useEffect(() => {
    void globalMutate(mediaPoolKey(projectId));
  }, [projectId]);
  return (
    <Stack gap={6} data-testid="pool-enhance-done">
      <Text size="sm">{t('added')}</Text>
      <Button mih={44} variant="default" onClick={onClose} data-testid="pool-enhance-close">
        {t('close')}
      </Button>
    </Stack>
  );
}
