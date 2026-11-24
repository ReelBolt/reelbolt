'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Alert, Box, Button, Checkbox, FileButton, Group, Menu, Progress, SegmentedControl, Select, Stack, Text, TextInput } from '@mantine/core';
import { IconFilter, IconFolder, IconPhotoVideo, IconSearch, IconUpload } from '@tabler/icons-react';
import { EditorEmpty } from '../ui';
import { EditorPanel } from '../ui/EditorPanel';
import { collectDroppedFiles, hasFiles } from '@/lib/files/drop';
import { useUploadQueue, type UploadEntry } from '@/lib/hooks/use-upload-queue';
import classes from './media-pool.module.css';
import { BinTree, isSmartBin } from './BinTree';
import { ClipGrid, type MediaViewMode } from './ClipGrid';
import { FilmstripStrip } from './FilmstripStrip';
import { filmstripDurationSec } from '@/lib/editing/media/filmstrip';
import { formatDuration } from '@/lib/editing/media/format';
import { PaneResizer } from './PaneResizer';
import {
  DEFAULT_POOL_PANES,
  clampPaneWidth,
  poolColumns,
  type PoolPaneState,
} from '@/lib/editing/media/pool-panes';
import { addBinItem, fetchMediaDerivatives, fetchMediaFilmstrip, upsertMediaAnnotation } from '@/lib/editing/api/media';
import { applyDropOptimistically, countsByBinId, planAddToBin } from '@/lib/editing/media/bins';
import { useMediaAnnotations, useMediaBins, useMediaPool } from '@/lib/hooks/use-media';
import {
  browsePool, DEFAULT_BROWSE, indexAnnotations, isBrowsing, matchesSmartFilter,
  type PoolBrowseOptions, type PoolSort, type PoolStateFilter,
} from '@/lib/editing/media/pool-browse';
import type { PlaceChoice } from '../place/placeChoices';
import type {
  FilmstripIndex,
  MediaBin,
  MediaBinMembership,
  MediaDerivativeRow,
  MediaPoolItem,
  MediaRef,
} from '@/lib/editing/media/types';
import { retryWhilePending } from '@/lib/outputs/output-check';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useFormat, useT } from '@/i18n';
import { PoolEnhanceDialog } from '../enhance/PoolEnhanceDialog';
import { SfxLibraryPanel } from './SfxLibraryPanel';
import { SubjectsPanel } from './SubjectsPanel';
import { useAvatarRequirements } from '@/lib/hooks/use-avatars';

/**
 * The seam the detail pane mounts into.
 *
 * `onSelectClip` is the typed callback: it fires with the newly selected pool item, or `null` when
 * the selection is cleared, so a sibling pane can drive itself without this component knowing
 * anything about it. `detailPane` is the slot: given the current selection it returns the node to
 * render to the right of the grid, and `null` means "no clip selected".
 *
 * Both are optional, so the workspace is complete and testable on its own. Neither produces a
 * document op, a revision or a timeline write: the selection is view state and lives only in this
 * component's state.
 */
/** The frame's left and right DOM nodes the pool's bins and detail panes render into (T-003). */
export interface MediaZoneTargets {
  bins: HTMLElement;
  /** Null on a tablet whose right zone is a closed drawer: the detail pane mounts when the drawer opens. */
  detail: HTMLElement | null;
}

export interface MediaPoolProps {
  projectId: string;
  onSelectClip?: (item: MediaPoolItem | null) => void;
  detailPane?: (selected: MediaPoolItem | null) => React.ReactNode;
  /**
   * T-003: when given, the pool renders its bins and viewer+metadata panes into the shared frame's
   * zones and keeps only the pool itself in place. The Media page passes this; the Edit tab and the
   * hub leave it out and keep the pool's own container-query grid (which reflows by width).
   */
  zoneTargets?: MediaZoneTargets | null;
  /**
   * The derivatives version of a source. When it changes for a source this pool has already read,
   * the pool re-reads that source's derivative rows and filmstrip index — how a "Check again" or
   * a pipeline progress event refreshes the filmstrip and its chips with no re-hover.
   */
  derivativesVersionOf?: (mediaRef: MediaRef) => number;
  /** R05.4: put a source on the timeline. Tiles get a Place menu only when this is given. */
  onPlace?: (item: MediaPoolItem, mode: PlaceChoice) => void;
  placeDisabled?: boolean;
  /** A view-only editor: the tile menu has no Enhance and the dialog cannot start a run. */
  readOnly?: boolean;
}

/**
 * Everything the pool reads about one source's derivatives, in one entry.
 *
 * `index` and `indexError` are separate fields rather than a single nullable value because the
 * filmstrip index has three distinct states and the strip renders them differently: still loading
 * (both null), read and empty (`index` present with zero densities), and failed to read
 * (`indexError` set). Collapsing them would make "not loaded yet" indistinguishable from "this
 * source has none", which is exactly the kind of claim this feature refuses to make.
 */
interface ReadsEntry {
  rows: MediaDerivativeRow[] | null;
  error: string | null;
  index: FilmstripIndex | null;
  indexError: string | null;
}


const MEDIA_ACCEPT = 'video/*,audio/*,image/*';

interface PoolUploaderProps {
  entries: readonly UploadEntry[];
  onFiles: (files: File[]) => void;
  onRetry: (id: string) => void;
  onClearFinished: () => void;
  /** The empty state shows a big drop zone; the header shows just the button and the progress rows. */
  variant: 'empty' | 'header';
}

/**
 * R-folded: adding media from the Media workspace itself. A button (the system file picker, which on a phone
 * offers the camera roll), a drop zone, and one progress row per file with a plain error and Retry.
 */
function PoolUploader({ entries, onFiles, onRetry, onClearFinished, variant }: PoolUploaderProps) {
  const t = useT('editor-media.pool.upload');
  const f = useFormat();
  const [dragging, setDragging] = useState(false);
  const busy = entries.some((entry) => entry.status === 'queued' || entry.status === 'uploading');
  const finished = entries.some((entry) => entry.status === 'done' || entry.status === 'error');

  const button = (
    <FileButton onChange={(files) => onFiles(files)} accept={MEDIA_ACCEPT} multiple>
      {(props) => (
        <Button {...props} size="sm" fz="xs" variant={variant === 'empty' ? 'filled' : 'default'} leftSection={<IconUpload size={16} stroke={1.8} />} data-testid="media-upload">
          {t('button')}
        </Button>
      )}
    </FileButton>
  );

  const rows = entries.length > 0 && (
    <Stack gap={6} data-testid="media-upload-rows">
      {entries.map((entry) => (
        <Stack key={entry.id} gap={2} data-testid="media-upload-row" data-status={entry.status}>
          <Group justify="space-between" wrap="nowrap" gap="xs">
            <Text size="sm" truncate>
              {entry.name}
            </Text>
            <Text size="sm" c={entry.status === 'error' ? 'red' : 'dimmed'} style={{ whiteSpace: 'nowrap' }}>
              {entry.status === 'done' ? t('added') : entry.status === 'error' ? t('failed') : entry.status === 'queued' ? t('waiting') : f.percent(entry.progress / 100)}
            </Text>
          </Group>
          {entry.status === 'uploading' || entry.status === 'queued' ? <Progress value={entry.progress} size="xs" aria-label={t('uploading', { name: entry.name })} /> : null}
          {entry.status === 'error' ? (
            <Group gap="xs" wrap="nowrap">
              <Text size="sm" c="red">
                {entry.error ?? t('uploadFailed')}
              </Text>
              {entry.quotaExceeded ? null : (
                <Button size="compact-sm" fz="xs" variant="light" onClick={() => onRetry(entry.id)} data-testid="media-upload-retry">
                  {t('tryAgain')}
                </Button>
              )}
            </Group>
          ) : null}
        </Stack>
      ))}
      {finished && !busy ? (
        <Button size="compact-sm" fz="xs" variant="subtle" color="gray" style={{ alignSelf: 'flex-start' }} onClick={onClearFinished}>
          {t('clearList')}
        </Button>
      ) : null}
    </Stack>
  );

  if (variant === 'header') {
    return (
      <Stack gap="xs" data-testid="media-upload-header">
        {button}
        {rows}
      </Stack>
    );
  }

  return (
    <Stack gap="sm" data-testid="media-upload-empty">
      <Box
        data-testid="media-dropzone"
        data-dragging={dragging}
        onDragOver={(event) => {
          if (!hasFiles(event.nativeEvent)) return;
          event.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(event) => {
          if (!hasFiles(event.nativeEvent)) return;
          event.preventDefault();
          setDragging(false);
          void collectDroppedFiles(event.dataTransfer).then((pending) => onFiles(pending.map((p) => p.file)));
        }}
        style={{
          border: '1px dashed var(--mantine-color-default-border)',
          borderRadius: 12,
          padding: 24,
          background: dragging ? 'var(--rb-ed-subtle)' : undefined,
        }}
      >
        <Stack align="center" gap="xs">
          <EditorEmpty
            compact
            icon={IconPhotoVideo}
            title={t('emptyTitle')}
            hint={t('emptyHint')}
          />
          {button}
        </Stack>
      </Box>
      {rows}
    </Stack>
  );
}

/**
 * The media workspace: the pool (grid / list / filmstrip), the bin tree, and hover-scrub.
 *
 * ## View state is component state, by design
 *
 * The view mode, the selected bin, the selected clip, the hovered clip and the scrub position all
 * live in `useState`/`useRef` here and nowhere else. Nothing this component does can produce an op,
 * a revision or a timeline write — the whole workspace is read-only against the document, which is
 * this initiative's standing rule.
 *
 * ## Reads on hover, writes never
 *
 * Hovering a clip issues at most ONE derivatives read and ONE filmstrip-index read for that
 * `mediaRef`, cached for the life of the view — both GETs, and neither triggered from list render.
 * **No write of any kind is issued from a hover or from a render**, which is the contract decision
 * behind `requestMediaDerivatives`: derivative production is asked for only on an explicit user
 * action (opening a clip in the detail pane, or pressing the button there).
 *
 * ## Honest degradation
 *
 * There is no `<video>` and no `<img>` in the pool: no usable media URL exists until a derivative
 * row has been fetched, and none is fetched until a clip is hovered. Every unknown value renders a
 * stated label ("Not analysed", "Not probed", "not generated yet", "No filmstrip index") rather
 * than an empty cell, a zero, a spinner that cannot resolve, or a broken frame.
 *
 * ## Bin counts are derived from real membership
 *
 * Each bin's count is the number of pool items whose own `bins` list names it
 * (`bins.ts#countsByBinId`) — a fact read off the pool, not the server's `itemCount` placeholder and
 * not an optimistic increment. When a clip is dropped into a bin the pool cache is updated with the
 * pin the server returned, so the count moves because the membership moved.
 */
export function MediaPool({ projectId, onSelectClip, detailPane, zoneTargets = null, derivativesVersionOf, onPlace, placeDisabled, readOnly = false }: MediaPoolProps) {
  const t = useT('editor-media.pool');
  const tSfx = useT('editor-media.sfx');
  const tSub = useT('editor-media.subjects');
  const messageOfNow = (error: unknown): string => humanizeErrorT(t, error, t('requestFailed'));
  const messageOf = useCallback((error: unknown): string => humanizeErrorT(t, error, t('requestFailed')), [t]);
  const { data: pool, error: poolError, isLoading: poolLoading, mutate: mutatePool } =
    useMediaPool(projectId);
  const { data: swrBins, error: binsError, mutate: mutateBins } = useMediaBins(projectId);
  const [enhanceItem, setEnhanceItem] = useState<MediaPoolItem | null>(null);
  const [poolTab, setPoolTab] = useState<'media' | 'sfx' | 'avatars'>('media');
  // The Avatars tab exists only while the project's avatars answer: a 404 means the feature is off here, and the
  // pool then looks exactly as it did.
  const { data: avatarRequirements, error: avatarRequirementsError } = useAvatarRequirements(projectId);
  const avatarsOffered = Boolean(avatarRequirements && !avatarRequirementsError);
  const shownTab = poolTab === 'avatars' && !avatarsOffered ? 'media' : poolTab;
  // A file that finished uploading is re-read into the pool; the server registers it like any other project file. Its ingest
  // probe lands a little later, so the pool is re-read every few seconds (at most two minutes) until no sound or video source
  // is left without one: otherwise a fresh upload says "not analysed" (marks off, sound unknown) until a manual Check again.
  const poolNow = useRef(pool);
  useEffect(() => {
    poolNow.current = pool;
  }, [pool]);
  const probeWatch = useRef<ReturnType<typeof setInterval> | null>(null);
  useEffect(() => () => { if (probeWatch.current) clearInterval(probeWatch.current); }, []);
  const watchProbes = useCallback(() => {
    if (probeWatch.current) clearInterval(probeWatch.current);
    let round = 0;
    probeWatch.current = setInterval(() => {
      round += 1;
      const waiting = (poolNow.current ?? []).some((i) => i.kind !== 'image' && i.probe === null);
      if (round > 40 || (round > 1 && !waiting)) {
        if (probeWatch.current) clearInterval(probeWatch.current);
        probeWatch.current = null;
        return;
      }
      void mutatePool();
    }, 3000);
  }, [mutatePool]);
  const uploads = useUploadQueue(projectId, () => {
    void mutatePool();
    watchProbes();
  });
  const { enqueue } = uploads;
  const addFiles = useCallback(
    (files: File[]) => {
      if (files.length > 0) enqueue(files.map((file) => ({ file, relativePath: file.name })), '');
    },
    [enqueue],
  );

  const [viewMode, setViewMode] = useState<MediaViewMode>('grid');
  // R05.8: pane widths and collapsed state, per visit. T-003 moved their persistence to the Media
  // page's record in the shared layout (the frame owns the zones); the legacy key is never read here.
  const [panes, setPanes] = useState<PoolPaneState>(DEFAULT_POOL_PANES);
  const updatePanes = useCallback((patch: Partial<PoolPaneState>) => {
    setPanes((current) => ({ ...current, ...patch }));
  }, []);
  const [browse, setBrowse] = useState<PoolBrowseOptions>(DEFAULT_BROWSE);
  const { data: swrAnnotations, mutate: mutateAnnotations } = useMediaAnnotations(projectId);
  const annotationIndex = useMemo(() => indexAnnotations(swrAnnotations), [swrAnnotations]);
  const [annotationMessage, setAnnotationMessage] = useState<string | null>(null);
  // One-tap star / flag: saved at once (one PUT of the changed field), the tile flips immediately and
  // flips back with a plain message if the save fails.
  const toggleAnnotation = useCallback(
    async (item: MediaPoolItem, field: 'favourite' | 'flag') => {
      const current = annotationIndex.get(item.mediaRef);
      const next = !(current?.[field] ?? false);
      const previous = swrAnnotations;
      const optimistic = {
        mediaRef: item.mediaRef, keywords: current?.keywords ?? [], note: current?.note ?? null,
        colorLabel: current?.colorLabel ?? null, favourite: current?.favourite ?? false, flag: current?.flag ?? false,
        updatedAt: current?.updatedAt ?? '', [field]: next,
      };
      setAnnotationMessage(null);
      void mutateAnnotations([...(previous ?? []).filter((a) => a.mediaRef !== item.mediaRef), optimistic], { revalidate: false });
      try {
        const saved = await upsertMediaAnnotation(projectId, item.mediaRef, { [field]: next });
        await mutateAnnotations((cur) => [...(cur ?? []).filter((a) => a.mediaRef !== saved.mediaRef), saved], { revalidate: false });
      } catch (error) {
        void mutateAnnotations(previous, { revalidate: false });
        setAnnotationMessage(t('saveAnnotationFailed', { reason: messageOf(error) }));
      }
    },
    [annotationIndex, mutateAnnotations, projectId, swrAnnotations, t, messageOf],
  );
  const [selectedMediaRef, setSelectedMediaRef] = useState<MediaRef | null>(null);
  const [subjectMediaRef, setSubjectMediaRef] = useState<MediaRef | null>(null);
  const [selectedBinId, setSelectedBinId] = useState<string | null>(null);
  const [scrubTimeSec, setScrubTimeSec] = useState(0);

  // The bin list IS the SWR cache entry, so a write is applied optimistically by writing the cache
  // (`revalidate: false`) and rolled back by writing the previous value back. No mirror state and no
  // seeding effect — which also means a real revalidation replaces the optimistic value with the
  // server's, as it should.
  const bins = useMemo(() => swrBins ?? [], [swrBins]);
  const handleBinsChange = useCallback(
    (next: MediaBin[]) => {
      void mutateBins(next, { revalidate: false });
    },
    [mutateBins],
  );

  const items = useMemo(() => pool ?? [], [pool]);

  // ───────────────────────────────────────────────────────────────────────────
  // Bin membership. The pool items carry their own pins, so a pin created or
  // removed anywhere is written straight into the pool cache — no refetch, and no
  // optimistic count that nothing can ever contradict.
  // ───────────────────────────────────────────────────────────────────────────
  const handleMembershipAdded = useCallback(
    (mediaRef: MediaRef, membership: MediaBinMembership) => {
      void mutatePool(
        (current) =>
          (current ?? []).map((item) =>
            item.mediaRef === mediaRef
              ? {
                  ...item,
                  // The same source may be pinned in several bins; only THIS pin is new, and a
                  // re-add of an existing pin returns the existing row rather than a second one.
                  bins: [
                    ...item.bins.filter((pin) => pin.itemId !== membership.itemId),
                    membership,
                  ],
                }
              : item,
          ),
        { revalidate: false },
      );
    },
    [mutatePool],
  );

  const handleMembershipRemoved = useCallback(
    (mediaRef: MediaRef, itemId: string) => {
      void mutatePool(
        (current) =>
          (current ?? []).map((item) =>
            item.mediaRef === mediaRef
              ? { ...item, bins: item.bins.filter((pin) => pin.itemId !== itemId) }
              : item,
          ),
        { revalidate: false },
      );
    },
    [mutatePool],
  );

  const binCounts = useMemo(() => countsByBinId(items), [items]);

  // "Add to bin" from a tile's menu: the same plan and refusals as a drop, with no drag needed.
  const [addToBinMessage, setAddToBinMessage] = useState<string | null>(null);
  const [binListingVersion, setBinListingVersion] = useState(0);
  const handleAddToBin = useCallback(
    async (item: MediaPoolItem, binId: string) => {
      const plan = planAddToBin(item.mediaRef, binId, bins, item.bins);
      if (plan.kind !== 'addItem') {
        setAddToBinMessage(plan.kind === 'none' ? plan.reason : null);
        return;
      }
      const name = bins.find((bin) => bin.id === binId)?.name ?? t('theBin');
      const previous = bins;
      handleBinsChange(applyDropOptimistically(bins, plan));
      try {
        const created = await addBinItem(projectId, plan.binId, plan.mediaRef);
        handleMembershipAdded(plan.mediaRef, { binId: plan.binId, itemId: created.id });
        setBinListingVersion((v) => v + 1);
        setAddToBinMessage(t('addedToBin', { name }));
      } catch (error) {
        handleBinsChange(previous);
        setAddToBinMessage(messageOf(error));
      }
    },
    [bins, projectId, handleBinsChange, handleMembershipAdded, t, messageOf],
  );
  const addToBin = useMemo(
    () => ({ bins, onAdd: (item: MediaPoolItem, binId: string) => void handleAddToBin(item, binId) }),
    [bins, handleAddToBin],
  );

  /** The pins of one clip, read off the pool — what a drop on "All media" needs to remove them. */
  const handlePinsFor = useCallback(
    (mediaRef: MediaRef) => items.find((item) => item.mediaRef === mediaRef)?.bins ?? [],
    [items],
  );

  // ───────────────────────────────────────────────────────────────────────────
  // Hover-scrub: ONE derivatives read and ONE filmstrip-index read per mediaRef,
  // cached for the life of the view. Both are GETs.
  //
  // The "already requested?" guard is a ref ON PURPOSE: it must be synchronous, because ten hover
  // events arriving in the same tick have to issue one request each, not ten — state would not have
  // committed by the time the second handler runs. The guard is written and read only inside
  // callbacks, never during render; the ROWS and the INDEX live in state, which is what the strip
  // renders from.
  // ───────────────────────────────────────────────────────────────────────────
  const requestedMediaRefs = useRef(new Set<MediaRef>());
  const [readsByMediaRef, setReadsByMediaRef] = useState<Record<MediaRef, ReadsEntry>>({});

  const patchEntry = useCallback((mediaRef: MediaRef, patch: Partial<ReadsEntry>) => {
    setReadsByMediaRef((previous) => {
      const existing: ReadsEntry = previous[mediaRef] ?? {
        rows: null,
        error: null,
        index: null,
        indexError: null,
      };
      return { ...previous, [mediaRef]: { ...existing, ...patch } };
    });
  }, []);

  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  const readVersions = useRef(new Map<MediaRef, number>());
  const fetchReads = useCallback(
    (mediaRef: MediaRef) => {
      readVersions.current.set(mediaRef, derivativesVersionOf?.(mediaRef) ?? 0);
      // A source still being checked answers "pending": stay in the loading state and ask again on the back-off.
      const wait = { isCancelled: () => !mounted.current, onPending: () => patchEntry(mediaRef, { error: null }) };
      void retryWhilePending(() => fetchMediaDerivatives(projectId, mediaRef), wait)
        .then((rows) => patchEntry(mediaRef, { rows, error: null }))
        .catch((error: unknown) => patchEntry(mediaRef, { rows: null, error: messageOf(error) }));

      void retryWhilePending(() => fetchMediaFilmstrip(projectId, mediaRef), wait)
        .then((response) =>
          patchEntry(mediaRef, { index: response?.filmstrip ?? null, indexError: null }),
        )
        .catch((error: unknown) => patchEntry(mediaRef, { index: null, indexError: messageOf(error) }));
    },
    [patchEntry, projectId, derivativesVersionOf, messageOf],
  );

  const ensureReads = useCallback(
    (mediaRef: MediaRef) => {
      if (requestedMediaRefs.current.has(mediaRef)) return;
      requestedMediaRefs.current.add(mediaRef);

      // A read that moved keeps every other field, so a slow filmstrip answer cannot wipe rows that
      // already arrived (and vice versa).
      patchEntry(mediaRef, {});
      fetchReads(mediaRef);
    },
    [patchEntry, fetchReads],
  );

  // A source whose derivatives version moved is re-read in place (the previous rows stay on screen
  // until the new ones arrive). Only sources already read are touched: a version bump never turns
  // into a read of something nobody looked at.
  useEffect(() => {
    if (!derivativesVersionOf) return;
    for (const [mediaRef, seen] of readVersions.current) {
      if (derivativesVersionOf(mediaRef) !== seen) fetchReads(mediaRef);
    }
  });

  // ───────────────────────────────────────────────────────────────────────────
  // Who owns the strip's subject, and when it is released
  //
  // The strip is a CONTROL, not a readout: its whole purpose is to be pointed at, so it has to
  // survive the pointer's journey from the tile that named the clip to the strip that scrubs it.
  // Attaching the release to the TILE cannot express that — `mouseleave` on the tile fires the
  // instant the pointer leaves the tile's box, which is the first millimetre of that journey — and
  // QA's browser probe measured exactly that: `cell count=0, idle count=1` after a gradual move.
  //
  // The release is therefore owned by the REGION below, one `Box` that contains both the grid and
  // the strip. Two facts make that the right seam rather than a hover-intent timer:
  //
  //   * A rectangle is convex. A straight pointer path between two points inside it never leaves
  //     it, so the tile→strip journey keeps the subject with no timing assumption at all. A grace
  //     period would have to outrun the user's pointer speed, which is precisely the kind of race
  //     that shows up as an intermittent failure under load.
  //   * The keyboard is not a pointer. Focus is tracked separately (`focusInsideRegion`), and focus
  //     ALONE adopts a subject — so a keyboard user reaches the slider by tabbing through the tiles
  //     and on to it, with no hover anywhere in the path.
  //
  // The refs are not state: they are written and read only inside event handlers, and a release
  // that depended on state would need the two flags to have committed first.
  // ───────────────────────────────────────────────────────────────────────────
  const pointerInsideRegion = useRef(false);
  const focusInsideRegion = useRef(false);

  /** Releases the subject only when NEITHER the pointer nor the focus still holds it. */
  const releaseSubjectIfUnheld = useCallback(() => {
    if (pointerInsideRegion.current || focusInsideRegion.current) return;
    setSubjectMediaRef(null);
    // The strip falls back to the open clip; a scrub position taken on ANOTHER clip means nothing
    // there, so it starts over. Falling back to the same clip keeps the position.
    if (subjectMediaRef !== null && subjectMediaRef !== selectedMediaRef) setScrubTimeSec(0);
  }, [subjectMediaRef, selectedMediaRef]);

  /** Adopts a clip as the strip's subject and starts its reads. Shared by the pointer and the keyboard. */
  const adoptSubject = useCallback(
    (mediaRef: MediaRef) => {
      setSubjectMediaRef(mediaRef);
      setScrubTimeSec(0);
      ensureReads(mediaRef);
    },
    [ensureReads],
  );

  const handleTileHover = useCallback(
    (mediaRef: MediaRef) => {
      pointerInsideRegion.current = true;
      adoptSubject(mediaRef);
    },
    [adoptSubject],
  );

  const handleTileFocus = useCallback(
    (mediaRef: MediaRef) => {
      focusInsideRegion.current = true;
      adoptSubject(mediaRef);
    },
    [adoptSubject],
  );

  // R04.6: a finger scrubbing a tile. The fraction waits for the clip's index, then becomes a position.
  const [touchScrub, setTouchScrub] = useState<{ mediaRef: MediaRef; fraction: number } | null>(null);
  const handleTouchScrub = useCallback(
    (mediaRef: MediaRef, fraction: number) => {
      pointerInsideRegion.current = true;
      if (subjectMediaRef !== mediaRef) adoptSubject(mediaRef);
      setTouchScrub({ mediaRef, fraction });
    },
    [subjectMediaRef, adoptSubject],
  );

  const handleRegionEnter = useCallback(() => {
    pointerInsideRegion.current = true;
  }, []);

  const handleRegionLeave = useCallback(() => {
    pointerInsideRegion.current = false;
    releaseSubjectIfUnheld();
  }, [releaseSubjectIfUnheld]);

  /**
   * Focus left the region entirely — as opposed to moving between two things inside it, which is
   * what tabbing from a tile to the strip's slider looks like and must NOT release the subject.
   */
  const handleRegionBlur = useCallback(
    (event: React.FocusEvent<HTMLDivElement>) => {
      const next = event.relatedTarget as Node | null;
      if (next && event.currentTarget.contains(next)) return;
      focusInsideRegion.current = false;
      releaseSubjectIfUnheld();
    },
    [releaseSubjectIfUnheld],
  );

  const selectedItem = useMemo(
    () => items.find((item) => item.mediaRef === selectedMediaRef) ?? null,
    [items, selectedMediaRef],
  );
  // What the strip shows: the hovered (or focused) clip, else the clip that is OPEN in the detail
  // pane. Without the fallback the strip collapsed to one line the moment the pointer left the
  // grid, and in the stacked phone layout — where the detail pane sits BELOW the strip — everything
  // under it jumped up between the press and the release of a tap on the pane's own buttons, so
  // "Set in" never received its click. Keeping the open clip on the strip keeps the layout still.
  // Its reads already exist: a clip cannot be clicked or tapped without first being hovered or
  // focused, and either one adopted it and started them (selection itself still requests nothing).
  const stripMediaRef = subjectMediaRef ?? selectedMediaRef;
  const subjectItem = useMemo(
    () => items.find((item) => item.mediaRef === stripMediaRef) ?? null,
    [items, stripMediaRef],
  );
  // Read from state during render, which is the only place a value may be read during render.
  const subjectEntry = stripMediaRef ? readsByMediaRef[stripMediaRef] ?? null : null;
  const touchRangeSec = filmstripDurationSec(subjectEntry ? subjectEntry.index : null);
  const touchScrubbing = touchScrub !== null && touchScrub.mediaRef === stripMediaRef;
  const effectiveScrubSec = touchScrubbing && touchRangeSec > 0 ? touchScrub.fraction * touchRangeSec : scrubTimeSec;
  const handleTouchScrubEnd = useCallback(() => {
    setScrubTimeSec(touchRangeSec > 0 && touchScrub ? touchScrub.fraction * touchRangeSec : scrubTimeSec);
    setTouchScrub(null);
  }, [touchRangeSec, touchScrub, scrubTimeSec]);
  const touchScrubLabel =
    touchScrub && touchScrubbing
      ? { mediaRef: touchScrub.mediaRef, text: touchRangeSec > 0 ? t('scrubPosition', { at: formatDuration(effectiveScrubSec), total: formatDuration(touchRangeSec) }) : t('previewLoading') }
      : null;

  const handleSelect = useCallback(
    (item: MediaPoolItem) => {
      const next = item.mediaRef === selectedMediaRef ? null : item;
      setSelectedMediaRef(next ? next.mediaRef : null);
      onSelectClip?.(next);
    },
    [onSelectClip, selectedMediaRef],
  );

  /**
   * The grid contents.
   *
   * Selecting a bin filters the grid to the sources that bin actually holds, read from each item's
   * own `bins` list. A DANGLING pin — one whose source row is gone — is listed in the bin's own item
   * panel but appears in no pool item, so it cannot appear here; the two views are allowed to differ
   * and the panel says so rather than hiding the pin.
   */
  // P6: a SMART bin has no pins; its contents are the sources its saved rule matches. The rule is evaluated here with
  // the same logic as the server (`pool-browse.ts#matchesSmartFilter`), so an edited rule or a new star shows at once.
  const selectedBin = useMemo(() => (selectedBinId === null ? null : bins.find((bin) => bin.id === selectedBinId) ?? null), [bins, selectedBinId]);
  const selectedIsSmart = selectedBin !== null && isSmartBin(selectedBin);
  const binScopedItems = useMemo(
    () =>
      selectedBinId === null
        ? items
        : selectedIsSmart
          ? items.filter((item) => matchesSmartFilter(selectedBin?.smartFilter ?? {}, item, annotationIndex.get(item.mediaRef)))
          : items.filter((item) => item.bins.some((pin) => pin.binId === selectedBinId)),
    [items, selectedBinId, selectedIsSmart, selectedBin, annotationIndex],
  );
  const visibleItems = useMemo(() => browsePool(binScopedItems, annotationIndex, browse), [binScopedItems, annotationIndex, browse]);
  const binNames = useMemo(() => new Map(bins.map((bin) => [bin.id, bin.name])), [bins]);

  // ───────────────────────────────────────────────────────────────────────────
  // States
  // ───────────────────────────────────────────────────────────────────────────

  const sortOptions: { value: PoolSort; label: string }[] = [
    { value: 'added', label: t('sort.added') },
    { value: 'newest', label: t('sort.newest') },
    { value: 'name', label: t('sort.name') },
    { value: 'duration', label: t('sort.duration') },
    { value: 'size', label: t('sort.size') },
  ];
  const stateOptions: { value: PoolStateFilter; label: string }[] = [
    { value: 'all', label: t('state.all') },
    { value: 'starred', label: t('state.starred') },
    { value: 'flagged', label: t('state.flagged') },
    { value: 'unanalysed', label: t('state.unanalysed') },
  ];

  if (poolLoading) {
    return (
      <Text size="sm" c="dimmed" data-testid="media-pool-loading">
        {t('loading')}
      </Text>
    );
  }

  if (poolError) {
    return (
      <Alert color="red" title={t('loadFailed')} data-testid="media-pool-error">
        {messageOfNow(poolError)}
      </Alert>
    );
  }

  if (items.length === 0) {
    return (
      <Stack gap="xs" data-testid="media-pool-empty">
        <PoolUploader variant="empty" entries={uploads.entries} onFiles={addFiles} onRetry={uploads.retry} onClearFinished={uploads.clearFinished} />
      </Stack>
    );
  }

  const cols = poolColumns(panes);

  const selectedBinName =
    selectedBinId === null ? null : bins.find((bin) => bin.id === selectedBinId)?.name ?? null;

  // T-003: the same content renders either in the pool's own grid or into the frame's zones.
  const binsTree = (
    <BinTree
      projectId={projectId}
      bins={bins}
      onBinsChange={handleBinsChange}
      selectedBinId={selectedBinId}
      onSelect={setSelectedBinId}
      countsByBinId={binCounts}
      onMembershipAdded={handleMembershipAdded}
      onMembershipRemoved={handleMembershipRemoved}
      pinsFor={handlePinsFor}
      listingVersion={binListingVersion}
      items={items}
      annotations={annotationIndex}
    />
  );
  const detailBody = detailPane ? (
    detailPane(selectedItem)
  ) : (
    <Text size="sm" c="dimmed">
      {selectedItem
        ? t('selected', { name: selectedItem.fileName })
        : t('selectToSee')}
    </Text>
  );

  const tabsBar = (
    <SegmentedControl
      size="xs"
      fullWidth
      value={shownTab}
      onChange={(v) => setPoolTab(v === 'sfx' ? 'sfx' : v === 'avatars' ? 'avatars' : 'media')}
      aria-label={tSfx('tabs')}
      data={[
        { value: 'media', label: tSfx('tabMedia') },
        { value: 'sfx', label: tSfx('tab') },
        ...(avatarsOffered ? [{ value: 'avatars', label: tSub('tab') }] : []),
      ]}
      data-testid="pool-tabs"
    />
  );

  if (shownTab === 'avatars') {
    return (
      <div className={classes.wrap} data-testid="media-pool-avatars">
        {tabsBar}
        <SubjectsPanel projectId={projectId} readOnly={readOnly} />
      </div>
    );
  }

  if (shownTab === 'sfx') {
    return (
      <div className={classes.wrap} data-testid="media-pool-sfx">
        {tabsBar}
        <SfxLibraryPanel
          projectId={projectId}
          readOnly={readOnly || placeDisabled}
          onPlaceItem={onPlace ? (item) => { onPlace(item, 'insert'); return null; } : undefined}
        />
      </div>
    );
  }

  return (
    <div
      className={classes.wrap}
      data-testid="media-pool-dropzone"
      onDragOver={(event) => {
        if (hasFiles(event.nativeEvent)) event.preventDefault();
      }}
      onDrop={(event) => {
        if (!hasFiles(event.nativeEvent)) return;
        event.preventDefault();
        void collectDroppedFiles(event.dataTransfer).then((pending) => addFiles(pending.map((p) => p.file)));
      }}
    >
    {tabsBar}
    {!zoneTargets && (
    <Group className={classes.paneBar} data-testid="pool-pane-bar">
      <Button
        size="compact-sm"
        fz="xs"
        variant="default"
        className={classes.paneToggle}
        onClick={() => updatePanes({ binsCollapsed: !panes.binsCollapsed })}
        data-testid="pool-toggle-bins"
      >
        {panes.binsCollapsed ? t('showBins') : t('hideBins')}
      </Button>
      <Button
        size="compact-sm"
        fz="xs"
        variant="default"
        className={classes.paneToggle}
        onClick={() => updatePanes({ detailCollapsed: !panes.detailCollapsed })}
        data-testid="pool-toggle-detail"
      >
        {panes.detailCollapsed ? t('showDetails') : t('hideDetails')}
      </Button>
    </Group>
    )}
    <div
      className={zoneTargets ? classes.zonePool : classes.grid}
      data-testid="media-pool"
      data-sized={zoneTargets ? undefined : ''}
      data-bins-collapsed={!zoneTargets && panes.binsCollapsed ? '' : undefined}
      data-detail-collapsed={!zoneTargets && panes.detailCollapsed ? '' : undefined}
      style={zoneTargets ? undefined : ({ '--pool-cols-two': cols.two, '--pool-cols-three': cols.three } as React.CSSProperties)}
    >
      {zoneTargets ? (
        createPortal(
          <Box className={classes.zonePane}>{binsTree}</Box>,
          zoneTargets.bins,
        )
      ) : (
      <Box className={classes.bins}>
        <PaneResizer
          pane="bins"
          width={panes.binsWidth}
          label={t('resizeBins')}
          onResize={(width) => updatePanes({ binsWidth: clampPaneWidth('bins', width) })}
          onCollapse={() => updatePanes({ binsCollapsed: true })}
        />
        <BinTree
          projectId={projectId}
          bins={bins}
          onBinsChange={handleBinsChange}
          selectedBinId={selectedBinId}
          onSelect={setSelectedBinId}
          countsByBinId={binCounts}
          onMembershipAdded={handleMembershipAdded}
          onMembershipRemoved={handleMembershipRemoved}
          pinsFor={handlePinsFor}
          listingVersion={binListingVersion}
          items={items}
          annotations={annotationIndex}
        />
      </Box>
      )}

      <EditorPanel
        title={selectedBinName ? t('mediaInBin', { name: selectedBinName }) : t('mediaPool')}
        description={
          selectedBinName ? (
            <Text size="xs" c="dimmed" data-testid="media-pool-bin-filter">
              {selectedIsSmart ? t('smartBinFilter') : t('binFilter')}
            </Text>
          ) : undefined
        }
        actions={
          <>
            <PoolUploader variant="header" entries={uploads.entries} onFiles={addFiles} onRetry={uploads.retry} onClearFinished={uploads.clearFinished} />
            <SegmentedControl
              size="xs"
              value={viewMode}
              onChange={(value) => setViewMode(value as MediaViewMode)}
              data={[
                { label: t('viewGrid'), value: 'grid' },
                { label: t('viewList'), value: 'list' },
                { label: t('viewFilmstrip'), value: 'filmstrip' },
              ]}
            />
          </>
        }
        testId="media-pool-surface"
      >
      <Stack gap="sm">
        <Group gap="xs" align="flex-end" wrap="wrap" data-testid="media-browse-bar">
          <TextInput
            size="xs"
            style={{ flex: '1 1 140px', minWidth: 0 }}
            placeholder={t('searchPlaceholder')}
            aria-label={t('searchAria')}
            leftSection={<IconSearch size={14} />}
            value={browse.query}
            onChange={(event) => setBrowse({ ...browse, query: event.currentTarget.value })}
            data-testid="media-search"
          />
          <Select
            size="xs"
            w={150}
            aria-label={t('sortAria')}
            allowDeselect={false}
            data={sortOptions}
            value={browse.sort}
            onChange={(value) => value && setBrowse({ ...browse, sort: value as PoolSort })}
            data-testid="media-sort"
          />
          <Menu position="bottom-end" withinPortal closeOnItemClick={false}>
            <Menu.Target>
              <Button size="xs" fz="xs" variant={browse.kinds.length > 0 || browse.state !== 'all' ? 'light' : 'default'} leftSection={<IconFilter size={14} />} data-testid="media-filter-button">
                {t('filter', { count: browse.kinds.length + (browse.state !== 'all' ? 1 : 0) })}
              </Button>
            </Menu.Target>
            <Menu.Dropdown p="sm" data-testid="media-filter-menu">
              <Stack gap="xs">
                <Text size="xs" fw={600}>{t('kind')}</Text>
                {/* eslint-disable-next-line i18next/no-literal-string -- ids, not text */}
                {(['video', 'audio', 'image'] as const).map((kind) => (
                  <Checkbox
                    key={kind}
                    size="sm"
                    label={kind === 'video' ? t('kindVideo') : kind === 'audio' ? t('kindAudio') : t('kindImage')}
                    checked={browse.kinds.includes(kind)}
                    onChange={(event) =>
                      setBrowse({ ...browse, kinds: event.currentTarget.checked ? [...browse.kinds, kind] : browse.kinds.filter((k) => k !== kind) })
                    }
                    data-testid={`media-filter-kind-${kind}`}
                  />
                ))}
                <Select
                  size="xs"
                  label={t('show')}
                  allowDeselect={false}
                  comboboxProps={{ withinPortal: false }}
                  data={stateOptions}
                  value={browse.state}
                  onChange={(value) => value && setBrowse({ ...browse, state: value as PoolStateFilter })}
                  data-testid="media-filter-state"
                />
                <Button size="compact-sm" fz="xs" variant="subtle" disabled={!isBrowsing(browse)} onClick={() => setBrowse({ ...DEFAULT_BROWSE, sort: browse.sort })}>
                  {t('clearFilters')}
                </Button>
              </Stack>
            </Menu.Dropdown>
          </Menu>
        </Group>

        {/*
          The hover region — the grid AND the strip inside one bounding box, which is what lets the
          subject survive the pointer's journey from a tile to the strip. See the release-ownership
          comment above `pointerInsideRegion` for why the seam is here and not on the tile.
        */}
        <Box
          data-testid="media-hover-region"
          onMouseEnter={handleRegionEnter}
          onMouseLeave={handleRegionLeave}
          onBlur={handleRegionBlur}
        >
          <Stack gap="sm">
            {addToBinMessage ? (
              <Text size="sm" role="status" data-testid="media-add-to-bin-message">
                {addToBinMessage}
              </Text>
            ) : null}
            {annotationMessage ? (
              <Text size="sm" c="red" role="status" data-testid="media-annotation-message">
                {annotationMessage}
              </Text>
            ) : null}
            {visibleItems.length === 0 ? (
              <EditorEmpty
                compact
                icon={isBrowsing(browse) ? IconSearch : IconFolder}
                title={isBrowsing(browse) ? t('noMatches') : t('binEmpty')}
                action={isBrowsing(browse) ? { label: t('clearFilters'), onClick: () => setBrowse({ ...DEFAULT_BROWSE, sort: browse.sort }), testId: 'media-clear-filters' } : undefined}
                testId="media-pool-bin-empty"
              />
            ) : (
              /*
                The tile list is BOUNDED and scrolls, so the scrubber below it can never be pushed
                off screen — a reachability property, not a cosmetic one.

                Measured at the 1280x800 viewport the browser check runs at: an unbounded grid of
                three 190px tiles wraps to three rows (454px), which puts the strip's cell at
                viewport y=857 — below the fold — and `document.elementFromPoint` at that cell's own
                centre returns NULL. A pointer cannot travel to a point outside the viewport, so the
                cell was as unreachable as when the tile's `mouseleave` unmounted it — and the
                failure returns the moment the pool holds more than a handful of clips, because the
                grid grows with the pool while the strip only moves further down.

                Capping the tile area in `vh` rather than in pixels keeps that relationship true at
                every window size instead of only at the one the spec happens to use. The pool is a
                bin, not a page; it is the scrubber that has to stay in view.
              */
              <Box
                data-testid="media-clip-scroll"
                style={{ maxHeight: 'min(34vh, 320px)', overflowY: 'auto' }}
              >
                <ClipGrid
                  items={visibleItems}
                  viewMode={viewMode}
                  selectedMediaRef={selectedMediaRef}
                  subjectMediaRef={subjectMediaRef}
                  onSelect={handleSelect}
                  onHover={handleTileHover}
                  onFocusTile={handleTileFocus}
                  onTouchScrub={handleTouchScrub}
                  onTouchScrubEnd={handleTouchScrubEnd}
                  touchScrubLabel={touchScrubLabel}
                  addToBin={addToBin}
                  annotations={annotationIndex}
                  onToggleStar={(item) => void toggleAnnotation(item, 'favourite')}
                  onToggleFlag={(item) => void toggleAnnotation(item, 'flag')}
                  onPlace={onPlace}
                  onEnhance={readOnly ? undefined : setEnhanceItem}
                  placeDisabled={placeDisabled}
                  projectId={projectId}
                  binNames={binNames}
                />
                <PoolEnhanceDialog projectId={projectId} item={enhanceItem} readOnly={readOnly} onClose={() => setEnhanceItem(null)} />
              </Box>
            )}

            <FilmstripStrip
              item={subjectItem}
              index={subjectEntry ? subjectEntry.index : null}
              indexError={subjectEntry ? subjectEntry.indexError : null}
              derivatives={subjectEntry ? subjectEntry.rows : null}
              derivativesError={subjectEntry ? subjectEntry.error : null}
              timeSec={effectiveScrubSec}
              onScrub={setScrubTimeSec}
            />
          </Stack>
        </Box>

        {binsError ? (
          <Text size="xs" c="red" data-testid="media-bins-error">
            {t('binsError', { reason: messageOfNow(binsError) })}
          </Text>
        ) : null}
      </Stack>
      </EditorPanel>

      {zoneTargets ? (
        zoneTargets.detail ? (
          createPortal(
            <Box className={classes.zonePane}>
              <Box data-testid="media-detail-pane">{detailBody}</Box>
            </Box>,
            zoneTargets.detail,
          )
        ) : null
      ) : (
        <Box data-testid="media-detail-pane" className={classes.detail}>
          <PaneResizer
            pane="detail"
            width={panes.detailWidth}
            label={t('resizeDetails')}
            onResize={(width) => updatePanes({ detailWidth: clampPaneWidth('detail', width) })}
            onCollapse={() => updatePanes({ detailCollapsed: true })}
          />
          {detailBody}
        </Box>
      )}
    </div>
    </div>
  );
}
