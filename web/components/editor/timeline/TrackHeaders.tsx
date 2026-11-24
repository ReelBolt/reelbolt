'use client';

import React from 'react';
import { Box, Button, Group, Modal, Text, TextInput, Tooltip } from '@mantine/core';
import { EditorIcon } from '../ui';
import { useMediaQuery } from '@mantine/hooks';
import { IconEye, IconEyeOff, IconGripVertical, IconHeadphones, IconLock, IconLockOpen, IconVolume, IconVolumeOff, IconChevronDown, IconChevronRight, IconMagnet, IconPlus } from '@tabler/icons-react';
import type { EditTimeline, Track } from '@/lib/editing/types';
import { RULER_HEIGHT, MARKER_LANE_HEIGHT, trackRows, type PaintUi } from './paint';
import { EVENTS_LANE_HEIGHT } from './eventsLane';
import { cleanTrackName, dropIndexAtY, type TimelineView } from './trackLayout';
import { useT } from '@/i18n';
import { TrackMenu } from './TrackMenu';

export interface TrackHeadersProps {
  doc: EditTimeline;
  ui: PaintUi;
  targetedTrackIds: readonly string[];
  onToggle: (trackId: string, flag: 'locked' | 'enabled' | 'mute' | 'solo' | 'collapsed') => void;
  onTarget: (trackId: string) => void;
  /** P4: add a track of a kind at the bottom of the stack. Absent = no add controls (read-only). */
  onAddTrack?: (kind: 'video' | 'audio') => void;
  /** P4: remove a track with everything on it (one undo step). Absent = no remove control. The header asks first when the track has clips. */
  onRemoveTrack?: (trackId: string) => void;
  /** R03.5: store a track's name / colour / height (`setTrackProperty`). Absent = those menu items are not offered. */
  onSetTrackProperty?: (trackId: string, key: 'name' | 'color' | 'height', value: string | number | null) => void;
  /** R03.5: move a track to another position (`moveTrack`). */
  onMoveTrack?: (trackId: string, toIndex: number) => void;
  /** R03.5: add a track of a kind at an index (`addTrack`). */
  onAddTrackAt?: (index: number, kind: 'video' | 'audio') => void;
  /** R03.5: the view-only toggles and their setter. */
  view?: TimelineView;
  onViewChange?: (patch: Partial<TimelineView>) => void;
  /** The Events lane's header row (name and the snap-to-events switch). Absent = no lane, no row. */
  eventsLane?: { label: string; snap: boolean; snapLabel: string; onToggleSnap: () => void };
  width?: number;
  /** Phone layout: finger-sized buttons on two rows (name and target above, the toggles below). */
  compact?: boolean;
}

/**
 * The track headers beside the canvas: name, kind, the lock/enable/mute/solo toggles and the
 * targeting dot. Since P4 the toggles are persisted through `setTrackProperty` ops (the P3 UI
 * overrides remain for a read-only timeline), and the strip above the first track adds a video or
 * audio track while each row can remove its track.
 */
/** A change a locked track would refuse is disabled, and the tooltip says what to do (a disabled button has no tooltip of its own). */
function LockedTip({ locked, children }: { locked: boolean; children: React.ReactNode }) {
  const t = useT('editor-timeline.headers');
  return (
    <Tooltip label={t('unlockFirst')} disabled={!locked}>
      <span style={{ display: 'inline-flex' }} data-locked-tip={locked ? 'true' : undefined}>
        {children}
      </span>
    </Tooltip>
  );
}

export function TrackHeaders({ doc, ui, targetedTrackIds, onToggle, onTarget, onAddTrack, onRemoveTrack, onSetTrackProperty, onMoveTrack, onAddTrackAt, view, onViewChange, eventsLane, width = 240, compact = false }: TrackHeadersProps) {
  const t = useT('editor-timeline.headers');
  const rows = trackRows(doc, ui);
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const [renaming, setRenaming] = React.useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = React.useState<string | null>(null);
  const [drag, setDrag] = React.useState<{ trackId: string; from: number; to: number } | null>(null);
  const rowsRef = React.useRef<HTMLDivElement | null>(null);
  const rowHeights = rows.map((r) => r.height);
  const trackToConfirm = confirmRemove ? doc.tracks.find((x) => x.id === confirmRemove) ?? null : null;
  const requestRemove = (trackId: string) => {
    const target = doc.tracks.find((x) => x.id === trackId);
    if (!target) return;
    if (target.items.length === 0) onRemoveTrack?.(trackId);
    else setConfirmRemove(trackId);
  };
  const dragTo = (event: React.PointerEvent): number => {
    const top = rowsRef.current?.getBoundingClientRect().top ?? 0;
    return dropIndexAtY(rowHeights, event.clientY - top);
  };
  // Dense EditorIcons: 28 px on a coarse pointer (two rows of them fit a default 64 px track), 24 px with a mouse.
  const glyph = 14;
  return (
    <Box data-testid="track-headers" style={{ width, flex: `0 0 ${width}px`, borderRight: '1px solid var(--mantine-color-default-border)' }}>
      <Group gap={4} px={6} wrap="nowrap" style={{ height: RULER_HEIGHT + MARKER_LANE_HEIGHT }}>
        {onAddTrack && (
          <>
            {compact ? (
              <>
                <EditorIcon dense variant="light" color="blue" label={t('addVideoTrack')} tooltip={t('addVideoTip')} onClick={() => onAddTrack('video')} data-testid="add-video-track">
                  <span aria-hidden="true" style={{ fontSize: 12, fontWeight: 700 }}>{t('plusVideo')}</span>
                </EditorIcon>
                <EditorIcon dense variant="light" color="teal" label={t('addAudioTrack')} tooltip={t('addAudioTip')} onClick={() => onAddTrack('audio')} data-testid="add-audio-track">
                  <span aria-hidden="true" style={{ fontSize: 12, fontWeight: 700 }}>{t('plusAudio')}</span>
                </EditorIcon>
              </>
            ) : (
              <>
                <Tooltip label={t('addVideoTip')} openDelay={600}>
                  <Button size="compact-sm" fz="xs" px={6} variant="light" color="blue" c="var(--mantine-color-text)" leftSection={<IconPlus size={12} stroke={1.7} />} aria-label={t('addVideoTrack')} onClick={() => onAddTrack('video')} data-testid="add-video-track">
                    {t('video')}
                  </Button>
                </Tooltip>
                <Tooltip label={t('addAudioTip')} openDelay={600}>
                  <Button size="compact-sm" fz="xs" px={6} variant="light" color="teal" c="var(--mantine-color-text)" leftSection={<IconPlus size={12} stroke={1.7} />} aria-label={t('addAudioTrack')} onClick={() => onAddTrack('audio')} data-testid="add-audio-track">
                    {t('audio')}
                  </Button>
                </Tooltip>
              </>
            )}
          </>
        )}
      </Group>
      {eventsLane && (
        <Group gap={4} px={6} wrap="nowrap" justify="space-between" data-testid="events-lane-header" style={{ height: EVENTS_LANE_HEIGHT, borderTop: '1px solid var(--mantine-color-default-border)', borderBottom: '1px solid var(--mantine-color-default-border)' }}>
          <Text fz="xs" fw={500} truncate>{eventsLane.label}</Text>
          <EditorIcon dense icon={IconMagnet} variant={eventsLane.snap ? 'light' : 'subtle'} color={eventsLane.snap ? 'violet' : 'gray'} label={eventsLane.snapLabel} aria-pressed={eventsLane.snap} onClick={eventsLane.onToggleSnap} data-testid="events-snap-toggle" />
        </Group>
      )}
      <div ref={rowsRef} style={{ position: 'relative' }}>
      {rows.map(({ track, height }, rowIndex) => {
        const flags = ui.trackFlags(track);
        const targeted = targetedTrackIds.includes(track.id);
        return (
          <Group
            key={track.id}
            gap={compact ? 2 : 4}
            px={6}
            wrap={compact ? 'wrap' : 'nowrap'}
            data-testid={`track-header-${track.id}`}
            style={{
              height,
              borderBottom: '1px solid var(--mantine-color-default-border)',
              borderLeft: `4px solid ${track.color ?? 'transparent'}`,
              overflow: 'hidden',
              opacity: drag?.trackId === track.id ? 0.55 : 1,
              boxShadow: drag && drag.to === rowIndex && drag.from !== rowIndex ? `inset 0 ${drag.to > drag.from ? -3 : 3}px 0 var(--mantine-color-blue-5)` : undefined,
            }}
          >
            {!compact && onMoveTrack && (
              <>
                <EditorIcon
                  dense
                  label={t('reorder', { track: track.name })}
                  tooltip={t('reorderTip')}
                  data-testid={`track-grip-${track.id}`}
                  style={{ touchAction: 'none', cursor: 'var(--rb-cursor-grab)' }}
                  onPointerDown={(e) => {
                    e.currentTarget.setPointerCapture(e.pointerId);
                    setDrag({ trackId: track.id, from: rowIndex, to: rowIndex });
                  }}
                  onPointerMove={(e) => {
                    if (drag?.trackId === track.id) {
                      const to = dragTo(e);
                      if (to !== drag.to) setDrag({ ...drag, to });
                    }
                  }}
                  onPointerUp={(e) => {
                    if (drag?.trackId !== track.id) return;
                    const to = dragTo(e);
                    setDrag(null);
                    if (to !== drag.from) onMoveTrack(track.id, to);
                  }}
                  onPointerCancel={() => setDrag(null)}
                >
                  <IconGripVertical size={coarse ? 20 : 14} />
                </EditorIcon>
              </>
            )}
            <EditorIcon dense label={flags.collapsed ? t('expand') : t('collapse')} onClick={() => onToggle(track.id, 'collapsed')}>
              {flags.collapsed ? <IconChevronRight size={glyph} /> : <IconChevronDown size={glyph} />}
            </EditorIcon>
            <EditorIcon
                dense
                variant={targeted ? 'filled' : 'outline'}
                color={track.kind === 'video' ? 'blue' : 'green'}
                tooltip={targeted ? t('targetedTip') : t('targetTip')}
                label={t('target', { track: track.name })}
                aria-pressed={targeted}
                onClick={() => onTarget(track.id)}
                data-testid={`track-target-${track.id}`}
              >
                <span aria-hidden="true" style={{ fontSize: 12, fontWeight: 700 }}>
                  {track.kind === 'video' ? t('videoShort') : t('audioShort')}
                </span>
              </EditorIcon>
            {renaming === track.id && onSetTrackProperty ? (
              <TextInput
                size="xs"
                autoFocus
                defaultValue={track.name}
                aria-label={t('nameFor', { track: track.name })}
                data-testid={`track-rename-input-${track.id}`}
                style={{ flex: 1, minWidth: 0 }}
                styles={{ input: { fontSize: 12, minHeight: coarse ? 44 : undefined } }}
                onFocus={(e) => e.currentTarget.select()}
                onBlur={() => setRenaming(null)}
                onKeyDown={(e) => {
                  if (e.key === 'Escape') setRenaming(null);
                  if (e.key === 'Enter') {
                    const name = cleanTrackName(e.currentTarget.value);
                    setRenaming(null);
                    if (name !== null && name !== track.name) onSetTrackProperty(track.id, 'name', name);
                  }
                }}
              />
            ) : (
              <Text
                size="xs"
                fw={600}
                truncate
                style={{ flex: compact ? '1 1 40px' : 1, minWidth: 0 }}
                title={onSetTrackProperty ? t('renameTip', { track: track.name }) : track.name}
                data-testid={`track-name-${track.id}`}
                onDoubleClick={() => onSetTrackProperty && !flags.locked && setRenaming(track.id)}
              >
                {track.name}
              </Text>
            )}
            <EditorIcon
              dense
              color={flags.locked ? 'orange' : 'gray'}
              label={flags.locked ? t('unlock', { track: track.name }) : t('lock', { track: track.name })}
              aria-pressed={flags.locked}
              onClick={() => onToggle(track.id, 'locked')}
              data-testid={`track-lock-${track.id}`}
            >
              {flags.locked ? <IconLock size={glyph} /> : <IconLockOpen size={glyph} />}
            </EditorIcon>
            {track.kind === 'video' ? (
              <LockedTip locked={flags.locked}><EditorIcon
                dense
                color={flags.enabled ? 'gray' : 'red'}
                label={flags.enabled ? t('disable', { track: track.name }) : t('enable', { track: track.name })}
                aria-pressed={!flags.enabled}
                onClick={() => onToggle(track.id, 'enabled')}
                disabled={flags.locked}
                data-testid={`track-enable-${track.id}`}
              >
                {flags.enabled ? <IconEye size={glyph} /> : <IconEyeOff size={glyph} />}
              </EditorIcon></LockedTip>
            ) : (
              <>
                <LockedTip locked={flags.locked}><EditorIcon
                  dense
                  color={flags.mute ? 'red' : 'gray'}
                  label={flags.mute ? t('unmute', { track: track.name }) : t('mute', { track: track.name })}
                  aria-pressed={flags.mute}
                  onClick={() => onToggle(track.id, 'mute')}
                  disabled={flags.locked}
                  data-testid={`track-mute-${track.id}`}
                >
                  {flags.mute ? <IconVolumeOff size={glyph} /> : <IconVolume size={glyph} />}
                </EditorIcon></LockedTip>
                <LockedTip locked={flags.locked}><EditorIcon
                  dense
                  color={flags.solo ? 'yellow' : 'gray'}
                  label={flags.solo ? t('unsolo', { track: track.name }) : t('solo', { track: track.name })}
                  aria-pressed={flags.solo}
                  onClick={() => onToggle(track.id, 'solo')}
                  disabled={flags.locked}
                  data-testid={`track-solo-${track.id}`}
                >
                  <IconHeadphones size={glyph} />
                </EditorIcon></LockedTip>
              </>
            )}
            <TrackMenu
              doc={doc}
              track={track}
              index={rowIndex}
              locked={flags.locked}
              targeted={targeted}
              coarse={coarse}
              flags={flags}
              // The menu hands focus back to its button as it closes; opening the field after that keeps it from blurring at once.
              onRename={() => window.setTimeout(() => setRenaming(track.id), 220)}
              onToggle={(flag) => onToggle(track.id, flag)}
              onTarget={() => onTarget(track.id)}
              onSetProperty={onSetTrackProperty ? (key, value) => onSetTrackProperty(track.id, key, value) : undefined}
              onMove={onMoveTrack ? (to) => onMoveTrack(track.id, to) : undefined}
              onAddAt={onAddTrackAt}
              onRemove={onRemoveTrack ? () => requestRemove(track.id) : undefined}
              view={view}
              onViewChange={onViewChange}
            />
          </Group>
        );
      })}
      </div>
      <Modal opened={trackToConfirm !== null} onClose={() => setConfirmRemove(null)} title={t('removeTitle')} centered>
        <Text size="sm" data-testid="track-remove-confirm-text">{trackToConfirm ? t('removeConfirm', { track: trackToConfirm.name, count: trackToConfirm.items.length }) : ''}</Text>
        <Group justify="flex-end" mt="md" gap="xs">
          <Button variant="default" size={coarse ? 'md' : 'sm'} onClick={() => setConfirmRemove(null)} data-testid="track-remove-cancel">{t('keep')}</Button>
          <Button
            color="red"
            size={coarse ? 'md' : 'sm'}
            data-testid="track-remove-confirm"
            onClick={() => {
              const id = confirmRemove;
              setConfirmRemove(null);
              if (id) onRemoveTrack?.(id);
            }}
          >
            {t('remove')}
          </Button>
        </Group>
      </Modal>
    </Box>
  );
}

export type { Track };
