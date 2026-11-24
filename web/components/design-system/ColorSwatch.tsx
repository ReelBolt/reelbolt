'use client';

import { Badge, Group, Stack, Text } from '@mantine/core';
import { confidenceBand } from '@/lib/types/design-system';
import { BAND_LABELS } from './copy';
import mobile from './mobile.module.css';
import { useT } from '@/i18n';

/**
 * The only colour shapes a swatch will paint. The value comes from a stored document, so it is
 * checked here rather than trusted: anything else (a `url(...)`, a keyword, a half-typed hex)
 * renders the neutral swatch and can never reach a style attribute or trigger a fetch.
 */
const SAFE_HEX = /^#[0-9a-f]{6}([0-9a-f]{2})?$/i;

/** The dot colour per band. The band is also spelled out in the badge, so colour is never the only signal. */
function bandDot(band: 'high' | 'medium' | 'low'): string {
  if (band === 'high') return 'var(--mantine-color-green-7)';
  if (band === 'medium') return 'var(--mantine-color-yellow-7)';
  return 'var(--mantine-color-orange-7)';
}

/**
 * One brand colour: the swatch, the hex text beside it (the colour is never the only signal) and an
 * optional confidence band. The hex is shown exactly as the document stores it (lowercase).
 */
export function ColorSwatch({ name, hex, conf }: { name: string; hex: string; conf?: number | null }) {
  const tr = useT();
  const band = confidenceBand(conf ?? null);
  const safe = SAFE_HEX.test(hex);
  return (
    <Group gap="xs" wrap="nowrap" align="flex-start" className={mobile.breakText}>
      <span
        aria-hidden
        data-neutral={safe ? undefined : 'true'}
        style={{
          width: 28,
          height: 28,
          flex: '0 0 auto',
          borderRadius: 4,
          background: safe ? hex : 'var(--mantine-color-gray-1)',
          border: '1px solid var(--mantine-color-gray-4)',
        }}
      />
      <Stack gap={2} className={mobile.breakText}>
        <Text size="sm" fw={600}>{name}</Text>
        <Text size="xs" ff="monospace">{hex}</Text>
        {band && (
          // Gray light keeps the label text at a safe contrast; the dot carries the colour.
          <Badge
            size="xs"
            variant="light"
            color="gray"
            leftSection={
              <span
                aria-hidden
                style={{ width: 6, height: 6, borderRadius: '50%', background: bandDot(band) }}
              />
            }
          >
            {tr.msg(BAND_LABELS[band])}
          </Badge>
        )}
      </Stack>
    </Group>
  );
}
