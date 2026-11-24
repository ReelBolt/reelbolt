'use client';

import { importHeadlineMsg } from '@/lib/editing/interchange/labels-msg';
import React from 'react';
import { Button, Group, List, Modal, Stack, Text } from '@mantine/core';
import { importHeadline, matchedHowWords, type ImportSheetModel } from '@/lib/editing/interchange/handoff-ui';
import { useT } from '@/i18n';

export interface ImportResultSheetProps {
  model: ImportSheetModel | null;
  readOnly: boolean;
  onClose: () => void;
  /** Opens the relink dialog (with the candidate picker when the name was shared) for one offline clip. */
  onRelink: (markerId: string) => void;
}

/**
 * R12.3: what an import did, in words. Matched files with how they were matched; offline clips with
 * the reason, each with the relink flow the editor already has. Closing it leaves the red markers and
 * the offline banner in place.
 */
export function ImportResultSheet({ model, readOnly, onClose, onRelink }: ImportResultSheetProps) {
  const t = useT('editor-misc.importResult');
  return (
    <Modal opened={model !== null} onClose={onClose} title={t('title')} centered size="lg" data-testid="import-result">
      {model && (
        <Stack gap="sm">
          <Text size="sm" data-testid="import-headline">{t.msg(importHeadlineMsg(model.matched.length, model.offline.length))}</Text>
          {model.matched.length > 0 && (
            <Stack gap={4}>
              <Text size="sm" fw={600}>{t('matched', { count: model.matched.length })}</Text>
              <List size="sm" spacing={2} data-testid="import-matched">
                {model.matched.map((m) => (
                  <List.Item key={`${m.name}-${m.fileId ?? m.stepResultId ?? ''}`}>
                    {t('matchedItem', { name: m.name, how: matchedHowWords(m.how) })}
                  </List.Item>
                ))}
              </List>
            </Stack>
          )}
          {model.offline.length > 0 && (
            <Stack gap={6}>
              <Text size="sm" fw={600}>{t('offline', { count: model.offline.length })}</Text>
              <Text size="xs" c="dimmed">{t('offlineNote')}</Text>
              {model.offline.map((o) => (
                <Group key={o.itemId} gap="xs" wrap="nowrap" align="center" data-testid={`import-offline-${o.name}`}>
                  <Stack gap={0} style={{ flex: 1 }}>
                    <Text size="sm">{o.name}</Text>
                    <Text size="xs" c="dimmed" data-testid={`import-reason-${o.name}`}>{o.reasonWords}</Text>
                  </Stack>
                  <Button size="xs" fz="xs" mih={44} disabled={readOnly} onClick={() => onRelink(o.itemId)} data-testid={`import-relink-${o.name}`}>
                    {o.candidates.length > 0 ? t('pickFile') : t('relink')}
                  </Button>
                </Group>
              ))}
            </Stack>
          )}
          <Group justify="flex-end" mt="xs">
            <Button variant="default" mih={44} onClick={onClose} data-testid="import-close">{t('done')}</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
