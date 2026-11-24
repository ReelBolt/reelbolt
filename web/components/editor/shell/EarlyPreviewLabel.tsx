'use client';

import React from 'react';
import { Badge, Popover, Text, UnstyledButton } from '@mantine/core';
import { useT } from '@/i18n';

/** What the label says when opened. Plain words; it is the same on every editor surface. */
export const EARLY_PREVIEW_SENTENCE = 'The editor is still being finished. Your work is saved, but some tools may change.';

/**
 * The "Early preview" label every editor surface shows (product decisions D2+ and D12: the whole
 * editor carries it until it is lifted after launch). A neutral badge — it is a status, not a
 * primary action, so it stays out of the violet reserved for interaction — inside a button whose
 * tap target is 44px tall, so a click, a tap or Enter opens the explanation; there is no
 * hover-only path. On a phone header it sits in the title area, never in a control slot.
 */
export function EarlyPreviewLabel() {
  const t = useT('editor.shell.earlyPreviewLabel');
  return (
    <Popover position="bottom-start" width={260} withArrow shadow="md">
      <Popover.Target>
        <UnstyledButton
          data-testid="early-preview-label"
          style={{ display: 'inline-flex', alignItems: 'center', minHeight: 44, flexShrink: 1, minWidth: 0 }}
        >
          <Badge component="span" variant="default" size="md" fz="xs" fw={600} tt="none" radius="sm" style={{ cursor: 'inherit', maxWidth: '100%' }}>
            {t('earlyPreview')}
          </Badge>
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown>
        <Text size="sm">{t('sentence')}</Text>
      </Popover.Dropdown>
    </Popover>
  );
}
