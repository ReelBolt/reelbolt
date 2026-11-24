'use client';

import React from 'react';
import { ActionIcon, Group, Menu, Text } from '@mantine/core';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';
import { EDITOR_BOTTOM_CHROME_PX } from '@/app/theme';
import { IconCheck, IconDots } from '@tabler/icons-react';
import type { EditTimeline, Track } from '@/lib/editing/types';
import {
  DEFAULT_TIMELINE_VIEW, TRACK_HEIGHT_PRESETS, TRACK_SWATCHES, moveTarget, nearestHeightPreset,
  type TimelineView, type TrackHeightPreset,
} from './trackLayout';

export interface TrackMenuProps {
  doc: EditTimeline;
  track: Track;
  index: number;
  locked: boolean;
  targeted: boolean;
  coarse: boolean;
  onRename: () => void;
  onToggle: (flag: 'locked' | 'enabled' | 'mute' | 'solo') => void;
  onTarget: () => void;
  onSetProperty?: (key: 'color' | 'height', value: string | number | null) => void;
  onMove?: (toIndex: number) => void;
  onAddAt?: (index: number, kind: 'video' | 'audio') => void;
  onRemove?: () => void;
  view?: TimelineView;
  onViewChange?: (patch: Partial<TimelineView>) => void;
  flags: { enabled: boolean; mute: boolean; solo: boolean };
}

/**
 * The track's options menu (R03.5). Every entry is a labelled text item (no icon-only controls),
 * and 44 px tall on a coarse pointer. Nothing here is a new op: names, colours, heights and flags
 * go through `setTrackProperty`, order through `moveTrack`, add/remove through `addTrack` /
 * `removeTrack`; the three view toggles change what the canvas paints and never touch the document.
 */
export function TrackMenu(p: TrackMenuProps) {
  const t = useT('editor-timeline.trackMenu');
  const { track, index, locked, coarse } = p;
  const count = p.doc.tracks.length;
  const itemStyle = coarse ? { minHeight: 44 } : undefined;
  const up = moveTarget(index, count, 'up');
  const down = moveTarget(index, count, 'down');
  const preset = nearestHeightPreset(track.height);
  return (
    <Menu
      position="bottom-end"
      withinPortal
      shadow="md"
      width={252}
      middlewares={{
        flip: { padding: { bottom: EDITOR_BOTTOM_CHROME_PX } },
        shift: { padding: { top: 5, left: 5, right: 5, bottom: EDITOR_BOTTOM_CHROME_PX } },
        // The menu is taller than the room a short phone leaves: it scrolls inside that room, never past the dock.
        size: {
          padding: { top: 8, left: 8, right: 8, bottom: EDITOR_BOTTOM_CHROME_PX },
          apply({ availableHeight, elements }) {
            elements.floating.style.maxHeight = `${Math.max(160, Math.floor(availableHeight))}px`;
          },
        },
      }}
    >
      <Menu.Target>
        <EditorIcon icon={IconDots} dense tooltip={false} label={t('options', { track: track.name })} data-testid={`track-menu-${track.id}`} style={coarse ? { minWidth: 44, minHeight: 44, flexShrink: 0 } : undefined} />
      </Menu.Target>
      {/* Long on a phone: it scrolls inside the screen rather than running off it. */}
      <Menu.Dropdown data-testid={`track-menu-dropdown-${track.id}`} style={{ overflowY: 'auto', overscrollBehavior: 'contain' }}>
        <Menu.Label>{track.name}</Menu.Label>
        {p.onSetProperty && (
          <Menu.Item style={itemStyle} onClick={p.onRename} disabled={locked} data-testid={`track-menu-rename-${track.id}`}>
            {locked ? t('renameLocked') : t('rename')}
          </Menu.Item>
        )}
        <Menu.Item style={itemStyle} onClick={() => p.onToggle('locked')} data-testid={`track-menu-lock-${track.id}`}>
          {locked ? t('unlock') : t('lock')}
        </Menu.Item>
        {track.kind === 'video' ? (
          <Menu.Item style={itemStyle} onClick={() => p.onToggle('enabled')} disabled={locked} data-testid={`track-menu-enable-${track.id}`}>
            {p.flags.enabled ? t('hide') : t('show')}
          </Menu.Item>
        ) : (
          <>
            <Menu.Item style={itemStyle} onClick={() => p.onToggle('mute')} disabled={locked} data-testid={`track-menu-mute-${track.id}`}>
              {p.flags.mute ? t('unmute') : t('mute')}
            </Menu.Item>
            <Menu.Item style={itemStyle} onClick={() => p.onToggle('solo')} disabled={locked} data-testid={`track-menu-solo-${track.id}`}>
              {p.flags.solo ? t('unsolo') : t('solo')}
            </Menu.Item>
          </>
        )}
        <Menu.Item style={itemStyle} onClick={p.onTarget} data-testid={`track-menu-target-${track.id}`}>
          {p.targeted ? t('untarget') : t('target')}
        </Menu.Item>

        {p.onSetProperty && (
          <>
            <Menu.Divider />
            <Menu.Label>{locked ? t('colourLocked') : t('colour')}</Menu.Label>
            <Group gap={6} px="sm" pb={6} wrap="wrap" role="group" aria-label={t('trackColour')}>
              {TRACK_SWATCHES.map((sw) => {
                const on = (track.color ?? '').toLowerCase() === sw.hex;
                return (
                  <ActionIcon
                    key={sw.hex}
                    size={coarse ? 44 : 26}
                    radius="xl"
                    disabled={locked}
                    aria-label={on ? t('swatchCurrent', { name: t(`swatches.${sw.name.toLowerCase()}`) }) : t(`swatches.${sw.name.toLowerCase()}`)}
                    aria-pressed={on}
                    onClick={() => p.onSetProperty?.('color', sw.hex)}
                    data-testid={`track-color-${sw.name.toLowerCase()}-${track.id}`}
                    style={{ background: sw.hex, border: on ? '2px solid var(--mantine-color-text)' : '2px solid transparent', color: '#fff' }}
                  >
                    {on ? <IconCheck size={14} /> : null}
                  </ActionIcon>
                );
              })}
              <Menu.Item
                style={{ ...itemStyle, width: 'auto', flex: '0 0 auto' }}
                disabled={locked || !track.color}
                onClick={() => p.onSetProperty?.('color', null)}
                data-testid={`track-color-none-${track.id}`}
              >
                {t('noColour')}
              </Menu.Item>
            </Group>
            <Menu.Label>{locked ? t('rowHeightLocked') : t('rowHeight')}</Menu.Label>
            {(Object.keys(TRACK_HEIGHT_PRESETS) as TrackHeightPreset[]).map((key) => (
              <Menu.Item
                key={key}
                style={itemStyle}
                disabled={locked}
                rightSection={preset === key && Math.abs(track.height - TRACK_HEIGHT_PRESETS[key]) < 8 ? <IconCheck size={14} /> : null}
                onClick={() => p.onSetProperty?.('height', TRACK_HEIGHT_PRESETS[key])}
                data-testid={`track-height-${key}-${track.id}`}
              >
                {t(`heights.${key}`)}
              </Menu.Item>
            ))}
          </>
        )}

        {p.onMove && (
          <>
            <Menu.Divider />
            <Menu.Item style={itemStyle} disabled={up === null} onClick={() => up !== null && p.onMove?.(up)} data-testid={`track-menu-up-${track.id}`}>
              {t('moveUp')}
            </Menu.Item>
            <Menu.Item style={itemStyle} disabled={down === null} onClick={() => down !== null && p.onMove?.(down)} data-testid={`track-menu-down-${track.id}`}>
              {t('moveDown')}
            </Menu.Item>
          </>
        )}

        {p.onAddAt && (
          <>
            <Menu.Divider />
            <Menu.Item style={itemStyle} onClick={() => p.onAddAt?.(index, 'video')} data-testid={`track-add-above-video-${track.id}`}>{t('addVideoAbove')}</Menu.Item>
            <Menu.Item style={itemStyle} onClick={() => p.onAddAt?.(index, 'audio')} data-testid={`track-add-above-audio-${track.id}`}>{t('addAudioAbove')}</Menu.Item>
            <Menu.Item style={itemStyle} onClick={() => p.onAddAt?.(index + 1, 'video')} data-testid={`track-add-below-video-${track.id}`}>{t('addVideoBelow')}</Menu.Item>
            <Menu.Item style={itemStyle} onClick={() => p.onAddAt?.(index + 1, 'audio')} data-testid={`track-add-below-audio-${track.id}`}>{t('addAudioBelow')}</Menu.Item>
          </>
        )}

        {p.view && p.onViewChange && (
          <>
            <Menu.Divider />
            <Menu.Label>{t('viewLabel')}</Menu.Label>
            {(Object.keys(DEFAULT_TIMELINE_VIEW) as (keyof TimelineView)[]).map((key) => (
              <Menu.Item
                key={key}
                style={itemStyle}
                rightSection={<Text size="xs" fw={600}>{p.view?.[key] ? t('on') : t('off')}</Text>}
                onClick={() => p.onViewChange?.({ [key]: !p.view?.[key] })}
                data-testid={`timeline-view-${key}`}
              >
                {t(`view.${key}`)}
              </Menu.Item>
            ))}
          </>
        )}

        {p.onRemove && (
          <>
            <Menu.Divider />
            <Menu.Item style={itemStyle} color="red" onClick={p.onRemove} data-testid={`track-remove-${track.id}`}>
              {t('remove')}
            </Menu.Item>
          </>
        )}
      </Menu.Dropdown>
    </Menu>
  );
}

