'use client';

import { ActionIcon, Group, Modal } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconChevronLeft, IconChevronRight } from '@tabler/icons-react';
import { PostTextButton } from './PostTextDialog';
import { PublishButton } from '@/components/publishing/PublishDialog';
import { OutputPlayer, type PlayableOutput } from './OutputPlayer';
import { useT } from '@/i18n';

/** Full-screen on phones, centered dialog on desktop; prev/next walks the list it was opened from. */
export function OutputModal({
  outputs,
  index,
  onIndexChange,
  onClose,
}: {
  outputs: PlayableOutput[];
  index: number | null;
  onIndexChange: (i: number) => void;
  onClose: () => void;
}) {
  const t = useT('outputs.modal');
  const isPhone = useMediaQuery('(max-width: 48em)');
  const current = index === null ? null : outputs[index];

  return (
    <Modal
      opened={!!current}
      onClose={onClose}
      fullScreen={!!isPhone}
      size="xl"
      centered
      title={current?.projectName ?? t('render')}
      keepMounted={false}
      closeButtonProps={{ size: 44, 'aria-label': t('close') }}
    >
      {current && (
        <>
          <OutputPlayer output={current} autoPlay />
          <Group mt="md"><PostTextButton projectId={current.projectId} outputId={current.stepResultId} /><PublishButton projectId={current.projectId} outputId={current.stepResultId} outputKind="step_result" /></Group>
          {outputs.length > 1 && index !== null && (
            <Group justify="space-between" mt="md">
              <ActionIcon
                variant="default" size={44} aria-label={t('prev')}
                disabled={index <= 0} onClick={() => onIndexChange(index - 1)}
              >
                <IconChevronLeft size={20} />
              </ActionIcon>
              <ActionIcon
                variant="default" size={44} aria-label={t('next')}
                disabled={index >= outputs.length - 1} onClick={() => onIndexChange(index + 1)}
              >
                <IconChevronRight size={20} />
              </ActionIcon>
            </Group>
          )}
        </>
      )}
    </Modal>
  );
}
