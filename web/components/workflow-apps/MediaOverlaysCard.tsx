'use client';

import { Text } from '@mantine/core';
import type { WorkflowStepResult } from '@/lib/types/workflow';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import styles from './AppRunPage.module.css';

/** One applied picture or video over the film, as the compile reports it (design §6.4). */
export interface MediaOverlayItem {
  media?: string | null;
  /** `Card` (full screen) or `PictureInPicture` (corner). */
  form?: string | null;
  startSec?: number | null;
  endSec?: number | null;
  opacityPercent?: number | null;
  border?: string | null;
  /** Cards only: `Keep` or `Mute`. */
  cardAudio?: string | null;
  /** Videos only: `Muted` or `Mixed`. */
  audio?: string | null;
  /** Videos only: `Quiet`, `Balanced` or `Prominent`. */
  audioLevel?: string | null;
}

export interface MediaOverlayOffer {
  mediaId?: string | null;
  name?: string | null;
}

export interface MediaOverlayDrop {
  mediaId?: string | null;
  reason?: string | null;
}

/** The compile's `mediaOverlays` output node. */
export interface MediaOverlaysOutput {
  applied?: boolean | null;
  items?: MediaOverlayItem[] | null;
  dropped?: MediaOverlayDrop[] | null;
  offered?: MediaOverlayOffer[] | null;
  unusedOffered?: MediaOverlayOffer[] | null;
}

type OverlaySource = Pick<WorkflowStepResult, 'stepType' | 'outputJson' | 'output' | 'status'>;

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function list(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value : null;
}

function seconds(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * The compile's `mediaOverlays` node, if a finished compile wrote one. Reads the step results the
 * page already holds: no extra endpoint, no stored ids, no pixel geometry.
 */
export function findMediaOverlays(results: readonly OverlaySource[] | null | undefined): MediaOverlaysOutput | null {
  let found: MediaOverlaysOutput | null = null;
  for (const result of results ?? []) {
    if (result.status && result.status !== 'Completed' && result.status !== 'Skipped') continue;
    // Only a compile writes the overlay report; a later agent step must never be rendered as it.
    if (result.stepType !== 'VideoCompile') continue;
    const raw = result.outputJson || result.output;
    if (!raw || !raw.includes('mediaOverlays')) continue;
    try {
      const parsed: unknown = JSON.parse(raw);
      const node = record(record(parsed)?.['mediaOverlays']);
      if (node) found = node as MediaOverlaysOutput;
    } catch {
      // A non-JSON output is not an overlay report.
    }
  }
  return found;
}

/** `m:ss`, so the range reads like a video player and never like a raw timestamp. */
function clock(value: number): string {
  const total = Math.max(0, Math.round(value));
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}

function borderWords(value: unknown, t: T): string | null {
  const word = text(value)?.toLowerCase();
  if (word === 'light') return t('borderLight');
  if (word === 'dark') return t('borderDark');
  return null;
}

function levelWord(value: unknown): string {
  switch (text(value)?.toLowerCase()) {
    case 'quiet':
      return 'Quiet';
    case 'prominent':
      return 'Prominent';
    default:
      return 'Balanced';
  }
}

function dropReason(reason: unknown, t: T): string {
  const word = (text(reason) ?? '').toLowerCase();
  if (word.includes('cut') || word.includes('crop')) return t('reasonCut');
  if (word.includes('max') || word.includes('limit')) return t('reasonTooMany');
  if (word.includes('opacity') || word.includes('zero')) return t('reasonInvisible');
  if (word.includes('duplicate')) return t('reasonDuplicate');
  if (word.includes('too_large') || word.includes('too_long') || word.includes('large')) return t('reasonTooLarge');
  return t('reasonUnreadable');
}

interface Props {
  /** The run's step results; only a completed compile's `mediaOverlays` node is read. */
  stepResults?: readonly OverlaySource[] | null;
}

/**
 * What the compile actually showed over the film: one row per applied picture or video, then a
 * quiet list of offered pictures that were not shown. Renders nothing when the compile wrote no
 * overlay report, so an app without overlays gains no empty card.
 */
export function MediaOverlaysCard({ stepResults }: Props) {
  const t = useT('apps.run.overlays');
  const node = findMediaOverlays(stepResults);
  if (!node) return null;

  const items = list(node.items).map(record).filter((item): item is Record<string, unknown> => item !== null);
  const unused = list(node.unusedOffered).map(record).filter((item): item is Record<string, unknown> => item !== null);
  const dropped = list(node.dropped).map(record).filter((item): item is Record<string, unknown> => item !== null);
  if (items.length === 0 && unused.length === 0 && dropped.length === 0) return null;

  const offered = [...list(node.offered), ...list(node.unusedOffered)]
    .map(record)
    .filter((item): item is Record<string, unknown> => item !== null);
  const nameOf = (mediaId: unknown): string => {
    const match = offered.find((offer) => text(offer.mediaId) === text(mediaId));
    return text(match?.name) ?? t('unnamedPicture');
  };

  const rows = items.map((item, index) => {
    const name = text(item.media) ?? t('unnamedPicture');
    const card = (text(item.form) ?? '').toLowerCase() !== 'pictureinpicture';
    const start = seconds(item.startSec);
    const end = seconds(item.endSec);
    const when = start !== null && end !== null ? t('timeRange', { start: clock(start), end: clock(end) }) : null;
    const opacity = seconds(item.opacityPercent);
    const border = borderWords(item.border, t);
    const audio = (text(item.audio) ?? '').toLowerCase();
    const cardAudio = (text(item.cardAudio) ?? '').toLowerCase();
    let sound: string | null = null;
    if (card) {
      if (cardAudio === 'mute') sound = t('soundMuted');
      else if (cardAudio === 'keep') sound = t('soundKept');
    } else if (audio === 'mixed') {
      sound = t('soundAt', { level: levelWord(item.audioLevel) });
    } else if (audio === 'muted') {
      sound = t('soundMuted');
    }
    const details = [
      opacity !== null && opacity < 100 ? t('opacity', { percent: Math.round(opacity) }) : null,
      border,
      sound,
    ].filter((detail): detail is string => detail !== null);
    return { key: `${name}-${index}`, name, headline: [card ? t('fullScreen') : t('corner'), when].filter(Boolean).join(' · '), details };
  });

  // An offered picture the planner did not use is honest information, not an error: it gets the
  // quiet list and the same plain words as a compile drop.
  const notShown = [
    ...unused.map((offer) => ({
      name: text(offer.name) ?? t('unnamedPicture'),
      reason: t('reasonNotChosen'),
    })),
    ...dropped.map((drop) => ({
      name: nameOf(drop.mediaId),
      reason: dropReason(drop.reason, t),
    })),
  ];

  return (
    <section aria-label={t('title')} data-testid="media-overlays" style={{ display: 'grid', gap: 8 }}>
      <Text component="h3" size="sm" fw={600} style={{ margin: 0 }}>
        {t('title')}
      </Text>
      {rows.length > 0 && (
        <ul
          aria-label={t('applied')}
          style={{ listStyle: 'none', margin: 0, padding: 0, display: 'grid', gap: 0 }}
        >
          {rows.map((row, index) => (
            <li
              key={row.key}
              style={{
                display: 'grid',
                gap: 2,
                padding: '8px 0',
                borderTop: index === 0 ? 'none' : '1px solid var(--rb-border)',
              }}
            >
              <Text size="sm" fw={600}>
                {row.name}
              </Text>
              {row.headline && (
                <Text size="sm" className={styles.muted}>
                  {row.headline}
                </Text>
              )}
              {row.details.length > 0 && (
                <Text size="sm" className={styles.muted}>
                  {row.details.join(' · ')}
                </Text>
              )}
            </li>
          ))}
        </ul>
      )}
      {notShown.length > 0 && (
        <div style={{ display: 'grid', gap: 4 }}>
          <Text size="sm" fw={600}>
            {t('notShown')}
          </Text>
          <ul aria-label={t('notShown')} style={{ listStyle: 'none', margin: 0, padding: 0, display: 'grid', gap: 2 }}>
            {notShown.map((row, index) => (
              <li key={`${row.name}-${index}`}>
                <Text size="sm" className={styles.muted}>
                  {t('notShownRow', { name: row.name, reason: row.reason })}
                </Text>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
