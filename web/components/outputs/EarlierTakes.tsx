'use client';

import { useState } from 'react';
import { ActionIcon, Badge, Button, Group, Stack, Text, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconDownload, IconPin, IconPinnedOff, IconPlayerPlay, IconTrash } from '@tabler/icons-react';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { deleteOutputTake, getOutputDownloadUrl, pruneOutputTakes, setOutputTakePinned, type OutputVideo } from '@/lib/api/outputs';
import { plainErrorMessage } from '@/lib/api/network-error';
import { timeAgo } from '@/lib/files/model';
import { isInProgress, type TakeGroup } from '@/lib/outputs/takes';
import { useT, type T } from '@/i18n';
import { useDownloadGuard } from '@/lib/outputs/output-check';

function takeLabel(o: OutputVideo, t: T): string {
  const run = o.runNumber ? t('run', { number: o.runNumber }) : t('earlierRun');
  return o.isIntermediate ? t('replaced', { run }) : run;
}

/**
 * The earlier takes of one video, collapsed under "N earlier takes": each still playable and
 * downloadable, pinnable, and deletable; plus "Delete earlier takes", which keeps the latest take and
 * every pinned one. Every delete asks first.
 */
export function EarlierTakes({
  projectId, group, onPlay, onChanged,
}: {
  projectId: string;
  group: TakeGroup;
  onPlay: (o: OutputVideo) => void;
  onChanged: () => void | Promise<unknown>;
}) {
  const t = useT('outputs.takes');
  const guardDownload = useDownloadGuard();
  const [open, setOpen] = useState(false);
  const [confirm, setConfirm] = useState<{ kind: 'one'; take: OutputVideo } | { kind: 'all' } | null>(null);
  const [busy, setBusy] = useState(false);
  const count = group.earlier.length;
  if (count === 0) return null;
  const deletable = group.earlier.filter((o) => !o.isPinned && !isInProgress(o)).length;

  const run = async (action: () => Promise<unknown>, done: string) => {
    setBusy(true);
    try {
      await action();
      notifications.show({ message: done, color: 'green' });
      await onChanged();
    } catch (e) {
      notifications.show({ title: t('failed'), message: plainErrorMessage(e), color: 'red' });
    } finally {
      setBusy(false);
      setConfirm(null);
    }
  };

  return (
    <Stack gap="xs">
      <Group gap="xs" justify="space-between">
        <Button size="compact-sm" variant="subtle" onClick={() => setOpen((v) => !v)} aria-expanded={open}>
          {t('count', { count })}
        </Button>
        {deletable > 0 && (
          <Button size="compact-sm" variant="subtle" color="red" leftSection={<IconTrash size={14} />} onClick={() => setConfirm({ kind: 'all' })}>
            {t('deleteEarlier')}
          </Button>
        )}
      </Group>
      {open && (
        <Stack gap={6} role="list" aria-label={t('listLabel')}>
          {group.earlier.map((o) => (
            <Group key={o.stepResultId} justify="space-between" wrap="nowrap" role="listitem">
              <div style={{ minWidth: 0 }}>
                <Text size="sm" lineClamp={1}>{takeLabel(o, t)}</Text>
                <Group gap={6}>
                  <Text size="xs" c="dimmed">{timeAgo(o.producedAt)}</Text>
                  {isInProgress(o) && <Badge size="xs" variant="light" color="blue">{t('stillGoing')}</Badge>}
                  {o.isIntermediate && <Badge size="xs" variant="light" color="gray">{t('inProgressTake')}</Badge>}
                  {o.isPinned && <Badge size="xs" variant="light">{t('pinned')}</Badge>}
                </Group>
              </div>
              <Group gap={4} wrap="nowrap">
                <Tooltip label={t('play')}><ActionIcon variant="subtle" aria-label={t('playLabel', { label: takeLabel(o, t) })} onClick={() => onPlay(o)}><IconPlayerPlay size={16} /></ActionIcon></Tooltip>
                <Tooltip label={t('download')}>
                  <ActionIcon variant="subtle" component="a" href={getOutputDownloadUrl(projectId, o.stepResultId, o.fileName)} onClick={guardDownload(getOutputDownloadUrl(projectId, o.stepResultId, o.fileName), o.fileName)} download={o.fileName} aria-label={t('downloadLabel', { label: takeLabel(o, t) })}>
                    <IconDownload size={16} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={o.isPinned ? t('unpin') : t('pinTip')}>
                  <ActionIcon
                    variant="subtle" disabled={busy} aria-label={o.isPinned ? t('unpinLabel', { label: takeLabel(o, t) }) : t('pinLabel', { label: takeLabel(o, t) })}
                    onClick={() => run(() => setOutputTakePinned(projectId, o.stepResultId, !o.isPinned), o.isPinned ? t('unpinned') : t('pinnedDone'))}
                  >
                    {o.isPinned ? <IconPinnedOff size={16} /> : <IconPin size={16} />}
                  </ActionIcon>
                </Tooltip>
                {!o.isPinned && !isInProgress(o) && (
                  <Tooltip label={t('deleteTip')}>
                    <ActionIcon variant="subtle" color="red" disabled={busy} aria-label={t('deleteLabel', { label: takeLabel(o, t) })} onClick={() => setConfirm({ kind: 'one', take: o })}>
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
            </Group>
          ))}
        </Stack>
      )}
      <ConfirmModal
        opened={confirm !== null}
        onClose={() => setConfirm(null)}
        loading={busy}
        title={confirm?.kind === 'all' ? t('deleteAllTitle') : t('deleteOneTitle')}
        message={confirm?.kind === 'all'
          ? t('deleteAllBody', { count: deletable })
          : t('deleteOneBody')}
        onConfirm={() => {
          if (confirm?.kind === 'all') void run(() => pruneOutputTakes(projectId, group.key), t('deletedAll'));
          else if (confirm?.kind === 'one') void run(() => deleteOutputTake(projectId, confirm.take.stepResultId), t('deletedOne'));
        }}
      />
    </Stack>
  );
}
