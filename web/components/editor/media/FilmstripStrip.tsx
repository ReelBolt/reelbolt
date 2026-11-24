'use client';

import React from 'react';
import { Badge, Box, Group, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useFormat, useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import { filmstripSheetUrl } from '@/lib/editing/api/media';
import { formatBytes } from '@/lib/editing/media/format';
import { filmstripCellBackground, filmstripDurationSec, resolveFilmstripThumb } from '@/lib/editing/media/filmstrip';
import { derivativeKindsToShow } from '@/lib/editing/media/derivative-kinds';
import type {
  FilmstripIndex,
  MediaDerivativeKind,
  MediaDerivativeRow,
  MediaPoolItem,
} from '@/lib/editing/media/types';

export interface FilmstripStripProps {
  /**
   * The clip the strip currently describes — the pool's SUBJECT, which is not the same thing as
   * "whatever the pointer is on this instant": it is held for as long as the pointer is anywhere in
   * the pool/strip region, and for as long as keyboard focus is inside it, so the control survives
   * the journey from the tile that named the clip to the strip that scrubs it. `null` means no clip
   * is held at all.
   */
  item: MediaPoolItem | null;
  /**
   * The filmstrip index for that clip, or null while it is unknown.
   *
   * A null index has two distinct meanings and the strip renders them differently, because they
   * call for different user actions: `indexError` set means the index could not be READ (a failed
   * request — retryable), while a null index with no error means the source genuinely has none yet.
   * Neither is ever rendered as a guessed frame.
   */
  index: FilmstripIndex | null;
  /** A failure message from the filmstrip-index fetch, if any. */
  indexError: string | null;
  /** The clip's derivative rows, or null while they are still being fetched. */
  derivatives: MediaDerivativeRow[] | null;
  /** A failure message from the derivatives fetch, if any. */
  derivativesError: string | null;
  /** Scrub position in source seconds. */
  timeSec: number;
  onScrub: (timeSec: number) => void;
}

/**
 * One label per landed kind. All seven are shown rather than only the three the editor consumes,
 * because the listing endpoint returns every row of the source regardless of kind: hiding a kind
 * would report "no rows" for a source that has some.
 */
const kindLabels = (t: T): Record<MediaDerivativeKind, string> => ({
  proxy: t('kind.proxy'),
  waveform: t('kind.waveform'),
  waveformPeaks: t('kind.waveformPeaks'),
  thumbnail: t('kind.thumbnail'),
  filmstrip: t('kind.filmstrip'),
  keyframeIndex: t('kind.keyframeIndex'),
  stills: t('kind.stills'),
  stabilizedProxy: t('kind.stabilizedProxy'),
});

/**
 * The hover-scrub strip.
 *
 * Its whole job is to be honest about what it cannot show. Every state below is stated, and none of
 * them is a spinner that cannot resolve, a wrong frame, or an empty box:
 *
 *   1. nothing hovered            → a plain instruction
 *   2. derivatives loading        → a stated "loading" line
 *   3. derivatives failed         → the reason
 *   4. index failed               → the reason (distinct from 5: retryable)
 *   5. no index for this source   → "No filmstrip index", with why, and what to do about it
 *   6. index present, no sheet    → the geometry was measured but that sheet's bytes are not
 *      reachable, which is a PER-SHEET degradation (the sheet route answers 404 for its own object)
 *      rather than a hole in the index
 *   7. a real sprite cell         → the measured frame, drawn from that sheet's own URL
 *
 * The scrub range is derived from the MEASURED thumbnail count
 * (`filmstripDurationSec`), never from the probe's `durationSec` and never from the scrub position
 * itself: the two sources of truth disagree, and reconciling them is precisely the guesswork that
 * makes a scrub land on the wrong thumbnail.
 */
export function FilmstripStrip({
  item,
  index,
  indexError,
  derivatives,
  derivativesError,
  timeSec,
  onScrub,
}: FilmstripStripProps) {
  const t = useT('editor-media.filmstrip');
  const f = useFormat();
  const oneDecimal = { minimumFractionDigits: 1, maximumFractionDigits: 1 };
  const coarsePointer = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const position = item ? resolveFilmstripThumb(index, timeSec, filmstripSheetUrl) : null;

  const scrubRangeSec = filmstripDurationSec(index);

  const handlePointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!item || scrubRangeSec <= 0) return;
    const rect = event.currentTarget.getBoundingClientRect();
    if (rect.width <= 0) return;
    const fraction = Math.min(Math.max((event.clientX - rect.left) / rect.width, 0), 1);
    onScrub(fraction * scrubRangeSec);
  };

  /**
   * Keyboard scrubbing, on the same control as the pointer.
   *
   * Hover-scrub is a pointer affordance, and a strip that could ONLY be scrubbed by hovering would
   * be unusable by keyboard — the position would be unreachable, not merely undiscoverable. The
   * arrows step by one thumbnail (the density the index actually declares), Home/End jump to the
   * ends, and the control advertises itself as a slider with the measured range so a screen reader
   * announces a real bound rather than an invented one.
   */
  const handleScrubKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (!item || scrubRangeSec <= 0) return;

    const step = position ? position.densitySec : 1;
    let next: number | null = null;

    if (event.key === 'ArrowRight' || event.key === 'ArrowUp') next = timeSec + step;
    else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') next = timeSec - step;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = scrubRangeSec;
    if (next === null) return;

    event.preventDefault();
    onScrub(Math.min(Math.max(next, 0), scrubRangeSec));
  };

  return (
    <Box
      data-testid="filmstrip-strip"
      p="sm"
      style={{ border: '1px solid var(--mantine-color-gray-3)', borderRadius: 8 }}
    >
      <Group justify="space-between" mb={6}>
        <Text size="sm" fw={600}>
          {t('title')}
        </Text>
        {item ? (
          <Text size="xs" c="dimmed" data-testid="filmstrip-subject">
            {item.fileName}
          </Text>
        ) : null}
      </Group>

      {!item ? (
        <Text size="sm" c="dimmed" data-testid="filmstrip-idle">
          {coarsePointer ? t('idleTouch') : t('idlePointer')}
        </Text>
      ) : (
        <Stack gap={6}>
          <DerivativeKindStates item={item} derivatives={derivatives} error={derivativesError} />

          {derivativesError ? (
            <Text size="sm" c="red" data-testid="filmstrip-error">
              {t('derivativesError', { error: derivativesError })}
            </Text>
          ) : indexError ? (
            <Text size="sm" c="red" data-testid="filmstrip-index-error">
              {t('indexError', { error: indexError })}
            </Text>
          ) : !derivatives || (!index && !indexError) ? (
            <Text size="sm" c="dimmed" data-testid="filmstrip-loading">
              {t('loading')}
            </Text>
          ) : !index || index.densities.length === 0 ? (
            <Stack gap={2}>
              <Text size="sm" fw={600} data-testid="filmstrip-index-unavailable">
                {t('noIndex')}
              </Text>
              <Text size="xs" c="dimmed">
                {t('noIndexHint')}
              </Text>
            </Stack>
          ) : !position ? (
            <Text size="sm" c="dimmed" data-testid="filmstrip-no-frame">
              {t('noFrame')}
            </Text>
          ) : position.sheetUrl === null ? (
            <Stack gap={2}>
              <Text size="sm" fw={600} data-testid="filmstrip-images-unavailable">
                {t('imageUnavailable')}
              </Text>
              <Text size="xs" c="dimmed">
                {t('imageUnavailableHint', { thumb: position.thumbIndex, sheet: position.sheetIndex + 1, column: position.thumbX + 1, row: position.thumbY + 1 })}
              </Text>
            </Stack>
          ) : (
            <Box
              data-testid="filmstrip-cell"
              role="slider"
              tabIndex={0}
              aria-label={t('cellAria', { thumb: position.thumbIndex, seconds: f.number(position.thumbTimeSec, oneDecimal) })}
              aria-valuemin={0}
              aria-valuemax={scrubRangeSec}
              aria-valuenow={Math.min(timeSec, scrubRangeSec)}
              aria-valuetext={t('cellValue', { thumb: position.thumbIndex, seconds: f.number(position.thumbTimeSec, oneDecimal) })}
              data-thumb-index={position.thumbIndex}
              onPointerMove={handlePointerMove}
              onKeyDown={handleScrubKeyDown}
              style={{
                width: '100%',
                height: 120,
                backgroundImage: `url(${position.sheetUrl})`,
                backgroundRepeat: 'no-repeat',
                // The sheet is a cols x rows grid scaled so one cell fills the box; the cell is picked
                // with container-relative percentages (see `filmstripCellBackground`), read off the
                // density, never off an assumed tile size.
                ...filmstripCellBackground(position),
                border: '1px solid var(--mantine-color-gray-3)',
                borderRadius: 6,
              }}
            />
          )}

          {position ? (
            <Text size="xs" c="dimmed" data-testid="filmstrip-position">
              {t('position', { density: position.densitySec, thumb: position.thumbIndex, seconds: f.number(position.thumbTimeSec, oneDecimal), sheet: position.sheetIndex + 1, column: position.thumbX + 1, row: position.thumbY + 1 })}
            </Text>
          ) : null}
        </Stack>
      )}
    </Box>
  );
}

/**
 * Per-kind presence: each landed derivative kind is shown as present (with its size) or absent.
 *
 * The absent label says **"not generated yet"** and nothing stronger, because the contract carries
 * no `state` field: the API cannot distinguish "not generated" from "failed" from "unsupported", so
 * claiming either of the latter two would be inventing a fact.
 */
function DerivativeKindStates({
  item,
  derivatives,
  error,
}: {
  item: MediaPoolItem;
  derivatives: MediaDerivativeRow[] | null;
  error: string | null;
}) {
  const t = useT('editor-media.filmstrip');
  if (error) return null;
  const labels = kindLabels(t);

  return (
    <Group gap={6} data-testid="derivative-kind-states">
      {derivativeKindsToShow(item, derivatives).map((kind) => {
        const rows = derivatives?.filter((row) => row.kind === kind) ?? [];
        const present = rows.length > 0;
        const totalBytes = rows.reduce((sum, row) => sum + row.sizeBytes, 0);

        return (
          <Badge
            key={kind}
            size="sm" fz="xs"
            variant={present ? 'filled' : 'outline'}
            color={present ? 'teal' : 'gray'}
            data-testid={`derivative-kind-${kind}`}
            data-present={present ? 'true' : 'false'}
          >
            {t('kindState', {
              kind: labels[kind],
              state: derivatives === null ? t('stateLoading') : present ? formatBytes(totalBytes) : t('stateAbsent'),
            })}
          </Badge>
        );
      })}
    </Group>
  );
}
