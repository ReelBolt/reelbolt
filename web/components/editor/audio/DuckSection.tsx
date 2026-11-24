'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Badge, Button, Group, MultiSelect, NumberInput, Select, Stack, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { compileDuck, compileRemoveDuck, defaultDuckConfig, duckIsStale, guessDuckTracks, loadDuckConfig, resolveDuck, saveDuckConfig, type DuckConfig } from '@/lib/editing/audio/duck-state';
import { useT } from '@/i18n';
import classes from './audio.module.css';

export interface DuckSectionProps {
  doc: EditTimeline;
  readOnly: boolean;
  advanced: boolean;
  onApply: (ops: readonly EditOp[], label: string) => void;
}

/**
 * R08.5: music ducks under the voice. "Duck music under voice" writes the music track's fader
 * keyframes from where the voice clips play and turns "Ducking: on"; while it is on the keyframes
 * follow the voice clips when they move (one undo step per update), Update forces it, Remove
 * restores a flat fader and turns it off.
 */
export function DuckSection({ doc, readOnly, advanced, onApply }: DuckSectionProps) {
  const t = useT('editor-audio.duck');
  const audioTracks = useMemo(() => doc.tracks.filter((t) => t.kind === 'audio'), [doc.tracks]);
  const [saved] = useState(() => loadDuckConfig(doc.id));
  const [config, setConfig] = useState<DuckConfig | null>(saved);
  const [pickedMusic, setMusic] = useState<string | null | undefined>(saved?.musicTrackId);
  const [pickedVoice, setVoice] = useState<string[] | undefined>(saved?.dialogueTrackIds);
  const [depth, setDepth] = useState(saved ? Math.abs(saved.depthDb) : 12);
  const [attack, setAttack] = useState(saved?.attackFrames ?? 8);
  const [release, setRelease] = useState(saved?.releaseFrames ?? 20);

  // Until the person chooses, music and voice come from the track names.
  const guess = useMemo(() => guessDuckTracks(doc.tracks), [doc.tracks]);
  const music = pickedMusic === undefined ? guess.music : pickedMusic;
  const voice = pickedVoice ?? guess.voice;

  const live = resolveDuck(doc, config);
  const stale = live ? duckIsStale(doc, live) : false;

  // While on: follow the voice clips. A curve identical to the stored one is never rewritten, so this settles.
  const lastApplied = useRef<string>('');
  useEffect(() => {
    if (!live || readOnly || !stale) return;
    const op = compileDuck(doc, live);
    const sig = JSON.stringify(op);
    if (lastApplied.current === sig) return;
    lastApplied.current = sig;
    onApply([op], t('updateLabel'));
  }, [doc, live, stale, readOnly, onApply, t]);
  useEffect(() => {
    if (!stale) lastApplied.current = '';
  }, [stale]);

  const options = audioTracks.map((t) => ({ value: t.id, label: t.name }));
  const ready = Boolean(music) && voice.length > 0 && !voice.includes(music ?? '');
  const nameOf = (id: string) => audioTracks.find((t) => t.id === id)?.name ?? t('musicFallback');

  const turnOn = () => {
    if (!music) return;
    const next: DuckConfig = { ...defaultDuckConfig(music, voice), depthDb: -Math.abs(depth), attackFrames: attack, releaseFrames: release };
    saveDuckConfig(doc.id, next);
    setConfig(next);
    const op = compileDuck(doc, next);
    lastApplied.current = JSON.stringify(op);
    onApply([op], t('duckLabel'));
  };
  const turnOff = () => {
    if (!live) return;
    onApply([compileRemoveDuck(doc, live)], t('removeLabel'));
    saveDuckConfig(doc.id, null);
    setConfig(null);
  };

  return (
    <Stack gap={6} data-testid="ducking-controls">
      <Group gap={6} justify="space-between">
        <Text size="xs" c="dimmed">
          {t('hint')}
        </Text>
        {live ? (
          <Badge size="sm" fz="xs" color="teal" variant="light" data-testid="ducking-state" data-on="true">
            {t('on', { track: nameOf(live.musicTrackId) })}
          </Badge>
        ) : (
          <Badge size="sm" fz="xs" color="gray" variant="light" data-testid="ducking-state" data-on="false">
            {t('off')}
          </Badge>
        )}
      </Group>
      <Group grow align="flex-start">
        <Select size="xs" label={t('musicTrack')} data={options} value={music} onChange={setMusic} disabled={readOnly} data-testid="ducking-music" classNames={{ input: classes.touchInput }} />
        <MultiSelect size="xs" label={t('voiceTracks')} data={options.filter((o) => o.value !== music)} value={voice} onChange={setVoice} disabled={readOnly} data-testid="ducking-dialogue" />
      </Group>
      <NumberInput size="xs" label={t('depth')} value={depth} min={1} max={60} step={1} clampBehavior="strict" disabled={readOnly} onChange={(v) => typeof v === 'number' && setDepth(v)} data-testid="ducking-depth" classNames={{ input: classes.touchInput }} />
      {advanced && (
        <Group grow>
          <NumberInput size="xs" label={t('attack')} value={attack} min={0} max={120} disabled={readOnly} onChange={(v) => typeof v === 'number' && setAttack(v)} data-testid="ducking-attack" />
          <NumberInput size="xs" label={t('release')} value={release} min={0} max={240} disabled={readOnly} onChange={(v) => typeof v === 'number' && setRelease(v)} data-testid="ducking-release" />
        </Group>
      )}
      <Group gap={6}>
        <Button size="compact-xs" fz="xs" className={classes.touch} disabled={readOnly || !ready} onClick={turnOn} data-testid="ducking-derive">
          {live ? t('duckAgain') : t('duckLabel')}
        </Button>
        {live && (
          <>
            <Button size="compact-xs" fz="xs" className={classes.touch} variant="light" disabled={readOnly || !stale} onClick={() => onApply([compileDuck(doc, live)], t('updateLabel'))} data-testid="ducking-update">
              {stale ? t('updateLabel') : t('upToDate')}
            </Button>
            <Button size="compact-xs" fz="xs" className={classes.touch} variant="subtle" color="red" disabled={readOnly} onClick={turnOff} data-testid="ducking-remove">
              {t('removeLabel')}
            </Button>
          </>
        )}
      </Group>
      {!ready && !readOnly && (
        <Text size="xs" c="dimmed">
          {t('pickTracks')}
        </Text>
      )}
    </Stack>
  );
}
