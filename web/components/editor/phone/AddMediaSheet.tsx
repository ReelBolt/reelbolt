'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Button, Group, Progress, Stack, Text, TextInput } from '@mantine/core';
import { IconArrowLeft, IconCamera, IconFolderPlus, IconMusic, IconUpload, IconUserCircle } from '@tabler/icons-react';
import { AnnotationEditor } from '../media/AnnotationEditor';
import { FilmstripStrip } from '../media/FilmstripStrip';
import { SourceViewer } from '../media/SourceViewer';
import { PlaceModeButton } from '../place/PlaceMenu';
import { EditorEmpty } from '../ui';
import { addBinItem, createBin, fetchMediaDerivatives, fetchMediaFilmstrip, removeBinItem, upsertMediaAnnotation } from '@/lib/editing/api/media';
import { filmstripDurationSec } from '@/lib/editing/media/filmstrip';
import { formatDuration } from '@/lib/editing/media/format';
import { applyDropOptimistically, planAddToBin } from '@/lib/editing/media/bins';
import type { Marks } from '@/lib/editing/media/in-out';
import type { FilmstripIndex, MediaBin, MediaPoolItem, MediaRef } from '@/lib/editing/media/types';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useMediaAnnotations, useMediaBins, useMediaPool } from '@/lib/hooks/use-media';
import { isTerminalStage, poolStageStoresProbe, useMediaDerivativeProgress, type MediaDerivativeStage } from '@/lib/hooks/use-media-derivatives';
import { useUploadQueue } from '@/lib/hooks/use-upload-queue';
import { SfxLibraryPanel } from '../media/SfxLibraryPanel';
import { SubjectsPanel } from '../media/SubjectsPanel';
import { useAvatarRequirements } from '@/lib/hooks/use-avatars';
import { MediaTile } from './MediaTile';
import { tileReadiness, uploadMsg, type TileReads } from './media-readiness';
import classes from './phone.module.css';
import { useT } from '@/i18n';

const MEDIA_ACCEPT = 'video/*,audio/*,image/*';

export type AddMediaPlaceMode = 'insert' | 'append' | 'placeOver';

export interface AddMediaSheetProps {
  projectId: string;
  readOnly: boolean;
  /** Puts the source on the timeline (the same Place logic as the desktop). Returns a plain refusal, or `null` when it landed. */
  onPlace: (item: MediaPoolItem, mode: AddMediaPlaceMode, marks: Marks | null) => string | null;
  /** The source landed: the sheet is done, the host shrinks it to a peek so the strip shows the result. */
  onPlaced: (item: MediaPoolItem) => void;
}

type View = { kind: 'browse' } | { kind: 'preview'; mediaRef: MediaRef } | { kind: 'annotate'; mediaRef: MediaRef };

interface Reads extends TileReads {
  index: FilmstripIndex | null;
  indexError: string | null;
}

const NO_READS: Reads = { rows: null, error: null, index: null, indexError: null };

/**
 * RM.8: the dock's Media item on a phone. Add files from the camera roll or the system file picker with plain-text progress,
 * see whether each tile is ready, and put a tile on the timeline, in a bin, or in front of you with one tap on its Actions button.
 */
export function AddMediaSheet({ projectId, readOnly, onPlace, onPlaced }: AddMediaSheetProps) {
  const t = useT('editor.phone.addMediaSheet');
  const tSfx = useT('editor-media.sfx');
  const tSub = useT('editor-media.subjects');
  // Offered only while the project's avatars answer (a 404 = the feature is off here).
  const { data: avatarRequirements, error: avatarRequirementsError } = useAvatarRequirements(projectId);
  const avatarsOffered = Boolean(avatarRequirements && !avatarRequirementsError);
  const { data: pool, error: poolError, isLoading: poolLoading, mutate: mutatePool } = useMediaPool(projectId);
  const { data: swrBins, mutate: mutateBins } = useMediaBins(projectId);
  const { data: annotations, mutate: mutateAnnotations } = useMediaAnnotations(projectId);
  const items = useMemo(() => pool ?? [], [pool]);
  const bins = useMemo(() => swrBins ?? [], [swrBins]);
  const binNames = useMemo(() => new Map(bins.map((b) => [b.id, b.name])), [bins]);

  const [view, setView] = useState<View>({ kind: 'browse' });
  const [selectedBinId, setSelectedBinId] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [naming, setNaming] = useState(false);
  const [sfxOpen, setSfxOpen] = useState(false);
  const [avatarsOpen, setAvatarsOpen] = useState(false);
  const [binName, setBinName] = useState('');

  // ── uploads ───────────────────────────────────────────────────────────────
  const uploads = useUploadQueue(projectId, () => {
    void mutatePool();
  });
  const { enqueue } = uploads;
  const pickRef = useRef<HTMLInputElement | null>(null);
  const captureRef = useRef<HTMLInputElement | null>(null);
  const onFiles = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const files = Array.from(event.currentTarget.files ?? []);
      event.currentTarget.value = '';
      if (files.length > 0) enqueue(files.map((file) => ({ file, relativePath: file.name })), '');
    },
    [enqueue],
  );
  const busy = uploads.entries.some((e) => e.status === 'queued' || e.status === 'uploading');
  const finished = uploads.entries.some((e) => e.status === 'done' || e.status === 'error');

  // A fresh upload's ingest probe lands a little later: re-read the pool every few seconds (at most two minutes) while a sound or video has none.
  const waitingForProbe = items.some((i) => i.kind !== 'image' && i.probe === null);
  useEffect(() => {
    if (!waitingForProbe) return undefined;
    let rounds = 0;
    const timer = setInterval(() => {
      rounds += 1;
      if (rounds > 40) clearInterval(timer);
      else void mutatePool();
    }, 3000);
    return () => clearInterval(timer);
  }, [waitingForProbe, mutatePool]);

  // ── what each tile can say about its preview ──────────────────────────────
  const [reads, setReads] = useState<Record<MediaRef, Reads>>({});
  const [stages, setStages] = useState<Record<MediaRef, MediaDerivativeStage>>({});
  const requested = useRef(new Set<MediaRef>());
  const patchReads = useCallback((mediaRef: MediaRef, patch: Partial<Reads>) => setReads((prev) => ({ ...prev, [mediaRef]: { ...(prev[mediaRef] ?? NO_READS), ...patch } })), []);
  const readRows = useCallback(
    (mediaRef: MediaRef) => {
      void fetchMediaDerivatives(projectId, mediaRef)
        .then((rows) => patchReads(mediaRef, { rows, error: null }))
        .catch((error: unknown) => patchReads(mediaRef, { rows: null, error: humanizeErrorT(t, error, t('theRequestFailed')) }));
    },
    [projectId, patchReads, t],
  );
  const readIndex = useCallback(
    (mediaRef: MediaRef) => {
      void fetchMediaFilmstrip(projectId, mediaRef)
        .then((response) => patchReads(mediaRef, { index: response?.filmstrip ?? null, indexError: null }))
        .catch((error: unknown) => patchReads(mediaRef, { index: null, indexError: humanizeErrorT(t, error, t('theRequestFailed2')) }));
    },
    [projectId, patchReads, t],
  );
  // Reads only (GETs), once per source, for the tiles on show. Nothing here asks the service to make anything.
  useEffect(() => {
    for (const item of items) {
      if (item.kind === 'image' || requested.current.has(item.mediaRef)) continue;
      requested.current.add(item.mediaRef);
      readRows(item.mediaRef);
    }
  }, [items, readRows]);
  useMediaDerivativeProgress(projectId, (event) => {
    if (!event.mediaRef) return;
    const ref = event.mediaRef;
    setStages((prev) => ({ ...prev, [ref]: event.stage }));
    if (poolStageStoresProbe(event.stage)) void mutatePool();
    if (isTerminalStage(event.stage)) readRows(ref);
  });

  // ── bins ──────────────────────────────────────────────────────────────────
  const submitBin = useCallback(async () => {
    const name = binName.trim();
    if (name === '') {
      setProblem(t('giveTheBinA'));
      return;
    }
    setProblem(null);
    try {
      const created = await createBin(projectId, { name });
      await mutateBins((cur) => [...(cur ?? []), created], { revalidate: false });
      setSelectedBinId(created.id);
      setBinName('');
      setNaming(false);
      setMessage(t('madeTheBin', { name: created.name }));
    } catch (error) {
      setProblem(t('couldNotMakeThe', { v1: humanizeErrorT(t, error, t('theRequestFailed3')) }));
    }
  }, [binName, projectId, mutateBins, t]);

  const addToBin = useCallback(
    async (item: MediaPoolItem, binId: string) => {
      setProblem(null);
      const plan = planAddToBin(item.mediaRef, binId, bins, item.bins);
      if (plan.kind !== 'addItem') {
        // The existing plain refusal (a smart bin, or already there).
        if (plan.kind === 'none') setProblem(plan.reason);
        return;
      }
      const name = binNames.get(binId) ?? 'the bin';
      const previous = bins;
      void mutateBins(applyDropOptimistically(bins, plan) as MediaBin[], { revalidate: false });
      try {
        const created = await addBinItem(projectId, plan.binId, plan.mediaRef);
        void mutatePool((cur) => (cur ?? []).map((i) => (i.mediaRef === plan.mediaRef ? { ...i, bins: [...i.bins.filter((p) => p.itemId !== created.id), { binId: plan.binId, itemId: created.id }] } : i)), { revalidate: false });
        setMessage(t('addedTo', { name }));
      } catch (error) {
        void mutateBins(previous, { revalidate: false });
        setProblem(humanizeErrorT(t, error, t('theRequestFailed4')));
      }
    },
    [bins, binNames, projectId, mutateBins, mutatePool, t],
  );
  const removeFromBin = useCallback(
    async (item: MediaPoolItem, binId: string, itemId: string) => {
      setProblem(null);
      try {
        await removeBinItem(projectId, binId, itemId);
        void mutatePool((cur) => (cur ?? []).map((i) => (i.mediaRef === item.mediaRef ? { ...i, bins: i.bins.filter((p) => p.itemId !== itemId) } : i)), { revalidate: false });
        setMessage(`Removed from "${binNames.get(binId) ?? 'the bin'}".`);
      } catch (error) {
        setProblem(humanizeErrorT(t, error, t('theRequestFailed5')));
      }
    },
    [projectId, binNames, mutatePool, t],
  );

  // ── put it on the timeline ────────────────────────────────────────────────
  const place = useCallback(
    (item: MediaPoolItem, mode: AddMediaPlaceMode, marks: Marks | null) => {
      setMessage(null);
      const refusal = onPlace(item, mode, marks);
      if (refusal) {
        setProblem(refusal);
        return;
      }
      setProblem(null);
      setView({ kind: 'browse' });
      onPlaced(item);
    },
    [onPlace, onPlaced],
  );

  // ── looking through a tile (R04.6) ────────────────────────────────────────
  const [scrub, setScrub] = useState<{ mediaRef: MediaRef; fraction: number } | null>(null);
  const [lastScrubbed, setLastScrubbed] = useState<{ mediaRef: MediaRef; timeSec: number } | null>(null);
  const onScrub = useCallback(
    (mediaRef: MediaRef, fraction: number) => {
      if (!reads[mediaRef]?.index && !reads[mediaRef]?.indexError && !scrub) readIndex(mediaRef);
      setScrub({ mediaRef, fraction });
    },
    [reads, scrub, readIndex],
  );
  const scrubRange = scrub ? filmstripDurationSec(reads[scrub.mediaRef]?.index ?? null) : 0;
  const onScrubEnd = useCallback(() => {
    if (scrub) setLastScrubbed({ mediaRef: scrub.mediaRef, timeSec: scrubRange > 0 ? scrub.fraction * scrubRange : 0 });
    setScrub(null);
  }, [scrub, scrubRange]);
  const scrubLabel = scrub ? (scrubRange > 0 ? `${formatDuration(scrub.fraction * scrubRange)} of ${formatDuration(scrubRange)}` : t('previewLoading')) : null;

  const annotationOf = (mediaRef: MediaRef) => (annotations ?? []).find((a) => a.mediaRef === mediaRef) ?? null;

  // ── the three views ───────────────────────────────────────────────────────
  const viewed = view.kind === 'browse' ? null : items.find((i) => i.mediaRef === view.mediaRef) ?? null;

  if (view.kind !== 'browse') {
    return (
      <PreviewOrAnnotate
        view={view}
        item={viewed}
        projectId={projectId}
        readOnly={readOnly}
        problem={problem}
        annotation={viewed ? annotationOf(viewed.mediaRef) : null}
        onBack={() => {
          setProblem(null);
          setView({ kind: 'browse' });
        }}
        onPlace={place}
        onSaveAnnotation={async (request) => {
          if (!viewed) return;
          const saved = await upsertMediaAnnotation(projectId, viewed.mediaRef, request);
          await mutateAnnotations((cur) => [...(cur ?? []).filter((a) => a.mediaRef !== saved.mediaRef), saved], { revalidate: false });
        }}
      />
    );
  }

  if (sfxOpen) {
    return (
      <div className={classes.sheetStack} data-testid="add-media-sfx">
        <Button h={44} miw={44} variant="default" leftSection={<IconArrowLeft size={18} />} style={{ alignSelf: 'flex-start' }} onClick={() => setSfxOpen(false)} data-testid="add-media-sfx-back">
          {t('backToMedia')}
        </Button>
        <SfxLibraryPanel
          projectId={projectId}
          readOnly={readOnly}
          touch
          onPlaceItem={(item) => {
            const refusal = onPlace(item, 'insert', null);
            if (!refusal) onPlaced(item);
            return refusal;
          }}
        />
      </div>
    );
  }

  if (avatarsOpen && avatarsOffered) {
    return (
      <div className={classes.sheetStack} data-testid="add-media-avatars">
        <Button h={44} miw={44} variant="default" leftSection={<IconArrowLeft size={18} />} style={{ alignSelf: 'flex-start' }} onClick={() => setAvatarsOpen(false)} data-testid="add-media-avatars-back">
          {t('backToMedia')}
        </Button>
        <SubjectsPanel projectId={projectId} readOnly={readOnly} touch />
      </div>
    );
  }

  const shown = selectedBinId === null ? items : items.filter((i) => i.bins.some((p) => p.binId === selectedBinId));
  const selectedBin = bins.find((b) => b.id === selectedBinId) ?? null;
  const subject = lastScrubbed ?? (scrub ? { mediaRef: scrub.mediaRef, timeSec: 0 } : null);
  const subjectItem = subject ? items.find((i) => i.mediaRef === subject.mediaRef) ?? null : null;
  const subjectReads = subject ? reads[subject.mediaRef] ?? NO_READS : null;

  return (
    <div className={classes.sheetStack} data-testid="media-pool">
      <Group gap="xs" wrap="wrap" data-testid="add-media-sheet">
        <Button h={44} miw={44} leftSection={<IconUpload size={18} />} disabled={readOnly} onClick={() => pickRef.current?.click()} data-testid="add-media-pick">
          {t('choosePhotosVideosOr')}
        </Button>
        <Button h={44} miw={44} variant="default" leftSection={<IconCamera size={18} />} disabled={readOnly} onClick={() => captureRef.current?.click()} data-testid="add-media-capture">
          {t('takeAPhotoOr')}
        </Button>
        <Button h={44} miw={44} variant="default" leftSection={<IconMusic size={18} />} onClick={() => setSfxOpen(true)} data-testid="add-media-sfx-open">
          {tSfx('tab')}
        </Button>
        {avatarsOffered ? (
          <Button h={44} miw={44} variant="default" leftSection={<IconUserCircle size={18} />} onClick={() => setAvatarsOpen(true)} data-testid="add-media-avatars-open">
            {tSub('tab')}
          </Button>
        ) : null}
        <input ref={pickRef} type="file" multiple accept={MEDIA_ACCEPT} hidden onChange={onFiles} data-testid="add-media-input" aria-label={t('chooseFilesToAdd')} />
        <input ref={captureRef} type="file" accept="video/*,image/*" capture="environment" hidden onChange={onFiles} data-testid="add-media-capture-input" aria-label={t('takeAPhotoOr2')} />
      </Group>

      {uploads.entries.length > 0 ? (
        <Stack gap={8} data-testid="add-media-uploads">
          {uploads.entries.map((entry) => (
            <Stack key={entry.id} gap={2} data-testid="add-media-upload-row" data-status={entry.status}>
              <Group justify="space-between" wrap="nowrap" gap="xs">
                <Text size="sm" truncate style={{ minWidth: 0 }}>
                  {entry.name}
                </Text>
                <Text size="sm" c={entry.status === 'error' ? 'red' : 'dimmed'} style={{ whiteSpace: 'nowrap' }} data-testid="add-media-upload-text">
                  {t.msg(uploadMsg(entry))}
                </Text>
              </Group>
              {entry.status === 'uploading' || entry.status === 'queued' ? <Progress value={entry.progress} size="sm" aria-label={t('uploading', { name: entry.name })} /> : null}
              {entry.status === 'error' ? (
                <Group gap="xs" wrap="nowrap">
                  <Text size="sm" c="red" style={{ flex: 1, minWidth: 0 }}>
                    {entry.error ?? t('theUploadFailed')}
                  </Text>
                  {entry.quotaExceeded ? null : (
                    <Button h={44} miw={44} variant="light" onClick={() => uploads.retry(entry.id)} data-testid="add-media-retry">
                      {t('tryAgain')}
                    </Button>
                  )}
                </Group>
              ) : null}
            </Stack>
          ))}
          {finished && !busy ? (
            <Button h={44} miw={44} variant="subtle" color="gray" style={{ alignSelf: 'flex-start' }} onClick={uploads.clearFinished}>
              {t('clearList')}
            </Button>
          ) : null}
        </Stack>
      ) : null}

      <div className={classes.binChips} role="group" aria-label={t('bins')} data-testid="add-media-bins">
        <button type="button" className={classes.binChip} data-active={selectedBinId === null} aria-pressed={selectedBinId === null} onClick={() => setSelectedBinId(null)} data-testid="add-media-bin-all">
          {t('allMedia')}
        </button>
        {bins.map((bin) => (
          <button key={bin.id} type="button" className={classes.binChip} data-active={selectedBinId === bin.id} aria-pressed={selectedBinId === bin.id} onClick={() => setSelectedBinId(bin.id)} data-testid={`add-media-bin-${bin.id}`}>
            {bin.name}
          </button>
        ))}
        <button type="button" className={classes.binChip} onClick={() => setNaming((v) => !v)} aria-expanded={naming} data-testid="add-media-new-bin">
          <IconFolderPlus size={16} style={{ verticalAlign: 'middle', marginRight: 6 }} />
          {t('newBin')}
        </button>
      </div>
      {naming ? (
        <Group gap="xs" wrap="nowrap" align="flex-end">
          <TextInput label={t('binName')} value={binName} onChange={(e) => setBinName(e.currentTarget.value)} style={{ flex: 1, minWidth: 0 }} styles={{ input: { minHeight: 44, fontSize: 16 } }} data-testid="add-media-bin-name" />
          <Button h={44} miw={64} onClick={() => void submitBin()} data-testid="add-media-bin-create">
            {t('makeBin')}
          </Button>
        </Group>
      ) : null}
      <Text size="xs" c="dimmed" data-testid="needs-larger-screen">
        {t('movingBinsAndEditing')}
      </Text>
      {selectedBin ? (
        <Text size="xs" c="dimmed" data-testid="add-media-bin-note">
          {selectedBin.isSmart || selectedBin.smartFilter ? t('aSmartBinShows') : t('showingOnlyWhatIs', { name: selectedBin.name })}
        </Text>
      ) : null}

      {message ? (
        <Text size="sm" role="status" data-testid="add-media-message">
          {message}
        </Text>
      ) : null}
      {problem ? (
        <p className={classes.sheetProblem} role="alert" data-testid="add-media-problem">
          {problem}
        </p>
      ) : null}

      {poolLoading ? (
        <Text size="sm" c="dimmed" data-testid="media-pool-loading">
          {t('loadingYourMedia')}
        </Text>
      ) : poolError ? (
        <Text size="sm" c="red" role="alert" data-testid="media-pool-error">
          {t('yourMediaCouldNot')} {humanizeErrorT(t, poolError, t('theRequestFailed6'))}
        </Text>
      ) : shown.length === 0 ? (
        <EditorEmpty compact title={items.length === 0 ? t('noMediaInThis') : t('nothingInThisBin')} hint={items.length === 0 ? t('chooseFilesAboveOr') : t('useATileS')} testId="add-media-empty" />
      ) : (
        <div className={classes.mediaGrid} data-testid="add-media-grid">
          {shown.map((item) => (
            <MediaTile
              key={item.mediaRef}
              projectId={projectId}
              item={item}
              readiness={tileReadiness(item, reads[item.mediaRef], stages[item.mediaRef])}
              bins={bins}
              binNames={binNames}
              readOnly={readOnly}
              scrubLabel={scrub?.mediaRef === item.mediaRef ? scrubLabel : null}
              onInsert={(i) => place(i, 'insert', null)}
              onAppend={(i) => place(i, 'append', null)}
              onAddToBin={(i, binId) => void addToBin(i, binId)}
              onRemoveFromBin={(i, binId, itemId) => void removeFromBin(i, binId, itemId)}
              onAnnotate={(i) => setView({ kind: 'annotate', mediaRef: i.mediaRef })}
              onPreview={(i) => setView({ kind: 'preview', mediaRef: i.mediaRef })}
              onScrub={onScrub}
              onScrubEnd={onScrubEnd}
            />
          ))}
        </div>
      )}

      {shown.length > 0 ? (
        <FilmstripStrip item={subjectItem} index={subjectReads?.index ?? null} indexError={subjectReads?.indexError ?? null} derivatives={subjectReads?.rows ?? null} derivativesError={subjectReads?.error ?? null} timeSec={scrub && scrubRange > 0 ? scrub.fraction * scrubRange : subject?.timeSec ?? 0} onScrub={(timeSec) => subjectItem && setLastScrubbed({ mediaRef: subjectItem.mediaRef, timeSec })} />
      ) : null}
    </div>
  );
}

interface PreviewOrAnnotateProps {
  view: Exclude<View, { kind: 'browse' }>;
  item: MediaPoolItem | null;
  projectId: string;
  readOnly: boolean;
  problem: string | null;
  annotation: Parameters<typeof AnnotationEditor>[0]['annotation'];
  onBack: () => void;
  onPlace: (item: MediaPoolItem, mode: AddMediaPlaceMode, marks: Marks | null) => void;
  onSaveAnnotation: Parameters<typeof AnnotationEditor>[0]['onSave'];
}

/** Preview (the source viewer) and Keywords and notes (the annotation editor) take over the sheet; Back returns to the tiles. */
function PreviewOrAnnotate({ view, item, projectId, readOnly, problem, annotation, onBack, onPlace, onSaveAnnotation }: PreviewOrAnnotateProps) {
  const t = useT('editor.phone.addMediaSheet');
  const [marks, setMarks] = useState<Marks | null>(null);
  return (
    <div className={classes.sheetStack} data-testid={view.kind === 'preview' ? 'add-media-preview' : 'add-media-annotate'}>
      <Button h={44} miw={44} variant="default" leftSection={<IconArrowLeft size={18} />} style={{ alignSelf: 'flex-start' }} onClick={onBack} data-testid="add-media-back">
        {t('backToMedia')}
      </Button>
      {item === null ? (
        <Text size="sm" c="dimmed">
          {t('thatFileIsNo')}
        </Text>
      ) : view.kind === 'preview' ? (
        <>
          <Text size="sm" fw={600} lineClamp={1}>
            {item.fileName}
          </Text>
          <SourceViewer projectId={projectId} item={item} onMarksChange={setMarks} />
          <Group gap="xs" wrap="wrap">
            <PlaceModeButton mode="insert" minHeight={44} size="sm" fontSize="sm" showIcon={false} variant="filled" disabled={readOnly} onPlace={() => onPlace(item, 'insert', marks)} testId="preview-insert" />
            <PlaceModeButton mode="append" minHeight={44} size="sm" fontSize="sm" showIcon={false} variant="default" disabled={readOnly} onPlace={() => onPlace(item, 'append', marks)} testId="preview-append" />
            {item.kind === 'video' ? <PlaceModeButton mode="placeOver" minHeight={44} size="sm" fontSize="sm" showIcon={false} variant="default" disabled={readOnly} onPlace={() => onPlace(item, 'placeOver', marks)} testId="preview-place-over" /> : null}
          </Group>
          {problem ? (
            <p className={classes.sheetProblem} role="alert" data-testid="add-media-problem">
              {problem}
            </p>
          ) : null}
        </>
      ) : (
        <>
          <Text size="sm" fw={600} lineClamp={1}>
            {item.fileName}
          </Text>
          <AnnotationEditor annotation={annotation} onSave={onSaveAnnotation} disabled={readOnly} />
        </>
      )}
    </div>
  );
}
