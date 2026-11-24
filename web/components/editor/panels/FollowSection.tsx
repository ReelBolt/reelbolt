'use client';

import { clearJobMark, registerJobMark } from '@/lib/editing/background/job-marks';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Button, Checkbox, Divider, Group, Loader, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { mediaRefOf } from '@/lib/editing/preview';
import type { ObjectSeed } from '@/lib/editing/selection/seed';
import type { MediaProbe } from '@/lib/editing/media/types';
import {
  classifyFollowError,
  requestFollow,
  startSubjectTrack,
  useSubjectTrack,
  type FollowOnLost,
  type FollowSmoothness,
  type FollowTightness,
  type SubjectBox,
} from '@/lib/editing/api/follow';
import { useObjectList } from '@/lib/editing/api/objects';
import { buildFollowTransform, isFollowStale, lostSpansInClip, snapshotClip, type FollowSnapshot } from '@/lib/editing/follow/map';
import { armFollowBox, disarmFollowBox, useFollowHost } from '@/lib/editing/follow/follow-host';
import { FOLLOW_RUN_PREFIX, userScopeFromEmail } from '@/lib/editing/sync/user-scope';
import { getUserFromCookie } from '@/lib/hooks/use-auth';
import { useCoarsePointer } from '../thumbnail/useCoarsePointer';
import { useT } from '@/i18n';

export interface FollowSectionProps {
  /** The single selected media clip. */
  clip: MediaClip;
  /** Null (no project in scope) renders nothing: there is no source to follow in. */
  projectId: string | null;
  readOnly: boolean;
  /** The drag on the viewer: off where the viewer cannot host it (the phone offers found objects only). */
  allowDraw: boolean;
  /** The probe of the clip's source, when known: a still picture has nothing to follow. */
  probe?: MediaProbe | null;
  /** The heading style: a divider in the inspector, a small heading in a sheet. */
  heading?: 'divider' | 'heading';
}

const STILL_CONTAINER = /image2|_pipe|tiff|webp|bmp/i;
export const isStillProbe = (probe: MediaProbe | null | undefined): boolean => Boolean(probe?.container && STILL_CONTAINER.test(probe.container));

const findClip = (doc: EditTimeline, id: string): MediaClip | null => {
  for (const track of doc.tracks) for (const item of track.items) if (item.id === id && item.type === 'mediaClip') return item as MediaClip;
  return null;
};
const onPicture = (doc: EditTimeline, id: string): boolean => doc.tracks.some((tr) => tr.kind === 'video' && tr.items.some((i) => i.id === id));

const clock = (sec: number): string => {
  const total = Math.max(0, Math.round(sec));
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
};

const storageKey = (projectId: string, clipId: string) => `${FOLLOW_RUN_PREFIX}${userScopeFromEmail(getUserFromCookie()?.email)}:${projectId}:${clipId}`;
const readStored = (projectId: string, clipId: string): string | null => {
  try {
    return typeof window === 'undefined' ? null : window.sessionStorage.getItem(storageKey(projectId, clipId));
  } catch {
    return null;
  }
};
const writeStored = (projectId: string, clipId: string, executionId: string | null) => {
  try {
    if (executionId) window.sessionStorage.setItem(storageKey(projectId, clipId), executionId);
    else window.sessionStorage.removeItem(storageKey(projectId, clipId));
  } catch {
    // storage unavailable: the section simply forgets the run when it closes
  }
};

/** Resolves true once the editor has no unsaved edits (it saves on its own), false after `timeoutMs` or if frozen. */
function waitForSaved(store: EditorStore, timeoutMs: number): Promise<boolean> {
  if (store.getState().readOnly) return Promise.resolve(false);
  if (!store.getState().dirty) return Promise.resolve(true);
  return new Promise((resolve) => {
    const done = (ok: boolean) => {
      window.clearTimeout(timer);
      unsubscribe();
      resolve(ok);
    };
    const unsubscribe = store.subscribe(() => {
      const s = store.getState();
      if (s.readOnly) done(false);
      else if (!s.dirty) done(true);
    });
    const timer = window.setTimeout(() => done(false), timeoutMs);
  });
}

type Phase =
  | { kind: 'idle' }
  | { kind: 'running' }
  | { kind: 'stale' }
  | { kind: 'error'; message: string }
  | { kind: 'applied'; zoomNote: string | null; lostNotes: string[] };

type Chosen = { analysisStepResultId: string; trackId: string | null };

/**
 * Follow subject: pick the subject (draw a box on the picture, or one the Objects section already found), pick a few
 * words, press Follow. The result lands on the clip as ordinary position and scale keyframes in ONE undoable write.
 * Hidden for anything but a picture clip of a project source. It needs the viewer's editor store (the viewer
 * registers it); with none, there is nothing to follow in and the section renders nothing.
 */
export function FollowSection(props: FollowSectionProps) {
  const { store } = useFollowHost();
  if (!store || !props.projectId) return null;
  return <FollowSectionGate key={props.clip.id} store={store} {...props} projectId={props.projectId} />;
}

function FollowSectionGate(props: FollowSectionProps & { store: EditorStore; projectId: string }) {
  const doc = useEditorStore(props.store, (s) => s.doc);
  const mediaRef = props.clip.assetRef ? mediaRefOf(props.clip.assetRef) : null;
  if (!mediaRef || !onPicture(doc, props.clip.id) || isStillProbe(props.probe)) return null;
  return <FollowSectionView {...props} mediaRef={mediaRef} />;
}

function FollowSectionView({ store, clip, projectId, mediaRef, readOnly, allowDraw, heading = 'divider' }: FollowSectionProps & { store: EditorStore; projectId: string; mediaRef: string }) {
  const t = useT('editor.panels.follow');
  const coarse = useCoarsePointer();
  const tap = coarse ? { minHeight: 44 } : undefined;
  const host = useFollowHost();
  const drawing = host.armed?.clipId === clip.id;

  const [mode, setMode] = useState<'box' | 'object'>(allowDraw ? 'box' : 'object');
  const [execId, setExecId] = useState<string | null>(() => readStored(projectId, clip.id));
  const [startError, setStartError] = useState<string | null>(null);
  const [objectKey, setObjectKey] = useState<string | null>(null);
  const [tightness, setTightness] = useState<FollowTightness>('Medium');
  const [smoothness, setSmoothness] = useState<FollowSmoothness>('Normal');
  const [onLost, setOnLost] = useState<FollowOnLost>('Hold');
  const [softZoom, setSoftZoom] = useState(false);
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      disarmFollowBox();
    };
  }, []);

  const track = useSubjectTrack(projectId, mediaRef, mode === 'box' ? execId : null);
  const job = track.data;
  // A found subject is kept (people navigate away while tracking runs) until Follow has used it; only a failed run is forgotten.
  const failedJob = job?.status === 'failed';
  useEffect(() => {
    if (failedJob) writeStored(projectId, clip.id, null);
  }, [failedJob, projectId, clip.id]);
  const list = useObjectList(projectId, mediaRef);
  const objectOptions = useMemo(() => {
    const out: { value: string; label: string; chosen: Chosen }[] = [];
    for (const r of list.data ?? []) {
      if (r.status !== 'succeeded' || !r.analysisStepResultId) continue;
      const ids = r.trackIds && r.trackIds.length > 0 ? r.trackIds : [null];
      ids.forEach((trackId, i) => {
        const label = ids.length > 1 ? `${r.prompt} ${i + 1}` : r.prompt;
        out.push({ value: `${r.analysisStepResultId}|${trackId ?? ''}`, label, chosen: { analysisStepResultId: r.analysisStepResultId!, trackId } });
      });
    }
    return out;
  }, [list.data]);

  const chosen: Chosen | null =
    mode === 'box'
      ? job?.status === 'succeeded' && job.analysisStepResultId && job.track
        ? { analysisStepResultId: job.analysisStepResultId, trackId: job.track.trackId }
        : null
      : (objectOptions.find((o) => o.value === objectKey)?.chosen ?? null);

  const startBox = useCallback(
    (seed: ObjectSeed) => {
      // The seed already carries the SOURCE second the playhead mapped to, and always a box (a tap gets a default one).
      const box: SubjectBox | undefined = seed.box;
      if (!box || !findClip(store.getState().doc, clip.id)) return;
      const atSec = seed.atSec;
      setStartError(null);
      startSubjectTrack(projectId, mediaRef, { atSec, box }).then(
        (queued) => {
          if (!mounted.current) {
            writeStored(projectId, clip.id, queued.executionId);
            return;
          }
          setExecId(queued.executionId);
          writeStored(projectId, clip.id, queued.executionId);
          setPhase({ kind: 'idle' });
        },
        (error: unknown) => {
          const c = classifyFollowError(error);
          if (mounted.current) setStartError(c.message ?? (c.kind === 'credits' ? t('blockedCredits') : t('startFailed')));
        },
      );
    },
    [store, clip.id, projectId, mediaRef, t],
  );

  const toggleDraw = () => {
    if (drawing) disarmFollowBox();
    else armFollowBox({ clipId: clip.id, onSeed: startBox });
  };

  const run = useCallback(() => {
    if (!chosen) return;
    setPhase({ kind: 'running' });
    // The server computes the path for the SAVED clip: wait for the editor's own save so the revision includes every edit.
    void waitForSaved(store, 8000).then((saved) => {
      if (!mounted.current) return;
      if (!saved) {
        setPhase({ kind: 'error', message: t('notSaved') });
        return;
      }
      const state = store.getState();
      const requested = findClip(state.doc, clip.id);
      if (!requested) {
        setPhase({ kind: 'stale' });
        return;
      }
      const canvas = { width: state.doc.settings.width, height: state.doc.settings.height };
      const snapshot: FollowSnapshot = snapshotClip(requested, canvas);
      requestFollow(projectId, state.timelineId, { itemId: clip.id, revision: state.revision, track: chosen, tightness, smoothness, onLost, allowSoftZoom: softZoom }).then(
        (res) => {
          if (!mounted.current) return;
          const now = store.getState();
          const current = findClip(now.doc, clip.id);
          const nowCanvas = { width: now.doc.settings.width, height: now.doc.settings.height };
          if (!current || isFollowStale(snapshot, current, nowCanvas, res.assumed)) {
            setPhase({ kind: 'stale' });
            return;
          }
          const built = buildFollowTransform(current, res.keys, now.doc.settings.rate);
          if (!built.transform) {
            setPhase({ kind: 'error', message: t('nothingToFollow') });
            return;
          }
          try {
            // The follow's own result: never held by the follow's own running mark. Applied only if it really landed.
            if (!store.dispatch({ type: 'setProperty', itemId: clip.id, key: 'transform', value: built.transform }, t('label'), { exemptJobId: `follow:${clip.id}` })) {
              setPhase({ kind: 'error', message: t('applyFailed') });
              return;
            }
          } catch {
            setPhase({ kind: 'error', message: t('applyFailed') });
            return;
          }
          const fps = now.doc.settings.rate.den > 0 ? now.doc.settings.rate.num / now.doc.settings.rate.den : 30;
          const lostNotes = lostSpansInClip(current, res.lostSpans, now.doc.settings.rate).map((s) =>
            t('lostSpan', { from: clock((s.fromFrame - current.recordIn) / fps), to: clock((s.toFrame - current.recordIn) / fps) }),
          );
          const zoomNote = res.zoomCapLimited ? (softZoom ? t('zoomLimitedSoft', { zoom: Math.round(res.zoomCap * 10) / 10 }) : t('zoomLimited', { zoom: Math.round(res.zoomCap * 10) / 10 })) : null;
          if (mode === 'box') writeStored(projectId, clip.id, null);
          setPhase({ kind: 'applied', zoomNote, lostNotes });
        },
        (error: unknown) => {
          if (!mounted.current) return;
          const c = classifyFollowError(error);
          setPhase({ kind: 'error', message: c.message ?? (c.kind === 'credits' ? t('blockedCredits') : t('followFailed')) });
        },
      );
    });
  }, [chosen, mode, store, clip.id, projectId, tightness, smoothness, onLost, softZoom, t]);

  const removeFollow = () => {
    try {
      // Only the three followed channels go back to identity; rotation, opacity and the rest stay.
      const rest = { ...(store.getState().doc.tracks.flatMap((tr) => tr.items).find((i) => i.id === clip.id && i.type === 'mediaClip') as MediaClip | undefined)?.transform };
      delete rest.positionX;
      delete rest.positionY;
      delete rest.scale;
      const left = Object.values(rest).some((c) => c !== null && c !== undefined);
      store.dispatch({ type: 'setProperty', itemId: clip.id, key: 'transform', value: left ? rest : null }, t('labelRemove'));
      setPhase({ kind: 'idle' });
    } catch {
      setPhase({ kind: 'error', message: t('applyFailed') });
    }
  };

  const followed = (clip.transform?.positionX?.keys?.length ?? 0) > 1;
  const busy = phase.kind === 'running';
  // The clip shows the job on the timeline (progress stripe) and the edit guard holds edits the follow reads.
  const jobLabel = t('jobLabel');
  useEffect(() => {
    const id = `follow:${clip.id}`;
    if (phase.kind === 'running') registerJobMark({ id, jobKind: 'follow', clipIds: [clip.id], state: 'running', label: jobLabel });
    else clearJobMark(id);
    return () => clearJobMark(id);
  }, [phase.kind, clip.id, jobLabel]);
  const head = heading === 'divider' ? <Divider label={t('heading')} labelPosition="left" /> : (
    <Text size="sm" fw={600} component="h3" m={0}>
      {t('heading')}
    </Text>
  );

  let subjectStatus: React.ReactNode = null;
  if (mode === 'box') {
    if (startError) subjectStatus = <Text size="xs" c="red" role="alert" data-testid="follow-start-error">{startError}</Text>;
    else if (execId && !job && !track.error) subjectStatus = <Group gap={6}><Loader size="xs" /><Text size="xs" c="dimmed" data-testid="follow-finding">{t('finding')}</Text></Group>;
    else if (track.stalled) subjectStatus = (
      <Group gap={6}>
        <Text size="xs" c="dimmed">{t('stalled')}</Text>
        <Button size="compact-xs" fz="xs" variant="subtle" onClick={track.retry}>{t('retry')}</Button>
      </Group>
    );
    else if (job && (job.status === 'queued' || job.status === 'running')) subjectStatus = <Group gap={6}><Loader size="xs" /><Text size="xs" c="dimmed" data-testid="follow-finding">{t('finding')}</Text></Group>;
    else if (job?.status === 'failed') subjectStatus = <Text size="xs" c="red" role="alert" data-testid="follow-job-failed">{job.message ?? t('startFailed')}</Text>;
    else if (job?.status === 'succeeded' && job.track) subjectStatus = <Text size="xs" c="dimmed" data-testid="follow-found">{t('found', { from: clock(job.track.startSec), to: clock(job.track.endSec) })}</Text>;
  }

  return (
    <Stack gap="xs" data-testid="follow-section" id="follow-section">
      {head}
      {allowDraw && (
        <SegmentedControl
          size="xs"
          fullWidth
          value={mode}
          disabled={readOnly || busy}
          onChange={(v) => {
            disarmFollowBox();
            setMode(v as 'box' | 'object');
          }}
          data={[
            { value: 'box', label: t('modeBox') },
            { value: 'object', label: t('modeObject') },
          ]}
          data-testid="follow-mode"
        />
      )}
      {mode === 'box' ? (
        <>
          <Button variant="default" size="xs" fz="xs" disabled={readOnly || busy} onClick={toggleDraw} style={tap} data-testid="follow-draw">
            {drawing ? t('drawCancel') : t('drawBox')}
          </Button>
          {drawing && <Text size="xs" c="dimmed" data-testid="follow-draw-hint">{t('drawHint')}</Text>}
          {subjectStatus}
        </>
      ) : objectOptions.length > 0 ? (
        <Select size="xs" label={t('objectLabel')} placeholder={t('objectPlaceholder')} value={objectKey} onChange={setObjectKey} data={objectOptions.map((o) => ({ value: o.value, label: o.label }))} disabled={readOnly || busy} allowDeselect={false} styles={coarse ? { input: { minHeight: 44 } } : undefined} data-testid="follow-object" />
      ) : (
        <Text size="xs" c="dimmed" data-testid="follow-no-objects">{t('noObjects')}</Text>
      )}

      <Text size="xs" fw={500}>{t('tightness')}</Text>
      <SegmentedControl size="xs" fullWidth value={tightness} onChange={(v) => setTightness(v as FollowTightness)} disabled={readOnly || busy} data={[{ value: 'Loose', label: t('loose') }, { value: 'Medium', label: t('medium') }, { value: 'Tight', label: t('tight') }]} data-testid="follow-tightness" />
      <Text size="xs" fw={500}>{t('smoothness')}</Text>
      <SegmentedControl size="xs" fullWidth value={smoothness} onChange={(v) => setSmoothness(v as FollowSmoothness)} disabled={readOnly || busy} data={[{ value: 'Gentle', label: t('gentle') }, { value: 'Normal', label: t('normal') }, { value: 'Snappy', label: t('snappy') }]} data-testid="follow-smoothness" />
      <Text size="xs" fw={500}>{t('onLost')}</Text>
      <SegmentedControl size="xs" fullWidth value={onLost} onChange={(v) => setOnLost(v as FollowOnLost)} disabled={readOnly || busy} data={[{ value: 'Hold', label: t('hold') }, { value: 'Widen', label: t('widen') }]} data-testid="follow-onlost" />
      <Checkbox size="xs" label={t('softZoom')} checked={softZoom} onChange={(e) => setSoftZoom(e.currentTarget.checked)} disabled={readOnly || busy} data-testid="follow-softzoom" />

      <Button size="xs" fz="xs" onClick={run} loading={busy} disabled={readOnly || !chosen || busy} style={tap} data-testid="follow-run">
        {t('follow')}
      </Button>

      {phase.kind === 'stale' && (
        <Group gap="xs" data-testid="follow-stale">
          <Text size="xs" c="dimmed">{t('stale')}</Text>
          <Button size="compact-xs" fz="xs" variant="default" disabled={readOnly} onClick={run} data-testid="follow-rerun">{t('runAgain')}</Button>
        </Group>
      )}
      {phase.kind === 'error' && <Text size="xs" c="red" role="alert" data-testid="follow-error">{phase.message}</Text>}
      {phase.kind === 'applied' && (
        <Stack gap={2} data-testid="follow-notes">
          <Text size="xs" c="dimmed">{t('applied')}</Text>
          {phase.zoomNote && <Text size="xs" c="dimmed" data-testid="follow-zoom-note">{phase.zoomNote}</Text>}
          {phase.lostNotes.map((n) => <Text key={n} size="xs" c="dimmed" data-testid="follow-lost-note">{n}</Text>)}
        </Stack>
      )}
      {followed && (
        <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} onClick={removeFollow} data-testid="follow-remove">
          {t('remove')}
        </Button>
      )}
    </Stack>
  );
}
