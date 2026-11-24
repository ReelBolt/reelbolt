'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Box, Button, FileButton, Group, Select, Stack, Text } from '@mantine/core';
import { IconMicrophone, IconPlayerStopFilled, IconRefresh, IconUpload, IconX } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import {
  COUNT_IN,
  compilePlaceVoiceover,
  listAudioInputs,
  meterFill,
  micErrorSentence,
  recorderSupport,
  startVoTake,
  voFileName,
  type RecorderSupport,
  type VoStartOptions,
  type VoTake,
} from '@/lib/editing/audio/vo-recorder';
import { uploadFile } from '@/lib/api/files';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';
import classes from './audio.module.css';

export interface VoRecorderPanelProps {
  doc: EditTimeline;
  projectId: string;
  readOnly: boolean;
  playheadFrame: number;
  newId: () => string;
  onApply: (ops: readonly EditOp[], label: string) => void;
  /** Injectable for tests: the platform check and the recorder/uploader. */
  support?: RecorderSupport;
  start?: (mimeType: string, options?: VoStartOptions) => Promise<VoTake>;
  upload?: (projectId: string, file: File) => Promise<{ id: string }>;
  /** Milliseconds per count-in number (3-2-1); tests shorten it. */
  countInMs?: number;
  /** The length of an uploaded recording in seconds; defaults to reading the file's metadata. */
  measureFile?: (file: File) => Promise<number>;
  listDevices?: () => Promise<{ id: string; label: string }[]>;
  /** The phone sheet's variant: the unavailable sentence is the plain one and the upload picker says "voice file". */
  phone?: boolean;
}

type Phase =
  | { kind: 'idle' }
  | { kind: 'countIn'; n: number; take: VoTake; atFrame: number; retake: boolean }
  | { kind: 'recording'; atFrame: number; take: VoTake; retake: boolean }
  | { kind: 'uploading' }
  | { kind: 'error'; message: string };

/** Why an uploaded recording was refused before it was sent; the panel words it (`fileErrors.<code>`). */
export class VoFileError extends Error {
  constructor(readonly code: 'unreadable' | 'empty') {
    super(code);
    this.name = 'VoFileError';
  }
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Reads a recording's length from its metadata (the browser decodes the header, not the whole file). */
export function measureAudioFile(file: File): Promise<number> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const el = new Audio();
    const done = (fn: () => void) => {
      URL.revokeObjectURL(url);
      fn();
    };
    el.preload = 'metadata';
    el.onloadedmetadata = () => {
      // A recorded webm can report Infinity until it is seeked once.
      if (el.duration === Infinity) {
        el.currentTime = 1e9;
        el.ontimeupdate = () => {
          el.ontimeupdate = null;
          done(() => resolve(el.duration));
        };
        return;
      }
      done(() => resolve(el.duration));
    };
    el.onerror = () => done(() => reject(new VoFileError('unreadable')));
    el.src = url;
  });
}

/**
 * Browser voiceover (R08.7): pick the microphone, watch its level, record after a 3-2-1 count-in
 * onto a NEW audio track at the playhead (one undo step), and retake to replace that track in one
 * step. The take is uploaded as an ordinary project file. When the microphone is blocked or the
 * browser cannot record, the reason is stated in a sentence and an uploaded recording takes its place.
 */
export function VoRecorderPanel({
  doc,
  projectId,
  readOnly,
  playheadFrame,
  newId,
  onApply,
  support,
  start = startVoTake,
  upload = uploadFile,
  countInMs = 1000,
  measureFile = measureAudioFile,
  listDevices = listAudioInputs,
  phone = false,
}: VoRecorderPanelProps) {
  const t = useT('editor-audio.vo');
  const platform = useMemo(() => support ?? recorderSupport(), [support]);
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const [level, setLevel] = useState(0);
  const [devices, setDevices] = useState<{ id: string; label: string }[]>([]);
  const [deviceId, setDeviceId] = useState<string | null>(null);
  const [last, setLast] = useState<{ trackId: string; itemId: string; atFrame: number } | null>(null);
  const [micProblem, setMicProblem] = useState(false);
  const [limitNote, setLimitNote] = useState<string | null>(null);
  const finishRef = useRef<() => Promise<void>>(async () => undefined);
  const docRef = useRef(doc);
  const cancelled = useRef(false);
  useEffect(() => {
    docRef.current = doc;
  }, [doc]);
  const fps = doc.settings.rate.den > 0 ? doc.settings.rate.num / doc.settings.rate.den : 30;

  const refreshDevices = useCallback(() => {
    void listDevices().then(setDevices, () => setDevices([]));
  }, [listDevices]);
  useEffect(() => {
    if (platform.ok) refreshDevices();
  }, [platform.ok, refreshDevices]);

  // The input meter follows the open microphone through the count-in and the take.
  const take = phase.kind === 'countIn' || phase.kind === 'recording' ? phase.take : null;
  useEffect(() => {
    if (!take?.level) return;
    let handle = 0;
    const tick = () => {
      setLevel(take.level?.() ?? 0);
      handle = window.requestAnimationFrame(tick);
    };
    handle = window.requestAnimationFrame(tick);
    return () => {
      window.cancelAnimationFrame(handle);
    };
  }, [take]);
  useEffect(
    () => () => {
      cancelled.current = true;
    },
    [],
  );

  const place = (uploadedId: string, atFrame: number, seconds: number, retake: boolean) => {
    const current = docRef.current;
    const frames = Math.max(1, Math.round(seconds * fps));
    const ids = { itemId: newId(), trackId: newId() };
    const previous = retake && last ? current.tracks.findIndex((tr) => tr.id === last.trackId && tr.items.length === 1 && tr.items[0].id === last.itemId) : -1;
    const ops: EditOp[] = [];
    let index: number | undefined;
    if (previous >= 0) {
      ops.push({ type: 'removeTrack', trackId: current.tracks[previous].id });
      index = previous;
    }
    const without = previous >= 0 ? current.tracks.filter((_, i) => i !== previous) : current.tracks;
    const placed = compilePlaceVoiceover({ ...current, tracks: without }, uploadedId, atFrame, frames, ids, 'Voiceover', true, index);
    onApply([...ops, ...placed], t(previous >= 0 ? 'retakeLabel' : 'recordLabel', { time: framesToTimecode(atFrame, current.settings.rate) }));
    setLast({ trackId: ids.trackId, itemId: ids.itemId, atFrame });
  };

  const begin = async (retake: boolean) => {
    if (!platform.ok) return;
    cancelled.current = false;
    const atFrame = retake && last ? last.atFrame : playheadFrame;
    let opened: VoTake;
    try {
      setLimitNote(null);
      opened = await start(platform.mimeType, {
        deviceId,
        deferStart: true,
        onLimit: (message) => {
          setLimitNote(message);
          void finishRef.current();
        },
      });
    } catch (error) {
      setMicProblem(true);
      setPhase({ kind: 'error', message: micErrorSentence(error) });
      return;
    }
    refreshDevices();
    for (const n of COUNT_IN) {
      setPhase({ kind: 'countIn', n, take: opened, atFrame, retake });
      await sleep(countInMs);
      if (cancelled.current) return;
    }
    opened.startRecording?.();
    setPhase({ kind: 'recording', atFrame, take: opened, retake });
  };

  const finish = async () => {
    if (phase.kind !== 'recording') return;
    const { take: active, atFrame, retake } = phase;
    setPhase({ kind: 'uploading' });
    try {
      const { blob, durationSeconds } = await active.stop();
      // The upload carries the bare media type: the `;codecs=opus` parameter belongs to the recorder, and a parameterised
      // Content-Type is a different signed header on some stores (the multipart fallback was refused with a bad signature).
      const mime = platform.ok ? platform.mimeType : 'audio/webm';
      const file = new File([blob], voFileName(docRef.current.name, new Date(), mime), { type: mime.split(';')[0] });
      const uploaded = await upload(projectId, file);
      place(uploaded.id, atFrame, durationSeconds, retake);
      setPhase({ kind: 'idle' });
    } catch (error) {
      setPhase({ kind: 'error', message: humanizeErrorT(t, error, t('saveFailed')) });
    }
  };

  useEffect(() => {
    finishRef.current = finish;
  });

  const cancel = () => {
    cancelled.current = true;
    if (phase.kind === 'countIn' || phase.kind === 'recording') phase.take.cancel();
    setPhase({ kind: 'idle' });
  };

  const uploadRecording = async (file: File | null) => {
    if (!file) return;
    setPhase({ kind: 'uploading' });
    try {
      const seconds = await measureFile(file);
      if (!Number.isFinite(seconds) || seconds <= 0) throw new VoFileError('empty');
      const uploaded = await upload(projectId, file);
      place(uploaded.id, playheadFrame, seconds, false);
      setPhase({ kind: 'idle' });
    } catch (error) {
      setPhase({ kind: 'error', message: error instanceof VoFileError ? t(`fileErrors.${error.code}`) : humanizeErrorT(t, error, t('addFailed')) });
    }
  };

  const canRecord = platform.ok && !micProblem;
  const busy = phase.kind === 'uploading';
  const armed = phase.kind === 'countIn' || phase.kind === 'recording';
  const deviceOptions = devices.map((d) => ({ value: d.id, label: d.label }));

  return (
    <Stack gap={6} p="xs" data-testid="vo-recorder">
      <Text size="sm" fw={600}>
        {t('title')}
      </Text>
      <Text size="xs" c="dimmed">
        {t('intro')}
      </Text>
      {limitNote && (
        <Alert color="yellow" variant="light" data-testid="vo-limit">
          {limitNote}
        </Alert>
      )}
      {!platform.ok && (
        <Alert color="gray" variant="light" title={t('unavailableTitle')} data-testid="vo-unavailable">
          {phone ? t('unavailablePhone') : t('unavailable', { reason: platform.reason })}
        </Alert>
      )}
      {platform.ok && deviceOptions.length > 1 && !armed && (
        <Select size="xs" label={t('microphone')} data={deviceOptions} value={deviceId ?? deviceOptions[0].value} allowDeselect={false} onChange={setDeviceId} disabled={readOnly || busy} data-testid="vo-device" classNames={{ input: classes.touchInput }} />
      )}
      {armed && (
        <Group gap={8} align="center" wrap="nowrap">
          <Box
            role="meter"
            aria-label={t('level')}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={meterFill(level)}
            data-testid="vo-level"
            data-level={meterFill(level)}
            h={10}
            style={{ flex: 1, background: 'var(--mantine-color-default-hover)', borderRadius: 4, overflow: 'hidden' }}
          >
            <Box h="100%" style={{ width: `${meterFill(level)}%`, background: level > 0.9 ? 'var(--mantine-color-red-6)' : 'var(--mantine-color-teal-6)' }} />
          </Box>
          {phase.kind === 'countIn' ? (
            <Text size="lg" fw={700} data-testid="vo-countin" aria-live="polite">
              {phase.n}
            </Text>
          ) : (
            <Text size="xs" c="red" data-testid="vo-recording-state">
              {t('recording', { time: framesToTimecode(phase.atFrame, doc.settings.rate) })}
            </Text>
          )}
        </Group>
      )}
      <Group gap={6}>
        {phase.kind === 'recording' ? (
          <Button size="compact-sm" fz="xs" className={classes.touch} color="red" leftSection={<IconPlayerStopFilled size={14} />} onClick={() => void finish()} data-testid="vo-stop">
            {t('stop')}
          </Button>
        ) : phase.kind === 'countIn' ? null : (
          <>
            {platform.ok && (
              <Button size="compact-sm" fz="xs" className={classes.touch} leftSection={<IconMicrophone size={14} />} disabled={readOnly || busy || !canRecord} loading={busy} onClick={() => void begin(false)} data-testid="vo-record">
                {t('record')}
              </Button>
            )}
            {last && canRecord && (
              <Button size="compact-sm" fz="xs" className={classes.touch} variant="light" leftSection={<IconRefresh size={14} />} disabled={readOnly || busy} onClick={() => void begin(true)} data-testid="vo-retake">
                {t('retake')}
              </Button>
            )}
          </>
        )}
        {armed && (
          <Button size="compact-sm" fz="xs" className={classes.touch} variant="subtle" leftSection={<IconX size={14} />} onClick={cancel} data-testid="vo-cancel">
            {t('discard')}
          </Button>
        )}
        {!armed && (
          <FileButton onChange={(f) => void uploadRecording(f)} accept="audio/*">
            {(props) => (
              <Button {...props} size="compact-sm" fz="xs" className={classes.touch} variant="subtle" leftSection={<IconUpload size={14} />} disabled={readOnly || busy} data-testid="vo-upload">
                {phone ? t('uploadVoice') : t('uploadRecording')}
              </Button>
            )}
          </FileButton>
        )}
      </Group>
      {phase.kind === 'error' && (
        <Alert color="red" variant="light" data-testid="vo-error">
          {phase.message}
        </Alert>
      )}
    </Stack>
  );
}
