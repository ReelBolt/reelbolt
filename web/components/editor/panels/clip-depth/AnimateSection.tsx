'use client';

import React, { useState } from 'react';
import { Badge, Box, Button, Collapse, Group, SegmentedControl, Slider, Stack, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import { MOTION_PRESETS, canAnimate, clearMotionOps, isAnimated, motionOps, presetInfo, type MotionPresetId, type MotionTiming } from '@/lib/editing/clip-depth';
import type { MediaClip, Rational } from '@/lib/editing/types';
import { CommitNumberInput } from '../../shell/CommitNumberInput';
import { MotionThumb } from './MotionThumb';
import classes from './clip-depth.module.css';
import { useT } from '@/i18n';

export interface AnimateSectionProps {
  clip: MediaClip;
  rate: Rational;
  readOnly: boolean;
  /** Applies ops as ONE undo step. */
  onBatch: (ops: EditOp[], label: string) => void;
  /** Moves the playhead to the start of the clip so the viewer shows the motion. */
  onSeek?: (frame: number) => void;
  /** The expert keyframe rows (diamond, key count, lane); shown under "Fine-tune keyframes". */
  fineTune: React.ReactNode;
}

/**
 * R07.1: the "Animate" front door. One tap on a preset writes the keyframes as one undo step; strength,
 * length and entrance/exit settle it. The diamond, the keyframe lane and easing sit under "Fine-tune keyframes".
 */
export function AnimateSection({ clip, rate, readOnly, onBatch, onSeek, fineTune }: AnimateSectionProps) {
  const t = useT('editor.panels.clip-depth.animateSection');
  const [active, setActive] = useState<MotionPresetId | null>(null);
  const [strength, setStrength] = useState<number | null>(null);
  const [seconds, setSeconds] = useState<number | null>(null);
  const [timing, setTiming] = useState<MotionTiming>('start');
  const [fineOpen, setFineOpen] = useState(false);
  const animated = isAnimated(clip);
  const animatable = canAnimate(clip);

  const apply = (id: MotionPresetId, next: { strength?: number | null; seconds?: number | null; timing?: MotionTiming }) => {
    const info = presetInfo(id);
    const ops = motionOps(id, clip, rate, {
      strengthPercent: next.strength ?? undefined,
      seconds: next.seconds ?? undefined,
      timing: info.hasTiming ? (next.timing ?? timing) : undefined,
    });
    if (ops.length === 0) return;
    onBatch(ops, `Animate: ${info.label}`);
    onSeek?.(clip.recordIn);
  };

  const info = active ? presetInfo(active) : null;
  const cleared = (): void => {
    const ops = clearMotionOps(clip);
    if (ops.length > 0) onBatch(ops, t('removeAnimation2'));
    setActive(null);
  };

  return (
    <Stack gap={6} data-testid="inspector-animate">
      <Group justify="space-between" wrap="nowrap">
        <Text size="xs" c="dimmed">
          {t('pickAMotionOne')}
        </Text>
        {animated && (
          <Badge size="md" variant="light" color="blue" tt="none" data-testid="animate-state">
            {t('animated')}
          </Badge>
        )}
      </Group>
      <Box className={classes.grid} role="group" aria-label={t('motionPresets')}>
        {MOTION_PRESETS.map((preset) => (
          <button
            key={preset.id}
            type="button"
            className={classes.wordButton}
            data-active={active === preset.id}
            disabled={readOnly || !animatable}
            title={preset.hint}
            onClick={() => {
              setActive(preset.id);
              setStrength(null);
              setSeconds(null);
              apply(preset.id, { strength: null, seconds: null });
            }}
            data-testid={`animate-${preset.id}`}
          >
            <MotionThumb id={preset.id} />
            <span>{preset.label}</span>
          </button>
        ))}
      </Box>
      {!animatable && (
        <Text size="xs" c="dimmed">
          {t('thisClipIsToo')}
        </Text>
      )}
      {info && (
        <Stack gap={6} data-testid="animate-settings">
          <Text size="xs" c="dimmed">
            {t('pressPlayToWatch', { hint: info.hint })}
          </Text>
          {info.hasTiming && (
            <SegmentedControl
              size="xs"
              fullWidth
              value={timing}
              onChange={(v) => {
                setTiming(v as MotionTiming);
                apply(info.id, { strength, seconds, timing: v as MotionTiming });
              }}
              data={[
                { value: 'start', label: t('comesIn') },
                { value: 'end', label: t('goesOut') },
              ]}
              disabled={readOnly}
              data-testid="animate-timing"
            />
          )}
          {info.hasDuration && (
            <CommitNumberInput
              key={`${info.id}-seconds`}
              size="xs"
              label={t('howLongSeconds')}
              value={seconds ?? (info.id === 'zoomIn' || info.id === 'zoomOut' ? 1 : 0.6)}
              min={0.1}
              step={0.1}
              decimalScale={2}
              disabled={readOnly}
              onCommit={(n) => {
                if (!Number.isFinite(n) || n <= 0) return;
                setSeconds(n);
                apply(info.id, { strength, seconds: n });
              }}
              data-testid="animate-seconds"
            />
          )}
          {info.strength && (
            <Box>
              <Text size="xs">{info.strength.label}: {strength ?? info.strength.defaultPercent}%</Text>
              <Slider
                size="sm"
                min={info.strength.min}
                max={info.strength.max}
                step={5}
                value={strength ?? info.strength.defaultPercent}
                onChange={setStrength}
                onChangeEnd={(v) => apply(info.id, { strength: v, seconds })}
                disabled={readOnly}
                label={null}
                data-testid="animate-strength"
                thumbLabel={info.strength.label}
              />
            </Box>
          )}
        </Stack>
      )}
      {animated && (
        <Button size="compact-sm" fz="xs" variant="subtle" color="red" className={classes.chip} disabled={readOnly} onClick={cleared} data-testid="animate-remove">
          {t('removeAnimation')}
        </Button>
      )}
      <Button
        size="compact-sm"
        variant="subtle"
        className={classes.chip}
        onClick={() => setFineOpen((o) => !o)}
        aria-expanded={fineOpen}
        data-testid="inspector-finetune-toggle"
      >
        {fineOpen ? t('hideFineTuneKeyframes') : t('fineTuneKeyframes')}
      </Button>
      <Collapse expanded={fineOpen} data-testid="inspector-finetune">
        <Stack gap={4}>{fineTune}</Stack>
      </Collapse>
    </Stack>
  );
}
