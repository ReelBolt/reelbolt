'use client';

import React from 'react';
import { Switch } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import type { Track } from '@/lib/editing/types';
import { useT } from '@/i18n';

/** The one-switch voice treatment: FFT denoise at 0.6 plus a speech leveler at -18 dB. */
export const CLEAN_VOICE = { denoise: { kind: 'fft', strength: 0.6 }, leveler: { kind: 'speech', targetDb: -18 } } as const;

export const isCleanVoice = (track: Track): boolean => Boolean(track.denoise) && Boolean(track.leveler);

export function compileCleanVoice(track: Track, on: boolean): EditOp[] {
  return [
    { type: 'setTrackProperty', trackId: track.id, key: 'denoise', value: on ? { ...CLEAN_VOICE.denoise } : null },
    { type: 'setTrackProperty', trackId: track.id, key: 'leveler', value: on ? { ...CLEAN_VOICE.leveler } : null },
  ];
}

export function CleanVoiceSwitch({ track, disabled, onApply }: { track: Track; disabled: boolean; onApply: (ops: readonly EditOp[], label: string) => void }) {
  const t = useT('editor-audio.cleanVoice');
  return (
    <Switch
      size="xs"
      label={t('label')}
      description={t('description')}
      styles={{ description: { fontSize: 12 } }}
      checked={isCleanVoice(track)}
      disabled={disabled}
      onChange={(e) => onApply(compileCleanVoice(track, e.currentTarget.checked), t(e.currentTarget.checked ? 'historyOn' : 'historyOff', { track: track.name }))}
      data-testid={`clean-voice-${track.id}`}
    />
  );
}
