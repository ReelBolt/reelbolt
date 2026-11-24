'use client';

import React from 'react';
import { Badge, Modal, SimpleGrid, Stack, Text, ThemeIcon, UnstyledButton } from '@mantine/core';
import { useIsPhoneClass } from '@/lib/editing/use-device-class';
import {
  IconArrowsMaximize,
  IconBackground,
  IconEraser,
  IconPaint,
  IconPhotoEdit,
  IconPhotoPlus,
  IconScissors,
  IconShape,
  IconSun,
  IconWand,
  IconZoomScan,
} from '@tabler/icons-react';
import { aiToolDescriptionMsg, aiToolGroupTitleMsg, aiToolTitleMsg } from '@/lib/editor/labels-msg';
import { AI_TOOL_GROUPS, availabilityOperation, type AiToolId } from '@/lib/editor/ai-tools';
import type { EditImageOperation } from '@/lib/types/image-document';
import { useT } from '@/i18n';

/** One icon per tool; the words come from `lib/editor/ai-tools.ts`. */
export const AI_TOOL_ICONS: Record<AiToolId, React.ComponentType<{ size?: number | string }>> = {
  subject: IconScissors,
  removeBackground: IconBackground,
  replaceBackground: IconPhotoEdit,
  erase: IconEraser,
  fill: IconPaint,
  instruct: IconWand,
  expand: IconArrowsMaximize,
  upscale: IconZoomScan,
  relight: IconSun,
  generate: IconPhotoPlus,
  designGraphic: IconShape,
};

export interface AiToolPickerProps {
  opened: boolean;
  onClose(): void;
  current: AiToolId | null;
  onPick(tool: AiToolId): void;
  /** Tools the server said this workspace cannot run, with its reason. */
  unavailable: Partial<Record<EditImageOperation, string>>;
  /** Why "Design a graphic" cannot run here, or null. */
  graphicUnavailable?: string | null;
}

/**
 * The list of AI tools, each with an icon and one plain sentence, so the AI tab can show a single
 * "Choose an AI tool" button instead of every tool's controls at once. On a phone it fills the
 * screen; elsewhere it is a dialog. Picking a tool closes it.
 */
export function AiToolPicker({ opened, onClose, current, onPick, unavailable, graphicUnavailable = null }: AiToolPickerProps) {
  const t = useT('editor-thumbnail-b.aiPicker');
  const isPhone = useIsPhoneClass();

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('title')}
      size="xl"
      radius="lg"
      centered={!isPhone}
      fullScreen={!!isPhone}
      closeButtonProps={{ size: isPhone ? 44 : undefined, 'aria-label': t('close') }}
    >
      <Stack gap="lg">
        <Text size="sm" c="dimmed">
          {t('intro')}
        </Text>
        {AI_TOOL_GROUPS.map((group, groupIndex) => (
          <Stack key={group.title} gap="xs" role="group" aria-label={t.msg(aiToolGroupTitleMsg(groupIndex))}>
            <Text size="xs" fw={700} tt="uppercase" c="dimmed" style={{ letterSpacing: 0.6 }}>
              {t.msg(aiToolGroupTitleMsg(groupIndex))}
            </Text>
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
              {group.tools.map((id) => {
                const Icon = AI_TOOL_ICONS[id];
                const probed = availabilityOperation(id);
                const reason = probed ? unavailable[probed] : id === 'designGraphic' ? (graphicUnavailable ?? undefined) : undefined;
                const active = current === id;
                return (
                  <UnstyledButton
                    key={id}
                    data-testid={`ai-tool-option-${id}`}
                    aria-label={t.msg(aiToolTitleMsg(id))}
                    aria-describedby={`ai-tool-desc-${id}`}
                    aria-pressed={active}
                    onClick={() => onPick(id)}
                    style={{
                      display: 'flex',
                      gap: 12,
                      alignItems: 'flex-start',
                      padding: 12,
                      minHeight: 44,
                      borderRadius: 10,
                      border: `1px solid ${active ? 'var(--mantine-color-blue-5)' : 'var(--mantine-color-default-border)'}`,
                      background: active ? 'rgba(77,171,247,0.10)' : 'rgba(255,255,255,0.02)',
                    }}
                  >
                    <ThemeIcon size={36} radius="md" variant="light" color={reason ? 'gray' : 'blue'}>
                      <Icon size={20} />
                    </ThemeIcon>
                    <div style={{ minWidth: 0 }}>
                      <Text size="sm" fw={600}>
                        {t.msg(aiToolTitleMsg(id))}
                      </Text>
                      <Text size="xs" c="dimmed" id={`ai-tool-desc-${id}`}>
                        {t.msg(aiToolDescriptionMsg(id))}
                      </Text>
                      {reason && (
                        <Badge mt={6} size="xs" fz="xs" variant="light" color="gray">
                          {t('notSetUp')}
                        </Badge>
                      )}
                    </div>
                  </UnstyledButton>
                );
              })}
            </SimpleGrid>
          </Stack>
        ))}
      </Stack>
    </Modal>
  );
}
