'use client';

import { useEffect, useMemo } from 'react';
import { Group, Stack, Text } from '@mantine/core';
import { loadBrandFont, resolveBrandFont } from '../../lib/design-system/brand-fonts';
import {
  TYPE_SPECIMEN_GENERIC,
  TYPE_SPECIMEN_SAMPLE,
  TYPE_SPECIMEN_UNAVAILABLE,
  TYPE_SPECIMEN_WEIGHT_LABEL,
} from './copy';
import { useT } from '@/i18n';

/**
 * One type style: the family, its weight and the sample sentence set in that family and weight.
 * A bundled family is loaded lazily on mount (self-hosted); anything else says so plainly.
 */
export function TypeSpecimen({
  family,
  weight,
  compact = false,
}: {
  family: string;
  weight?: number | null;
  /** Sample line and note only (the family is already named next to it, as in a token row or a diff). */
  compact?: boolean;
}) {
  const tr = useT();
  const resolution = useMemo(() => resolveBrandFont(family), [family]);
  const availableFamily = resolution.kind === 'available' ? resolution.family : null;

  useEffect(() => {
    if (availableFamily) void loadBrandFont(availableFamily);
  }, [availableFamily]);

  let fontFamily: string;
  let note: string | null = null;
  if (resolution.kind === 'available') {
    fontFamily = `'${resolution.cssFamily}', sans-serif`;
  } else if (resolution.kind === 'generic') {
    fontFamily = resolution.keyword;
    note = tr.msg(TYPE_SPECIMEN_GENERIC);
  } else {
    fontFamily = 'sans-serif';
    note = tr.msg(TYPE_SPECIMEN_UNAVAILABLE);
  }

  return (
    <Stack gap={4}>
      {!compact && (
        <Group gap="xs">
          <Text size="sm" fw={600}>{family}</Text>
          {typeof weight === 'number' && (
            <Text size="xs" c="dimmed">{tr.msg(TYPE_SPECIMEN_WEIGHT_LABEL)} {weight}</Text>
          )}
        </Group>
      )}
      <Text
        size={compact ? 'sm' : 'lg'}
        data-testid="type-specimen-sample"
        data-font-status={resolution.kind}
        style={{ fontFamily, fontWeight: typeof weight === 'number' ? weight : undefined }}
      >
        {tr.msg(TYPE_SPECIMEN_SAMPLE)}
      </Text>
      {note && <Text size="xs" c="dimmed" data-testid="type-specimen-note">{note}</Text>}
    </Stack>
  );
}
