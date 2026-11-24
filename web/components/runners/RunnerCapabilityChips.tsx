'use client';

import { Badge, Group, Text, Tooltip } from '@mantine/core';
import { IconCircleFilled } from '@tabler/icons-react';
import {
  capabilityChips,
  parseRunnerCapabilities,
  runnerPresence,
  type CapabilityChip,
} from '@/lib/utils/runners';
import type { RunnerDevice } from '@/lib/types/runner';
import { useT } from '@/i18n';

/**
 * What a device says it can do (E7-UI). Every chip is rendered from the capabilities blob the
 * gateway relays verbatim from the runner's own `hello`, which is self-reported and untrusted —
 * so the tooltips say 'this machine says', never 'this machine is'.
 *
 * The shape of the story is deliberate: 'Remotion' vs 'ffmpeg only' answers the one question a
 * user has (will my renders run here?), the container chip shows the precondition that answer
 * rests on, and slots/ffmpeg ride along as secondary facts.
 */
export function RunnerCapabilityChips({ device }: { device: RunnerDevice }) {
  const tr = useT();
  const t = useT('runners.chips');
  const caps = parseRunnerCapabilities(device.capabilities);
  const chips = capabilityChips(caps, tr);

  if (chips.length === 0) {
    return (
      <Tooltip label={t('notConnected')}>
        <Text size="xs" c="dimmed">
          {t('notReported')}
        </Text>
      </Tooltip>
    );
  }

  return (
    <Group gap={4} wrap="wrap">
      {chips.map((chip: CapabilityChip) => (
        <Tooltip key={chip.label} label={chip.tooltip} multiline maw={320}>
          <Badge color={chip.color} variant="light" size="sm">
            {chip.label}
          </Badge>
        </Tooltip>
      ))}
    </Group>
  );
}

/**
 * The online indicator. Green means a gateway recorded a heartbeat within the last few minutes;
 * grey means it did not. It is an inference from `last_seen_at`, not a live socket check — the
 * authoritative 'is a runner online?' answer is a per-replica question the engine asks the
 * gateway, and the dashboard cannot reach it — so the label says 'heard from', not 'connected'.
 */
export function RunnerPresenceDot({ lastSeenAt }: { lastSeenAt?: string | null }) {
  const t2 = useT('runners.presence');
  const presence = runnerPresence(lastSeenAt);

  if (presence === 'never') {
    return (
      <Tooltip label={t2('never')}>
        <IconCircleFilled size={10} color="var(--mantine-color-gray-4)" />
      </Tooltip>
    );
  }

  const online = presence === 'online';
  return (
    <Tooltip
      label={
        online
          ? t2('online')
          : t2('offline')
      }
    >
      <IconCircleFilled
        size={10}
        color={online ? 'var(--mantine-color-green-6)' : 'var(--mantine-color-gray-4)'}
        aria-label={online ? t2('onlineAria') : t2('offlineAria')}
      />
    </Tooltip>
  );
}
