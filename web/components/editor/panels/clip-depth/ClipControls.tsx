'use client';

import React from 'react';
import { Box, Button, Group, Select, Slider, Stack, Switch, Text } from '@mantine/core';
import {
  SPEED_CHIPS,
  autoFadeFrames,
  checkFadeSeconds,
  checkOpacityPercent,
  checkVolumePercent,
  dbToVolumePercent,
  formatSeconds,
  framesToSeconds,
  isMutedDb,
  opacityToPercent,
  planSpeed,
  speedReadout,
  type SpeedPlan,
} from '@/lib/editing/clip-depth';
import { BLEND_MODES } from '@/lib/editing/engine/ops';
import type { BlendMode, MediaClip, Rational } from '@/lib/editing/types';
import { CommitNumberInput } from '../../shell/CommitNumberInput';
import { useRefusalToast } from '../../shell/refusal-toast';
import classes from './clip-depth.module.css';
import { useT } from '@/i18n';

/** Shared across the rows: a field's remount key and the revert-on-blur that puts the stored value back. */
interface FieldProps {
  fieldKey: (field: string) => string;
  revertOnBlur: (field: string) => () => void;
  readOnly: boolean;
}

export function ResetButton({ onClick, disabled, testId }: { onClick: () => void; disabled?: boolean; testId: string }) {
  const t = useT('editor.panels.clip-depth.clipControls');
  return (
    <Button size="compact-xs" fz="xs" variant="subtle" className={classes.chip} disabled={disabled} onClick={onClick} data-testid={testId}>
      {t('reset')}
    </Button>
  );
}

/** Opacity as a percent: a field and a slider. `current` is the 0..1 value at the playhead (keyed or static). */
export function OpacityControl({ current, onApply, fieldKey, revertOnBlur, readOnly }: FieldProps & { current: number; onApply: (opacity: number) => void }) {
  const t = useT('editor.panels.clip-depth.clipControls');
  const { showRefusalMessage } = useRefusalToast();
  const percent = opacityToPercent(current);
  const commit = (value: number) => {
    const checked = checkOpacityPercent(value);
    if (!checked.ok) {
      showRefusalMessage(checked.sentence);
      return;
    }
    if (checked.value !== current) onApply(checked.value);
  };
  return (
    <Stack gap={2}>
      <CommitNumberInput
        key={fieldKey('opacity')}
        size="xs"
        label={t('opacity')}
        value={percent}
        step={5}
        min={0}
        max={100}
        decimalScale={0}
        disabled={readOnly}
        onCommit={commit}
        onBlur={revertOnBlur('opacity')}
        data-testid="inspector-opacity"
      />
      <Slider size="sm" min={0} max={100} step={1} value={percent} onChangeEnd={commit} disabled={readOnly} label={(v) => `${v}%`} thumbLabel="Opacity" data-testid="inspector-opacity-slider" />
    </Stack>
  );
}

/** How the clip's picture blends with the pictures below it (video tracks only). Normal removes the field. */
export function BlendControl({ current, onApply, readOnly }: { current: BlendMode | undefined; onApply: (mode: BlendMode) => void; readOnly: boolean }) {
  const t = useT('editor.panels.clip-depth.clipControls');
  const labels: Record<BlendMode, string> = {
    normal: t('blendNormal'),
    multiply: t('blendMultiply'),
    screen: t('blendScreen'),
    overlay: t('blendOverlay'),
    softLight: t('blendSoftLight'),
    hardLight: t('blendHardLight'),
    darken: t('blendDarken'),
    lighten: t('blendLighten'),
    add: t('blendAdd'),
    difference: t('blendDifference'),
  };
  return (
    <Select
      size="xs"
      label={t('blend')}
      data={(BLEND_MODES as readonly BlendMode[]).map((word) => ({ value: word, label: labels[word] }))}
      value={current ?? 'normal'}
      allowDeselect={false}
      disabled={readOnly}
      onChange={(v) => v && onApply(v as BlendMode)}
      data-testid="inspector-blend-mode"
    />
  );
}

/** Fades in seconds, an Auto chip, and the room the clip has left. */
export function FadeControls({ clip, rate, onApply, fieldKey, revertOnBlur, readOnly }: FieldProps & { clip: MediaClip; rate: Rational; onApply: (which: 'in' | 'out', frames: number) => void }) {
  const t = useT('editor.panels.clip-depth.clipControls');
  const { showRefusalMessage } = useRefusalToast();
  const duration = clip.recordOut - clip.recordIn;
  const fadeIn = clip.fadeInFrames ?? 0;
  const fadeOut = clip.fadeOutFrames ?? 0;
  const field = (which: 'in' | 'out') => {
    const own = which === 'in' ? fadeIn : fadeOut;
    const other = which === 'in' ? fadeOut : fadeIn;
    return (
      <CommitNumberInput
        key={fieldKey(`fade-${which}`)}
        onBlur={revertOnBlur(`fade-${which}`)}
        size="xs"
        label={which === 'in' ? t('fadeInSeconds') : t('fadeOutSeconds')}
        value={framesToSeconds(own, rate)}
        min={0}
        step={0.1}
        decimalScale={2}
        disabled={readOnly}
        onCommit={(n) => {
          const checked = checkFadeSeconds(which, n, rate, duration, other);
          if (!checked.ok) {
            showRefusalMessage(checked.sentence);
            return;
          }
          if (checked.value !== own) onApply(which, checked.value);
        }}
        data-testid={`inspector-fade-${which}`}
      />
    );
  };
  return (
    <Stack gap={4}>
      <Group grow align="flex-end">
        {field('in') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
        {field('out') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
      </Group>
      <Text size="12px" c="dimmed" data-testid="inspector-fade-room">
        {formatSeconds(framesToSeconds(Math.max(0, duration - fadeIn - fadeOut), rate))} {t('ofTheClipIs')}
      </Text>
    </Stack>
  );
}

/** Auto fade chip: a short fade at both ends. */
export function AutoFadeButton({ clip, rate, onApply, readOnly }: { clip: MediaClip; rate: Rational; onApply: (frames: number) => void; readOnly: boolean }) {
  const t = useT('editor.panels.clip-depth.clipControls');
  const frames = autoFadeFrames(rate, clip.recordOut - clip.recordIn);
  return (
    <Button size="compact-xs" fz="xs" variant="light" className={classes.chip} disabled={readOnly || clip.recordOut - clip.recordIn < 4} onClick={() => onApply(frames)} data-testid="inspector-auto-fade">
      {t('autoFade')}
    </Button>
  );
}

/** Speed chips, the stepper, and "Clip is now N s". */
export function SpeedControl({ clip, rate, onApply, fieldKey, revertOnBlur, readOnly }: FieldProps & { clip: MediaClip; rate: Rational; onApply: (plan: SpeedPlan) => void }) {
  const t = useT('editor.panels.clip-depth.clipControls');
  const { showRefusalMessage } = useRefusalToast();
  const current = clip.playbackRate ?? 1;
  const apply = (speed: number) => {
    const plan = planSpeed(speed, clip);
    if (!plan.ok) {
      showRefusalMessage(plan.sentence);
      return;
    }
    if (speed !== current) onApply(plan.value);
  };
  return (
    <Stack gap={4}>
      <Group gap={4} role="group" aria-label={t('speed')} data-testid="inspector-speed-chips">
        {SPEED_CHIPS.map((s) => (
          <Button key={s} size="compact-sm" fz="xs" variant={current === s ? 'filled' : 'light'} className={classes.chip} disabled={readOnly} aria-pressed={current === s} onClick={() => apply(s)} data-testid={`inspector-speed-chip-${s}`}>
            {t('speedValue', { speed: s })}
          </Button>
        ))}
      </Group>
      <CommitNumberInput key={fieldKey('speed')} size="xs" label={t('speedX')} value={current} min={0.1} max={10} step={0.25} decimalScale={2} disabled={readOnly} onCommit={apply} onBlur={revertOnBlur('speed')} data-testid="inspector-speed" />
      <Text size="12px" c="dimmed" data-testid="inspector-speed-readout">
        {t('speedNote', { readout: speedReadout(current, { ...clip, playbackRate: current }, rate) })}
      </Text>
    </Stack>
  );
}

/** Volume as a percent with a mute switch. `gainDb` is the clip's static gain. */
export function VolumeControl({ gainDb, onApply, onMute, fieldKey, revertOnBlur, readOnly }: FieldProps & { gainDb: number; onApply: (db: number) => void; onMute: (muted: boolean) => void }) {
  const t = useT('editor.panels.clip-depth.clipControls');
  const { showRefusalMessage } = useRefusalToast();
  const percent = dbToVolumePercent(gainDb);
  const muted = isMutedDb(gainDb);
  const commit = (value: number) => {
    const checked = checkVolumePercent(value);
    if (!checked.ok) {
      showRefusalMessage(checked.sentence);
      return;
    }
    if (checked.value !== gainDb) onApply(checked.value);
  };
  return (
    <Stack gap={2}>
      <Group align="flex-end" wrap="nowrap">
        <CommitNumberInput key={fieldKey('volume')} size="xs" label={t('volume')} value={percent} min={0} max={200} step={5} decimalScale={0} disabled={readOnly} onCommit={commit} onBlur={revertOnBlur('volume')} style={{ flex: 1 }} data-testid="inspector-volume" />
        <Switch size="md" label={t('mute')} checked={muted} disabled={readOnly} onChange={(e) => onMute(e.currentTarget.checked)} data-testid="inspector-mute" />
      </Group>
      <Box>
        <Slider size="sm" min={0} max={200} step={1} value={percent} onChangeEnd={commit} disabled={readOnly} label={(v) => `${v}%`} thumbLabel="Volume" data-testid="inspector-volume-slider" />
      </Box>
    </Stack>
  );
}
