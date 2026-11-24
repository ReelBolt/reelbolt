'use client';

import React, { useRef } from 'react';
import { Badge, Box, Group, Menu, Text, UnstyledButton } from '@mantine/core';
import { IconDots, IconEye, IconFolderMinus, IconFolderPlus, IconMusic, IconNotes, IconPhoto, IconPlayerTrackNext, IconPlus, IconVideo } from '@tabler/icons-react';
import { TilePoster } from '../media/TilePoster';
import { formatBytes, formatDuration } from '@/lib/editing/media/format';
import { classifyTileScrub } from '@/lib/editing/timeline-gestures';
import type { MediaBin, MediaKind, MediaPoolItem, MediaRef } from '@/lib/editing/media/types';
import { readinessMsg, type TileReadiness } from './media-readiness';
import { placeChoiceLabelMsg } from '../place/placeChoices';
import classes from './phone.module.css';
import { useT } from '@/i18n';

const KIND_ICON: Record<MediaKind, React.ComponentType<{ size?: number }>> = { video: IconVideo, audio: IconMusic, image: IconPhoto };
const CHIP_COLOR: Record<TileReadiness, string> = { ready: 'green', processing: 'blue', notReady: 'yellow' };

export interface MediaTileProps {
  projectId: string;
  item: MediaPoolItem;
  readiness: TileReadiness;
  /** Every bin, smart ones included: choosing a smart bin shows the plain refusal instead of hiding the choice. */
  bins: readonly MediaBin[];
  binNames: ReadonlyMap<string, string>;
  readOnly: boolean;
  scrubLabel: string | null;
  onInsert: (item: MediaPoolItem) => void;
  onAppend: (item: MediaPoolItem) => void;
  onAddToBin: (item: MediaPoolItem, binId: string) => void;
  onRemoveFromBin: (item: MediaPoolItem, binId: string, itemId: string) => void;
  onAnnotate: (item: MediaPoolItem) => void;
  onPreview: (item: MediaPoolItem) => void;
  onScrub: (mediaRef: MediaRef, fraction: number) => void;
  onScrubEnd: () => void;
}

/**
 * One tile of the Add media sheet. A tap opens its action menu (every item 44 px tall); a finger dragged sideways over
 * the picture scrubs it (R04.6, same rule as the desktop pool). There is no HTML5 drag: every action is a button.
 */
export function MediaTile({ projectId, item, readiness, bins, binNames, readOnly, scrubLabel, onInsert, onAppend, onAddToBin, onRemoveFromBin, onAnnotate, onPreview, onScrub, onScrubEnd }: MediaTileProps) {
  const tx = useT('editor.phone.mediaTile');
  const Icon = KIND_ICON[item.kind];
  const touchRef = useRef<{ id: number; x: number; y: number; state: 'pending' | 'scrub' | 'scroll' } | null>(null);
  const swallowClick = useRef(false);
  const endTouch = () => {
    if (touchRef.current?.state === 'scrub') onScrubEnd();
    touchRef.current = null;
  };
  const duration = item.probe?.durationSec != null ? formatDuration(item.probe.durationSec) : null;
  const pins = item.bins.map((pin) => ({ ...pin, name: binNames.get(pin.binId) })).filter((p): p is typeof p & { name: string } => Boolean(p.name));

  return (
    <Box className={classes.mediaTile} data-testid="add-media-tile" data-media-ref={item.mediaRef} data-readiness={readiness}>
      <UnstyledButton
        className={classes.mediaTilePicture}
        aria-label={tx('dragSidewaysToLook', { fileName: item.fileName })}
        data-testid="media-clip-tile"
        data-media-ref={item.mediaRef}
        onPointerDown={(event: React.PointerEvent<HTMLButtonElement>) => {
          if (event.pointerType !== 'touch') return;
          swallowClick.current = false;
          touchRef.current = { id: event.pointerId, x: event.clientX, y: event.clientY, state: 'pending' };
        }}
        onPointerMove={(event: React.PointerEvent<HTMLButtonElement>) => {
          const t = touchRef.current;
          if (!t || t.id !== event.pointerId || t.state === 'scroll') return;
          if (t.state === 'pending') {
            const kind = classifyTileScrub(event.clientX - t.x, event.clientY - t.y);
            if (kind === 'pending') return;
            if (kind === 'scroll') {
              t.state = 'scroll';
              return;
            }
            t.state = 'scrub';
            swallowClick.current = true;
          }
          const rect = event.currentTarget.getBoundingClientRect();
          if (rect.width > 0) onScrub(item.mediaRef, Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)));
        }}
        onPointerUp={endTouch}
        onPointerCancel={endTouch}
        onClick={() => {
          if (swallowClick.current) {
            swallowClick.current = false;
            return;
          }
          onPreview(item);
        }}
      >
        <TilePoster projectId={projectId} item={item} height={88} />
        {scrubLabel ? (
          <Text data-testid="media-tile-scrub-label" role="status" size="xs" fw={600} className={classes.mediaScrubLabel}>
            {scrubLabel}
          </Text>
        ) : null}
      </UnstyledButton>
      <Group gap={6} wrap="nowrap" mt={6}>
        <Icon size={14} />
        <Text size="sm" fw={600} lineClamp={1} title={item.fileName} style={{ flex: 1, minWidth: 0 }}>
          {item.fileName}
        </Text>
      </Group>
      <Text size="xs" c="dimmed" data-testid="media-tile-facts">
        {[tx(`kind.${item.kind}`), duration, formatBytes(item.sizeBytes)].filter(Boolean).join(' · ')}
      </Text>
      <Badge size="md" fz="xs" variant="light" color={CHIP_COLOR[readiness]} mt={4} style={{ textTransform: 'none', height: 'auto', minHeight: 22, whiteSpace: 'normal' }} data-testid="add-media-chip">
        {tx.msg(readinessMsg(readiness))}
      </Badge>
      {pins.length > 0 ? (
        <Group gap={4} mt={4} wrap="wrap" data-testid="media-tile-bins">
          {pins.slice(0, 3).map((p) => (
            <Badge key={p.itemId} size="xs" fz="xs" variant="outline" color="gray" style={{ textTransform: 'none' }}>
              {p.name}
            </Badge>
          ))}
        </Group>
      ) : null}
      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <UnstyledButton className={classes.mediaTileMenu} aria-label={tx('actionsFor', { fileName: item.fileName })} data-testid="media-tile-menu" data-for-media-ref={item.mediaRef}>
            <IconDots size={20} />
            <span>{tx('actions')}</span>
          </UnstyledButton>
        </Menu.Target>
        <Menu.Dropdown data-testid="add-media-menu">
          <Menu.Label>{tx('putItOnThe')}</Menu.Label>
          <Menu.Item mih={44} leftSection={<IconPlus size={16} />} disabled={readOnly} onClick={() => onInsert(item)} data-testid="tile-place-insert">
            {tx.msg(placeChoiceLabelMsg('insert'))}
          </Menu.Item>
          <Menu.Item mih={44} leftSection={<IconPlayerTrackNext size={16} />} disabled={readOnly} onClick={() => onAppend(item)} data-testid="tile-place-append">
            {tx.msg(placeChoiceLabelMsg('append'))}
          </Menu.Item>
          <Menu.Divider />
          <Menu.Label>{tx('addToBin')}</Menu.Label>
          {bins.length === 0 ? (
            <Menu.Item mih={44} disabled>
              {tx('noBinsYetMake')}
            </Menu.Item>
          ) : (
            bins.map((bin) => (
              <Menu.Item key={bin.id} mih={44} leftSection={<IconFolderPlus size={16} />} onClick={() => onAddToBin(item, bin.id)} data-testid={`add-to-bin-${bin.id}`}>
                {bin.name}
              </Menu.Item>
            ))
          )}
          {pins.length > 0 ? (
            <>
              <Menu.Divider my={8} />
              <Menu.Label>{tx('removeFromBin')}</Menu.Label>
              {pins.map((p) => (
                <Menu.Item key={p.itemId} mih={44} leftSection={<IconFolderMinus size={16} />} onClick={() => onRemoveFromBin(item, p.binId, p.itemId)} data-testid={`remove-from-bin-${p.binId}`}>
                  {tx('removeFrom', { name: p.name })}
                </Menu.Item>
              ))}
            </>
          ) : null}
          <Menu.Divider my={8} />
          <Menu.Item mih={44} leftSection={<IconNotes size={16} />} onClick={() => onAnnotate(item)} data-testid="tile-annotate">
            {tx('keywordsAndNotes')}
          </Menu.Item>
          <Menu.Item mih={44} leftSection={<IconEye size={16} />} onClick={() => onPreview(item)} data-testid="tile-preview">
            {tx('preview')}
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
    </Box>
  );
}
