'use client';

/**
 * The metadata panel — what the media API actually knows about one source.
 *
 * ## Every row is either a real fact or an explicit unknown
 *
 * The panel renders eight technical rows — codec, container, frame rate, resolution, duration,
 * channel count, sample rate and bitrate — and not one of them can ever be `NaN`, `undefined`,
 * `null`, an empty string, a fabricated `0` or a stand-in like "unknown codec" presented as data.
 * Two mechanisms guarantee that: `format.ts`'s helpers are TOTAL (they return a stated label for any
 * input), and a row whose value the API does not carry renders {@link NOT_PROBED} instead of a
 * number nobody measured.
 *
 * ## All eight rows are now real
 *
 * The landed `MediaProbeResponse` appends six fields to the original five —
 * `videoCodec`, `audioCodec`, `container`, `audioChannels`, `audioSampleRateHz`, `bitrateBps` — so
 * the four rows that previously had no field behind them at all are read from the probe. They are
 * **nullable and null when unprobed**, including for any artifact persisted before the fields
 * existed, which is why the explicit unknown state is a first-class state here rather than a
 * fallback: `null` says exactly what is true, and a `0` would state something ffprobe never said.
 *
 * ## The container row is the one that must not be over-simplified
 *
 * `container` is ffprobe's `format_name` **verbatim**, and for a sniffed MP4 that is the
 * comma-separated probe list `mov,mp4,m4a,3gp,3g2,mj2` — six demuxers that all claimed the header.
 * `formatContainer` renders that readably while keeping the raw list visible, so the panel never
 * presents one clean container name the file may not have. When the probe carries no container at
 * all, the row falls back to the source's declared MIME type and **says so**, because a declared
 * type is a different fact from a sniffed one.
 */

import React from 'react';
import { Box, Code, Group, Stack, Text } from '@mantine/core';
import {
  NOT_ANALYSED,
  NOT_PROBED,
  formatBitrateVerbose,
  formatBytes,
  formatChannels,
  formatCodecs,
  formatContainer,
  formatDuration,
  formatFrameRate,
  formatResolution,
  formatSampleRate,
} from '@/lib/editing/media/format';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { cameraProfileName } from '@/lib/editing/color/camera-profile-ops';
import { EditorPanel } from '../ui/EditorPanel';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';

/** Where a row's value came from, so the panel can explain itself. */
export type MetadataValueSource = 'probe' | 'mimeType' | 'poolItem' | 'unavailable';

/** One rendered row of the panel. */
export interface MetadataRow {
  /** Stable key, used as the test hook. */
  key: string;
  label: string;
  /** Always a non-empty, human-readable string. */
  value: string;
  source: MetadataValueSource;
}

/** `video/mp4` → `mp4`; anything unusable → null, so the caller can state the absence itself. */
function containerFromMimeType(mimeType: string | null | undefined): string | null {
  if (typeof mimeType !== 'string') return null;
  const parts = mimeType.split('/');
  const subtype = parts.length === 2 ? parts[1].trim() : '';
  return subtype.length > 0 ? subtype : null;
}

/**
 * The panel's rows for one item — a pure function, so the "no bad value" property can be asserted
 * on the values themselves as well as on the rendered DOM.
 *
 * The order is fixed and is the one the acceptance criterion names: codec, container, frame rate,
 * resolution, duration, channels, sample rate, bitrate.
 *
 * `t` is the `editor-media.metadata` translator; without it the labels are the row keys (tests of the
 * values need no language).
 */
export function metadataRows(item: MediaPoolItem | null | undefined, t?: T): MetadataRow[] {
  const label = {
    codec: t ? t('rows.codec') : 'codec',
    container: t ? t('rows.container') : 'container',
    frameRate: t ? t('rows.frameRate') : 'frameRate',
    resolution: t ? t('rows.resolution') : 'resolution',
    duration: t ? t('rows.duration') : 'duration',
    channels: t ? t('rows.channels') : 'channels',
    sampleRate: t ? t('rows.sampleRate') : 'sampleRate',
    bitrate: t ? t('rows.bitrate') : 'bitrate',
  };
  const probe = item && typeof item === 'object' ? item.probe : null;

  const probedContainer = formatContainer(probe?.container);
  const declaredContainer = containerFromMimeType(item?.mimeType);
  const container =
    probedContainer !== NOT_PROBED
      ? { value: probedContainer, source: 'probe' as const }
      : declaredContainer
        ? { value: t ? t('rows.declaredMime', { type: declaredContainer }) : declaredContainer, source: 'mimeType' as const }
        : { value: NOT_PROBED, source: 'unavailable' as const };

  return [
    {
      key: 'codec',
      label: label.codec,
      value: formatCodecs(probe?.videoCodec, probe?.audioCodec),
      source: probe ? 'probe' : 'unavailable',
    },
    { key: 'container', label: label.container, value: container.value, source: container.source },
    {
      key: 'frameRate',
      label: label.frameRate,
      value: formatFrameRate(probe?.fpsNum ?? null, probe?.fpsDen ?? null),
      source: 'probe',
    },
    {
      key: 'resolution',
      label: label.resolution,
      value: formatResolution(probe?.width ?? null, probe?.height ?? null),
      source: 'probe',
    },
    {
      key: 'duration',
      label: label.duration,
      value: formatDuration(probe?.durationSec ?? null),
      source: 'probe',
    },
    {
      key: 'channels',
      label: label.channels,
      value: formatChannels(probe?.audioChannels),
      source: 'probe',
    },
    {
      key: 'sampleRate',
      label: label.sampleRate,
      value: formatSampleRate(probe?.audioSampleRateHz),
      source: 'probe',
    },
    {
      key: 'bitrate',
      label: label.bitrate,
      value: formatBitrateVerbose(probe?.bitrateBps),
      source: 'probe',
    },
  ];
}

/**
 * The colour and camera rows: each only when the probe knows the fact, plus "Detected: <Profile> (likely)" when the
 * server has a camera-profile hint. Kept apart from `metadataRows` (the eight technical rows) so those stay fixed.
 */
export function colorCameraRows(item: MediaPoolItem | null | undefined, tc: T): MetadataRow[] {
  const probe = item?.probe;
  if (!probe) return [];
  const known = (v: string | null | undefined): v is string => typeof v === 'string' && v.trim().length > 0;
  const rows: MetadataRow[] = [];
  const add = (key: string, label: string, value: string | null | undefined) => {
    if (known(value)) rows.push({ key, label, value, source: 'probe' });
  };
  add('colorTransfer', tc('meta.transfer'), probe.colorTransfer);
  add('colorPrimaries', tc('meta.primaries'), probe.colorPrimaries);
  add('colorRange', tc('meta.range'), probe.colorRange);
  if (typeof probe.bitDepth === 'number' && probe.bitDepth > 0) rows.push({ key: 'bitDepth', label: tc('meta.bitDepth'), value: tc('meta.bits', { bits: probe.bitDepth }), source: 'probe' });
  add('make', tc('meta.make'), probe.make);
  add('model', tc('meta.model'), probe.model);
  const detected = cameraProfileName(probe.cameraProfileHint?.profile);
  if (detected) rows.push({ key: 'detected', label: tc('meta.detectedLabel'), value: tc('meta.detected', { profile: detected }), source: 'probe' });
  return rows;
}

/** True when the probe exists but every one of the six appended fields is null. */
export function hasNoAppendedProbeFields(item: MediaPoolItem | null | undefined): boolean {
  const probe = item && typeof item === 'object' ? item.probe : null;
  if (!probe) return false;
  return (
    probe.videoCodec === null &&
    probe.audioCodec === null &&
    probe.container === null &&
    probe.audioChannels === null &&
    probe.audioSampleRateHz === null &&
    probe.bitrateBps === null
  );
}

export interface MetadataPanelProps {
  /** The selected pool item, or `null` when nothing is selected. */
  item: MediaPoolItem | null;
}

export function MetadataPanel({ item }: MetadataPanelProps) {
  const t = useT('editor-media.metadata');
  const tc = useT('editor.cameraProfile');
  if (!item) {
    return (
      <Text size="sm" c="dimmed" data-testid="metadata-panel-empty">
        {t('empty')}
      </Text>
    );
  }

  // R05.8: a row with no value is not shown at all. The names of the hidden ones are listed once,
  // in plain words, so nothing is silently missing.
  const allRows = metadataRows(item, t);
  const rows = allRows.filter((row) => row.value !== NOT_PROBED && row.value !== NOT_ANALYSED);
  const hiddenLabels = allRows.filter((row) => row.value === NOT_PROBED || row.value === NOT_ANALYSED).map((row) => row.label);
  const neverAnalysed = item.probe === null;
  const appendedFieldsAbsent = hasNoAppendedProbeFields(item);

  return (
    <EditorPanel title={item.fileName} testId="metadata-panel" actions={<Text size="xs" c="dimmed">{formatBytes(item.sizeBytes)}</Text>}>
      <Stack gap="xs">
      {neverAnalysed ? (
        <Text size="xs" c="dimmed" data-testid="metadata-not-analysed">
          {t.rich('notAnalysed', { state: NOT_ANALYSED, b: (c) => <b>{c}</b> })}
        </Text>
      ) : null}

      {appendedFieldsAbsent ? (
        <Text size="xs" c="dimmed" data-testid="metadata-pre-probe-artifact">
          {t('preProbe', { state: NOT_PROBED })}
        </Text>
      ) : null}

      <Box>
        {rows.map((row) => (
          <Group
            key={row.key}
            justify="space-between"
            gap="sm"
            wrap="nowrap"
            data-testid={`metadata-row-${row.key}`}
            data-source={row.source}
          >
            <Text size="xs" c="dimmed">
              {row.label}
            </Text>
            <Text
              size="xs"
              ta="right"
              data-testid={`metadata-value-${row.key}`}
              data-source={row.source}
            >
              {row.value}
            </Text>
          </Group>
        ))}
      </Box>

      {colorCameraRows(item, tc).length > 0 ? (
        <Box data-testid="metadata-colour-rows">
          {colorCameraRows(item, tc).map((row) => (
            <Group key={row.key} justify="space-between" gap="sm" wrap="nowrap" data-testid={`metadata-row-${row.key}`} data-source={row.source}>
              <Text size="xs" c="dimmed">
                {row.label}
              </Text>
              <Text size="xs" ta="right" data-testid={`metadata-value-${row.key}`} data-source={row.source}>
                {row.value}
              </Text>
            </Group>
          ))}
        </Box>
      ) : null}

      {hiddenLabels.length > 0 ? (
        <Text size="xs" c="dimmed" data-testid="metadata-gap-note">
          {t('gapNote', { labels: hiddenLabels.join(', ') })}
        </Text>
      ) : null}

      <Text size="xs" c="dimmed">
        <Code>{item.kind}</Code>
      </Text>
      </Stack>
    </EditorPanel>
  );
}
