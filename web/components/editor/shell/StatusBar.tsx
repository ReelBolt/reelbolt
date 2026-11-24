'use client';

import React, { useState } from 'react';
import { Button, List, Popover, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconLock } from '@tabler/icons-react';
import type { SaveState } from '@/lib/editing/sync';
import { saveStateText, syncReasonText } from './save-state-text';
import type { PlaybackStats, PreviewSessionKind } from '@/lib/editing/preview';
import classes from './workspace.module.css';
import { useFormat, useT } from '@/i18n';

export interface StatusBarProps {
  save: SaveState;
  stats: PlaybackStats;
  sessionKind: PreviewSessionKind;
  /** The last finished render of THIS revision's region cache picture; null when no render matches. */
  renderCache?: { total: number; cached: number } | null;
  timecode: string;
  readOnly: boolean;
  /** Phone layout: only the save state and the timecode. */
  compact?: boolean;
  /** The phone header already owns the save chip, Retry and Sign in: leave them out here so each exists once. */
  hideSave?: boolean;
  /** The phone strip already shows the playhead timecode: leave the status readout out so it exists once. */
  hideTimecode?: boolean;
  /** Re-sends the timeline after a refused or failed save (the sync client's `retry`). */
  onRetry?: () => void;
  /** R02.2: reopens the sign-in-in-place dialog after the session expired. */
  onSignIn?: () => void;
}

/** The states a person can act on with Retry. */
export const RETRYABLE = new Set<SaveState['status']>(['rejected', 'error', 'invalid', 'retrying']);

/** Why the last save did not land, as the sync client's plain sentences, behind a tap/click. */
function SaveReasons({ reasons }: { reasons: string[] }) {
  const t = useT('editor.shell.statusBar');
  const [opened, setOpened] = useState(false);
  return (
    <Popover opened={opened} onChange={setOpened} position="top-start" withArrow shadow="md" width={320}>
      <Popover.Target>
        <UnstyledButton
          className={classes.statusItem}
          onClick={() => setOpened((o) => !o)}
          aria-expanded={opened}
          data-testid="save-reasons-toggle"
          style={{ textDecoration: 'underline', fontSize: 'inherit' }}
        >
          {reasons.length === 1 ? t('why') : t('why2', { length: reasons.length })}
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown data-testid="save-reasons">
        <Text size="xs" fw={600} mb={4}>
          {t('whyThisWasNot')}
        </Text>
        <List size="xs" spacing={2}>
          {reasons.map((reason) => (
            <List.Item key={reason} data-testid="save-reason">
              {syncReasonText(t, reason)}
            </List.Item>
          ))}
        </List>
      </Popover.Dropdown>
    </Popover>
  );
}

function saveColor(state: SaveState): string {
  switch (state.status) {
    case 'saved':
      return 'var(--mantine-color-teal-6)';
    case 'dirty':
    case 'saving':
    case 'retrying':
      return 'var(--mantine-color-yellow-6)';
    case 'conflict':
    case 'authExpired':
    case 'otherTab':
    case 'takenOver':
      return 'var(--mantine-color-orange-6)';
    default:
      return 'var(--mantine-color-red-6)';
  }
}

/**
 * The status bar: save state, the user-visible dropped-frame counter, the session kind,
 * the render-cache chip and the playhead timecode. Every number here is MEASURED by the session or
 * reported by the sync client; nothing is estimated in this component. Colour is used only where a
 * state needs attention (unsaved, dropping frames, degraded preview).
 */
export function StatusBar({ save, stats, sessionKind, renderCache, timecode, readOnly, compact = false, hideSave = false, hideTimecode = false, onRetry, onSignIn }: StatusBarProps) {
  const t = useT('editor.shell.statusBar');
  const nf = useFormat();
  // The sentence already says the first reason (rejected/invalid); "Why?" lists the rest. A 401, a missing or a
  // forbidden timeline need no list: the sentence is the whole story.
  const reasons =
    hideSave ||
    save.status === 'saved' || save.status === 'dirty' || save.status === 'saving' || save.status === 'authExpired' || save.problem === 'deleted' || save.problem === 'noPermission'
      ? []
      : (save.reasons ?? []);
  const canRetry = !hideSave && onRetry && RETRYABLE.has(save.status) && save.problem !== 'deleted' && save.problem !== 'noPermission';
  const canSignIn = !hideSave && onSignIn && save.status === 'authExpired';
  // A tap target is at least 44 px on a phone: the bar grows while it carries a button.
  const tall = compact && (canRetry || canSignIn);
  const actionProps = compact ? { size: 'md' as const, h: 44, miw: 64 } : { size: 'compact-xs' as const };
  const chipText = saveStateText(t, save);
  const dropPct = stats.deadlines > 0 ? Math.round((stats.droppedFrames / stats.deadlines) * 1000) / 10 : 0;
  const dropColor = stats.droppedFrames === 0 ? undefined : dropPct > 5 ? 'var(--mantine-color-red-6)' : 'var(--mantine-color-yellow-6)';
  const sessionColor = sessionKind === 'live' ? 'var(--mantine-color-teal-6)' : sessionKind === 'fallback' ? 'var(--mantine-color-orange-6)' : undefined;
  return (
    <div className={classes.status} data-testid="status-bar" role="status" aria-live="polite" style={tall ? { height: 52 } : undefined}>
      <div className={classes.statusGroup} style={{ minWidth: 0 }}>
        {!hideSave && <span
          className={classes.statusItem}
          data-testid="save-state-chip"
          data-status={save.status}
          title={chipText}
          style={{ color: save.status === 'saved' ? undefined : saveColor(save), fontSize: 12, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
        >
          <span className={classes.dot} style={{ color: saveColor(save), flex: '0 0 auto' }} aria-hidden />
          {chipText}
        </span>}
        {reasons.length > 0 && <SaveReasons reasons={reasons} />}
        {canRetry && (
          <Button {...actionProps} variant="light" color="gray" onClick={onRetry} data-testid="save-retry">
            {t('retry')}
          </Button>
        )}
        {canSignIn && (
          <Button {...actionProps} variant="light" color="orange" onClick={onSignIn} data-testid="save-sign-in">
            {t('signIn')}
          </Button>
        )}
        {readOnly && (
          <span className={classes.statusItem} data-testid="read-only-chip" style={{ color: 'var(--mantine-color-orange-6)', fontWeight: 600 }}>
            <IconLock size={12} aria-hidden />
            {t('readOnly')}
          </span>
        )}
      </div>

      <div className={classes.statusGroup} style={{ flex: 1, justifyContent: 'center', overflow: 'hidden' }}>
        <Popover position="top" withArrow shadow="md" keepMounted>
          <Popover.Target>
            <UnstyledButton
              data-testid="playback-details-toggle"
              className={`${classes.statusItem} ${classes.playbackToggle}`}
              style={{ color: dropColor, minHeight: 24, padding: '0 6px' }}
            >
              <span className={classes.dot} style={{ color: sessionColor ?? 'var(--rb-ed-line-strong)' }} aria-hidden />
              {stats.droppedFrames === 0 ? t('playbackDetails') : t('playbackSkipped', { count: stats.droppedFrames })}
            </UnstyledButton>
          </Popover.Target>
          <Popover.Dropdown>
            <Tooltip
              label={t('deadlines', { deadlines: stats.deadlines, presentedFrames: stats.presentedFrames, latency: nf.number(stats.latenessP50Ms, { minimumFractionDigits: 1, maximumFractionDigits: 1 }), fps: nf.number(stats.achievedFps, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })}
            >
              <Text
                size="xs"
                data-testid="dropped-frames"
                data-dropped={stats.droppedFrames}
                data-deadlines={stats.deadlines}
                data-presented={stats.presentedFrames}
                data-repeats={stats.repeatedFrames}
                data-achieved-fps={stats.achievedFps.toFixed(1)}
                style={{ color: dropColor }}
              >
                {t('skippedFrames', { droppedFrames: stats.droppedFrames })}
                {stats.deadlines > 0 ? ` (${dropPct}%)` : ''}
              </Text>
            </Tooltip>
            <Text size="xs" data-testid="session-kind">
              {sessionKind === 'live' ? t('livePreview') : sessionKind === 'fallback' ? t('simplePreviewSlow') : t('noPreviewShort')}
            </Text>
            {!compact && (
              <Text size="xs" data-testid="render-cache-chip">
                {renderCache ? t('savedFrames', { cached: renderCache.cached, total: renderCache.total }) : t('savedFramesNone')}
              </Text>
            )}
          </Popover.Dropdown>
        </Popover>
      </div>

      {!hideTimecode && (
        <span className={classes.timecode} data-testid="status-timecode">
          {timecode}
        </span>
      )}
    </div>
  );
}
