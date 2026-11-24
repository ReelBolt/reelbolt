'use client';

import React, { useMemo, useState } from 'react';
import { Badge, Popover, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconChevronRight } from '@tabler/icons-react';
import { parityEntries, previewBadge, type BadgeEntry, type ParitySummary, type PreviewConditions } from '@/lib/editing/preview';
import { useT } from '@/i18n';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import { previewText } from './preview-text';

export interface RenderRequiredBadgeProps {
  /** The parity of what is on screen NOW. */
  parity: ParitySummary;
  /** The parity of the whole document: its other non-faithful clips are listed under "Elsewhere in this timeline". */
  documentParity?: ParitySummary;
  /** What the session actually showed (PA-18). Absent: a live session that drew every layer. */
  conditions?: PreviewConditions;
  /** A reason about a clip was chosen: select it and bring it into view. */
  onJumpToClip?: (itemId: string) => void;
  /** The Simple shell: nothing is shown while the picture on screen is exactly what the export will be. */
  quiet?: boolean;
}

const DRAWN_EVERYTHING: PreviewConditions = { sessionKind: 'live', sessionReason: null, missing: [], notReady: [] };
const COLORS = { faithful: 'teal', approximate: 'yellow', renderRequired: 'red', notReady: 'gray' } as const;

/**
 * The parity badge, made honest and reachable (R04.2). It reads `parityAtFrame` for what is on screen NOW
 * and the live session's state, so it is never "faithful" while a layer is missing, a clip is not playing
 * from its proxy, or the session is the simple preview or running at reduced quality. Tap or click it for
 * the reasons in words; a line about a clip jumps to that clip.
 */
export function RenderRequiredBadge({ parity, documentParity, conditions = DRAWN_EVERYTHING, onJumpToClip, quiet = false }: RenderRequiredBadgeProps) {
  const t = useT('editor.viewer.renderRequiredBadge');
  const [opened, setOpened] = useState(false);
  // A finger needs 44 px (F4); a pointer keeps the compact chip.
  const touchH = useIsTouchSurface() ? 44 : 32;
  const tr = (text: string) => previewText(text, t.msg);
  const badge = previewBadge(parity, conditions);
  const badgeLabel = tr(badge.label);
  const elsewhere: BadgeEntry[] = useMemo(() => {
    if (!documentParity) return [];
    const onScreen = new Set(parity.items.map((i) => i.itemId));
    return parityEntries(documentParity.items.filter((i) => !onScreen.has(i.itemId)));
  }, [documentParity, parity]);

  const line = (entry: BadgeEntry, index: number) =>
    entry.itemId && onJumpToClip ? (
      <UnstyledButton
        key={`${index}-${tr(entry.text)}`}
        data-testid="parity-reason"
        data-item-id={entry.itemId}
        onClick={() => {
          onJumpToClip(entry.itemId as string);
          setOpened(false);
        }}
        style={{ display: 'flex', gap: 6, alignItems: 'flex-start', minHeight: touchH, textAlign: 'left' }}
      >
        <Text size="sm" style={{ flex: 1 }}>
          {tr(entry.text)}
        </Text>
        <IconChevronRight size={14} style={{ marginTop: 4, flexShrink: 0 }} aria-hidden />
      </UnstyledButton>
    ) : (
      <Text key={`${index}-${tr(entry.text)}`} size="sm" data-testid="parity-reason">
        {tr(entry.text)}
      </Text>
    );

  if (quiet && badge.kind === 'faithful') return null;

  return (
    <Popover opened={opened} onChange={setOpened} width={380} position="bottom-end" withArrow shadow="md" trapFocus>
      <Popover.Target>
        <Badge
          component="button"
          type="button"
          color={COLORS[badge.kind]}
          variant="dot"
          size="lg"
          radius="sm"
          tt="none"
          fw={500}
          data-testid="parity-badge"
          data-parity={badge.kind}
          aria-haspopup="dialog"
          aria-expanded={opened}
          aria-label={t('showWhy', { label: badgeLabel })}
          onClick={() => setOpened((o) => !o)}
          style={{ cursor: 'var(--rb-cursor-pointer)', minHeight: touchH, minWidth: 0, flexShrink: 1 }}
        >
          {badgeLabel}
        </Badge>
      </Popover.Target>
      <Popover.Dropdown p="sm" data-testid="parity-reasons">
        <Stack gap="xs">
          <Text fw={600} size="sm">
            {badgeLabel}
          </Text>
          {badge.entries.length === 0 ? <Text size="sm">{t('everythingOnScreen')}</Text> : <Stack gap={4} mah={260} style={{ overflowY: 'auto' }}>{badge.entries.map(line)}</Stack>}
          {elsewhere.length > 0 && (
            <Stack gap={4}>
              <Text size="sm" c="dimmed" fw={600}>
                {t('elsewhereInThisTimeline')}
              </Text>
              <Stack gap={4} mah={200} style={{ overflowY: 'auto' }} data-testid="parity-elsewhere">
                {elsewhere.map(line)}
              </Stack>
            </Stack>
          )}
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
