'use client';

import React, { useState } from 'react';
import { Badge, Button, Group, Select, Stack, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import type { MediaProbe } from '@/lib/editing/media/types';
import { isCameraProfileId, type CameraProfileId } from '@/lib/editing/color/camera-profiles';
import { clearAutoApplied, wasAutoApplied } from '@/lib/editing/color/auto-profile';
import { applyToAllFromFileOps, cameraProfileGroups, cameraProfileName, cameraProfileStatus, clipHasLut } from '@/lib/editing/color/camera-profile-ops';
import { hdrKindOfProbe } from '@/lib/editing/media/hdr';
import { useT } from '@/i18n';

const NONE = 'none';

/** "Not this one" and "Turn off" for the open session: a dismissed suggestion does not come back until the page reloads. */
const dismissedSuggestions = new Set<string>();

/** Test seam: forget this session's dismissals. */
export function resetDismissedSuggestions(): void {
  dismissedSuggestions.clear();
}

export interface CameraProfileRowProps {
  clip: MediaClip;
  readOnly: boolean;
  /** One undo step. */
  onSet: (profile: CameraProfileId | null) => void;
  /** Taller touch targets for the phone. */
  touch?: boolean;
  testIdPrefix?: string;
}

/** The select, its honesty note, and the badge with "Turn off". */
export function CameraProfileRow({ clip, readOnly, onSet, touch = false, testIdPrefix = 'camera-profile' }: CameraProfileRowProps) {
  const tx = useT('editor.cameraProfile');
  const profile = clip.inputProfile ?? null;
  const name = cameraProfileName(profile);
  const status = cameraProfileStatus(profile);
  const data = [
    { value: NONE, label: tx('none') },
    ...cameraProfileGroups().map((g) => ({ group: g.maker, items: g.items.map((p) => ({ value: p.id, label: p.displayName })) })),
  ];
  const auto = profile !== null && wasAutoApplied(clip.id);
  const change = (next: CameraProfileId | null) => {
    if (next === null) {
      clearAutoApplied(clip.id);
      dismissedSuggestions.add(clip.id);
    } else if (next !== profile) clearAutoApplied(clip.id);
    onSet(next);
  };
  return (
    <Stack gap={6} data-testid={`${testIdPrefix}-row`}>
      <Select
        size={touch ? 'md' : 'xs'}
        // Mantine's `md` is 42 px; the phone surface's touch rule is 44 px, the same as every other sheet control.
        styles={touch ? { input: { height: 44, minHeight: 44 } } : undefined}
        label={tx('label')}
        data={data}
        value={profile ?? NONE}
        allowDeselect={false}
        disabled={readOnly}
        comboboxProps={{ withinPortal: true }}
        onChange={(v) => change(v && isCameraProfileId(v) ? v : null)}
        data-testid={`${testIdPrefix}-select`}
      />
      {status === 'uncalibrated' && (
        <Text size="xs" c="dimmed" data-testid={`${testIdPrefix}-note`}>
          {tx('uncalibratedNote')}
        </Text>
      )}
      {status === 'approximate' && (
        <Text size="xs" c="dimmed" data-testid={`${testIdPrefix}-note`}>
          {tx('approximateNote')}
        </Text>
      )}
      {name && (
        <Stack gap={4} data-testid={`${testIdPrefix}-badge`} data-auto={auto ? 'true' : 'false'}>
          <Group gap={6} wrap="wrap" align="center">
            <Badge size="sm" fz="xs" variant="light" color="teal" c="var(--mantine-color-text)" style={{ textTransform: 'none' }}>
              {auto ? tx('autoBadge', { profile: name }) : tx('setBadge', { profile: name })}
            </Badge>
            <Button size={touch ? 'md' : 'xs'} h={touch ? 44 : undefined} variant="default" disabled={readOnly} onClick={() => change(null)} data-testid={`${testIdPrefix}-turn-off`}>
              {tx('turnOff')}
            </Button>
          </Group>
          {auto && (
            <Text size="xs" c="dimmed" data-testid={`${testIdPrefix}-auto-note`}>
              {tx('autoAppliedNote', { profile: name })}
            </Text>
          )}
          {auto && (
            <Text size="xs" c="dimmed">
              {tx('wrong')}
            </Text>
          )}
        </Stack>
      )}
    </Stack>
  );
}

export interface CameraProfileControlProps {
  clip: MediaClip;
  doc: EditTimeline;
  readOnly: boolean;
  probe: MediaProbe | null;
  onSet: (profile: CameraProfileId | null, label: string) => void;
  onBatch: (ops: EditOp[], label: string) => void;
}

/** The full picture-section control: row, suggestion chip, "apply to all", and the double-conversion hint. */
export function CameraProfileControl({ clip, doc, readOnly, probe, onSet, onBatch }: CameraProfileControlProps) {
  const tx = useT('editor.cameraProfile');
  const [, bump] = useState(0);
  const profile = clip.inputProfile ?? null;
  // HDR wins: the render tone-maps it and ignores a profile, so do not offer one (the Inspector already gates this;
  // doing it here hides the control on the Colour page's Input strip too).
  if (hdrKindOfProbe(probe) !== null) return null;
  const hint = probe?.cameraProfileHint;
  const suggested = hint && hint.confidence === 'medium' && hint.profile && isCameraProfileId(hint.profile) ? hint.profile : null;
  const showChip = suggested !== null && profile === null && !dismissedSuggestions.has(clip.id);
  const allOps = applyToAllFromFileOps(doc, clip, profile);
  return (
    <Stack gap={6} data-testid="camera-profile-control">
      <CameraProfileRow clip={clip} readOnly={readOnly} onSet={(p) => onSet(p, p ? tx('setLabel') : tx('turnOffLabel'))} />
      {showChip && suggested && (
        <Stack gap={4} data-testid="camera-profile-chip">
          <Text size="xs">{tx('chip', { profile: cameraProfileName(suggested) ?? '' })}</Text>
          <Group gap={6}>
            <Button size="xs" fz="xs" disabled={readOnly} onClick={() => onSet(suggested, tx('setLabel'))} data-testid="camera-profile-chip-convert">
              {tx('convert')}
            </Button>
            <Button
              size="xs"
              variant="default"
              onClick={() => {
                dismissedSuggestions.add(clip.id);
                bump((n) => n + 1);
              }}
              data-testid="camera-profile-chip-dismiss"
            >
              {tx('notThisOne')}
            </Button>
          </Group>
        </Stack>
      )}
      <Button size="xs" fz="xs" variant="default" disabled={readOnly || allOps.length === 0} onClick={() => onBatch(allOps, tx('applyAllLabel'))} data-testid="camera-profile-apply-all">
        {tx('applyAll')}
      </Button>
      {profile !== null && clipHasLut(clip) && (
        <Text size="xs" c="dimmed" data-testid="camera-profile-double-hint">
          {tx('doubleHint')}
        </Text>
      )}
    </Stack>
  );
}
