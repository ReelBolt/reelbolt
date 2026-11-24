'use client';

import React, { useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import useSWR from 'swr';
import {
  Alert,
  Badge,
  Box,
  Button,
  Collapse,
  FileButton,
  Group,
  Menu,
  Modal,
  Select,
  Switch,
  Skeleton,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconAlertCircle,
  IconArrowBackUp,
  IconChevronDown,
  IconChevronRight,
  IconSearch,
  IconCopy,
  IconDots,
  IconPencil,
  IconTrash,
  IconFileImport,
  IconLink,
  IconMessageCircle,
  IconMovie,
  IconPlayerPlay,
  IconPlus,
  IconTimeline,
} from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import {
  createEditTimeline,
  deleteEditTimeline,
  duplicateEditTimeline,
  editTimelinesKey,
  editTimelinesTrashKey,
  importEditTimeline,
  listDeletedTimelines,
  listEditTimelines,
  renameEditTimeline,
  restoreTimeline,
  type EditTimelineSummary,
} from '@/lib/editing/sync';
import type { EditTimelineSettings } from '@/lib/editing/types';
import { stashImportResult } from '@/lib/editing/interchange/handoff-ui';
import { TimecodeInput } from '../shell/TimecodeInput';
import {
  DEFAULT_FRAME_RATE,
  describeFrameRate,
  FRAME_RATE_CHOICES,
  MATCH_FIRST_CLIP,
  matchFirstClipRate,
  TIMELINE_FORMATS,
} from '@/lib/editing/time';
import { useMediaPool } from '@/lib/hooks/use-media';
import {
  filterTimelines,
  isTimelineSort,
  loadTimelineSort,
  saveTimelineSort,
  sortTimelines,
  TIMELINE_SORT_OPTIONS,
  trashSentence,
  type TimelineSort,
  groupRenderRuns,
} from './timeline-list';
import { GuestActivityBadge } from '../review/GuestActivityBadge';
import { notifyUnplaced, openTimelineForRender, timelineEditorHref, timelineReviewHref } from '@/lib/editing/open-timeline';
import { useProjectTimelines } from '@/lib/hooks/use-timelines';
import { useOutputVideos } from '@/lib/hooks/use-outputs';
import { RENDER_STAGE_LABEL, renderStage, renderTitle } from '@/lib/utils/run-summary';
import { formatDurationSec } from '@/lib/utils/timecode';
import classes from './hub.module.css';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { EditorEmpty, EditorIcon } from '../ui';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { useRelativeTime } from '../ui/useRelativeTime';
import { msg, type Msg } from '@/i18n/msg';

/** The name a new timeline starts with, selected in the field so typing replaces it. */
export const DEFAULT_NEW_NAME = 'Untitled video';
/** How long the "Deleted ... Undo" toast stays (the lane's 8-10 s window). */
export const UNDO_TOAST_MS = 9000;
const undoToastId = (timelineId: string): string => `timeline-undo-${timelineId}`;

const PIXEL_SHAPES: { value: string; label: Msg; num: number; den: number }[] = [
  { value: '1:1', label: msg('editor.hub.timelinesView.pixelShapes.square'), num: 1, den: 1 },
  { value: '10:11', label: msg('editor.hub.timelinesView.pixelShapes.ntsc'), num: 10, den: 11 },
  { value: '12:11', label: msg('editor.hub.timelinesView.pixelShapes.pal'), num: 12, den: 11 },
  { value: '2:1', label: msg('editor.hub.timelinesView.pixelShapes.anamorphic'), num: 2, den: 1 },
];

export interface TimelinesViewProps {
  projectId: string;
}

/** The hub's skeleton rows: the shape of the list that is about to arrive. */
export function HubListSkeleton({ rows = 3 }: { rows?: number }) {
  const tx = useT('editor.hub.timelinesView');
  return (
    <div className={classes.list} aria-busy="true" aria-label={tx('loading')}>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className={classes.row}>
          <Skeleton className={classes.tile} radius={8} />
          <Stack gap={6} style={{ flex: 1 }}>
            <Skeleton height={12} width="40%" radius="sm" />
            <Skeleton height={10} width="24%" radius="sm" />
          </Stack>
        </div>
      ))}
    </div>
  );
}

/**
 * The hub's Videos view: every editable timeline in the project, and every workflow render that
 * can become one. A render opens into the timeline already seeded from it (the user's own edit) or
 * a new one, so "Edit" never starts a second copy behind the user's back.
 */
export function TimelinesView({ projectId }: TimelinesViewProps) {
  const tx = useT('editor.hub.timelinesView');
  const tAll = useT();
  const relativeTime = useRelativeTime();
  const router = useRouter();
  // The list is re-read every time the hub mounts (never served from the 2 s dedupe window): the user comes back from the
  // timeline they just made, and the stale empty state would say there is nothing to edit.
  const { data: timelines, error: timelinesError, isLoading, mutate } = useSWR<EditTimelineSummary[]>(editTimelinesKey(projectId), () => listEditTimelines(projectId), {
    revalidateOnMount: true,
    dedupingInterval: 0,
  });
  const { data: renders } = useProjectTimelines(projectId);
  const { data: outputs } = useOutputVideos(projectId);
  const outputById = useMemo(() => new Map((outputs ?? []).map((o) => [o.stepResultId, o])), [outputs]);

  const { data: trash, mutate: mutateTrash } = useSWR<EditTimelineSummary[]>(editTimelinesTrashKey(projectId), () => listDeletedTimelines(projectId), {
    revalidateOnMount: true,
    dedupingInterval: 0,
  });
  const { data: pool } = useMediaPool(projectId);
  const [trashOpen, setTrashOpen] = useState(false);
  const [restoringId, setRestoringId] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  // Read on first render (the list, and so this control, only renders after the data arrives, never on the server).
  const [sort, setSort] = useState<TimelineSort>(() => loadTimelineSort());

  const [creating, setCreating] = useState(false);
  const [name, setName] = useState(DEFAULT_NEW_NAME);
  const [seed, setSeed] = useState<string | null>(null);
  const [formatKey, setFormatKey] = useState<string>(TIMELINE_FORMATS[0].key);
  const [rateChoice, setRateChoice] = useState<string>('30');
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [pixelAspect, setPixelAspect] = useState<string>('1:1');
  const [startFrames, setStartFrames] = useState(0);
  const [busy, setBusy] = useState(false);
  const [strictImport, setStrictImport] = useState(false);
  // Enter can fire twice before `busy` renders: a ref answers synchronously, so a submit in flight is never repeated.
  const submitting = useRef(false);
  const [openingRender, setOpeningRender] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  // WS-021: rename and delete (with a confirmation) from the row menu.
  const [renaming, setRenaming] = useState<EditTimelineSummary | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const [deleting, setDeleting] = useState<EditTimelineSummary | null>(null);

  const renderLabel = (stepResultId: string): string => {
    const row = (renders ?? []).find((r) => r.stepResultId === stepResultId);
    if (row?.workflowName && row.runNumber != null) {
      const heading = tx('runHeading', { v1: row.workflowName, v2: row.runNumber });
      return row.isFinal === false && row.stepLabel ? `${heading} · ${row.stepLabel}` : heading;
    }
    const out = outputById.get(stepResultId);
    if (out) return renderTitle(out);
    const render = (renders ?? []).find((r) => r.stepResultId === stepResultId);
    return render ? tx('workflowRender', { v1: relativeTime(render.createdAt) }) : tx('aWorkflowRender');
  };

  const seededIds = new Set((timelines ?? []).map((t) => t.seedStepResultId).filter((id): id is string => !!id));
  const [showIntermediate, setShowIntermediate] = useState(false);
  const unseeded = (renders ?? []).filter((r) => !seededIds.has(r.stepResultId));
  const readyRuns = groupRenderRuns(unseeded, showIntermediate);
  const readyRenders = readyRuns.flatMap((g) => g.rows);
  // The new-timeline select lists the same rows (seeded ones included: a render can seed more than one timeline).
  const seedRenders = groupRenderRuns(renders ?? [], showIntermediate).flatMap((g) => g.rows);
  const allTimelines = useMemo(() => timelines ?? [], [timelines]);
  const sortedTimelines = useMemo(() => sortTimelines(filterTimelines(allTimelines, search), sort), [allTimelines, search, sort]);
  const trashed = trash ?? [];

  const matched = useMemo(() => matchFirstClipRate(pool), [pool]);
  const chosenRate = useMemo(() => {
    if (rateChoice === MATCH_FIRST_CLIP) return matched.rate;
    const fps = Number(rateChoice);
    return Number.isFinite(fps) && fps > 0 ? { num: fps, den: 1 } : DEFAULT_FRAME_RATE;
  }, [rateChoice, matched]);

  const openCreate = () => {
    setName(DEFAULT_NEW_NAME);
    setSeed(null);
    setFormatKey(TIMELINE_FORMATS[0].key);
    setRateChoice('30');
    setAdvancedOpen(false);
    setPixelAspect('1:1');
    setStartFrames(0);
    setError(null);
    setCreating(true);
  };

  const buildSettings = (): EditTimelineSettings => {
    const format = TIMELINE_FORMATS.find((f) => f.key === formatKey) ?? TIMELINE_FORMATS[0];
    const aspect = PIXEL_SHAPES.find((p) => p.value === pixelAspect) ?? PIXEL_SHAPES[0];
    return {
      width: format.width,
      height: format.height,
      rate: chosenRate,
      sampleRate: 48000,
      pixelAspect: { num: aspect.num, den: aspect.den },
      aspectPolicy: 'fit',
      startTimecodeFrames: startFrames,
    };
  };

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const created = await createEditTimeline(projectId, {
        name: name.trim() || (seed ? renderLabel(seed) : DEFAULT_NEW_NAME),
        seedStepResultId: seed,
        settings: seed ? null : buildSettings(),
      });
      setCreating(false);
      notifyUnplaced(created.unplacedItems);
      await mutate();
      router.push(timelineEditorHref(projectId, created.id));
    } catch (e) {
      setError(humanizeErrorT(tx, e, tx('theTimelineCouldNot')));
      setBusy(false);
    }
  };

  /** OpenTimelineIO in: a ReelBolt export restores whole; another tool's file imports when every clip's media is in this project. */
  const importOtio = async (file: File | null) => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      let otio: unknown;
      try {
        otio = JSON.parse(await file.text());
      } catch {
        throw new Error(tx('thatFileIsNot'));
      }
      const created = await importEditTimeline(projectId, { name: file.name.replace(/\.(otio|json)$/i, '') || null, otio }, { strict: strictImport });
      stashImportResult(created.id, created);
      await mutate();
      router.push(timelineEditorHref(projectId, created.id));
    } catch (e) {
      if (e instanceof ApiError && e.body && typeof e.body === 'object' && Array.isArray((e.body as { unresolvedUrls?: unknown }).unresolvedUrls)) {
        const urls = (e.body as { unresolvedUrls: string[] }).unresolvedUrls;
        setError(`${humanizeErrorT(tx, e, tx('thatFileCouldNot'))}${urls.length > 0 ? tx('mediaThisProjectDoes', { v1: urls.join(', ') }) : ''}`);
      } else {
        setError(humanizeErrorT(tx, e, tx('theTimelineCouldNot2')));
      }
      setBusy(false);
    }
  };

  const duplicate = async (timeline: EditTimelineSummary) => {
    try {
      await duplicateEditTimeline(projectId, timeline.id, `${timeline.name} copy`);
      await mutate();
      notifications.show({ color: 'violet', message: tx('duplicated', { name: timeline.name }) });
    } catch (e) {
      setError(humanizeErrorT(tx, e, tx('theTimelineCouldNot3')));
    }
  };

  const startRename = (timeline: EditTimelineSummary) => {
    setRenameValue(timeline.name);
    setRenaming(timeline);
  };

  const rename = async () => {
    if (!renaming || submitting.current) return;
    const next = renameValue.trim();
    if (!next) return;
    submitting.current = true;
    setBusy(true);
    try {
      await renameEditTimeline(projectId, renaming.id, next);
      await mutate();
      notifications.show({ color: 'violet', message: tx('renamedTo', { next }) });
      setRenaming(null);
    } catch (e) {
      setError(humanizeErrorT(tx, e, tx('theTimelineCouldNot4')));
      setRenaming(null);
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!deleting || submitting.current) return;
    submitting.current = true;
    setBusy(true);
    try {
      const gone = deleting;
      await deleteEditTimeline(projectId, gone.id);
      await Promise.all([mutate(), mutateTrash()]);
      setDeleting(null);
      showUndoToast(gone);
    } catch (e) {
      setError(humanizeErrorT(tx, e, tx('theTimelineCouldNot5')));
      setDeleting(null);
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  };

  const undoDelete = async (timeline: EditTimelineSummary) => {
    notifications.hide(undoToastId(timeline.id));
    try {
      await restoreTimeline(projectId, timeline.id);
      await Promise.all([mutate(), mutateTrash()]);
      notifications.show({ color: 'violet', message: tx('isBack', { name: timeline.name }) });
    } catch (e) {
      setError(describeError(tAll, e, tx('theTimelineCouldNot6')));
    }
  };

  const showUndoToast = (timeline: EditTimelineSummary) => {
    notifications.show({
      id: undoToastId(timeline.id),
      color: 'violet',
      autoClose: UNDO_TOAST_MS,
      withCloseButton: true,
      message: (
        <Group gap="sm" wrap="nowrap" justify="space-between" data-testid="timeline-undo-toast">
          <Text size="sm">{tx('deleted', { name: timeline.name })}</Text>
          <Button size="compact-sm" fz="xs" variant="light" className={classes.touchTarget} onClick={() => void undoDelete(timeline)} data-testid="timeline-undo-delete">
            {tx('undo')}
          </Button>
        </Group>
      ),
    });
  };

  const restoreFromTrash = async (timeline: EditTimelineSummary) => {
    setRestoringId(timeline.id);
    try {
      await restoreTimeline(projectId, timeline.id);
      await Promise.all([mutate(), mutateTrash()]);
      notifications.show({ color: 'violet', message: tx('restored', { name: timeline.name }) });
    } catch (e) {
      setError(describeError(tAll, e, tx('theTimelineCouldNot7')));
    } finally {
      setRestoringId(null);
    }
  };

  const copyReviewLink = async (timeline: EditTimelineSummary) => {
    const url = `${window.location.origin}/app${timelineReviewHref(projectId, timeline.id)}`;
    try {
      await navigator.clipboard.writeText(url);
      notifications.show({ color: 'violet', message: tx('reviewLinkCopied') });
    } catch {
      notifications.show({ color: 'yellow', message: url, title: tx('copyThisReviewLink') });
    }
  };

  const editRender = async (stepResultId: string) => {
    setOpeningRender(stepResultId);
    setError(null);
    try {
      const id = await openTimelineForRender(projectId, stepResultId, renderLabel(stepResultId));
      await mutate();
      router.push(timelineEditorHref(projectId, id));
    } catch (e) {
      setError(humanizeErrorT(tx, e, tx('thisRenderCouldNot')));
      setOpeningRender(null);
    }
  };

  const importButton = (variant: 'default' | 'subtle') => (
    <FileButton onChange={(file) => void importOtio(file)} accept=".otio,application/json">
      {(props) => (
        <Button {...props} variant={variant} color={variant === 'subtle' ? 'gray' : undefined} size="sm" fz="xs" className={classes.touchTarget} leftSection={<IconFileImport size={16} stroke={1.6} />} disabled={busy} data-testid="timeline-import">
          {tx('import')}
        </Button>
      )}
    </FileButton>
  );

  const actions = (
    <Group gap="xs" wrap="wrap" justify="flex-end">
      <Switch size="xs" label={tx('importFailsIfAnything')} checked={strictImport} onChange={(e) => setStrictImport(e.currentTarget.checked)} disabled={busy} data-testid="timeline-import-strict" />
      {importButton('default') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
      <Button size="sm" fz="xs" className={classes.touchTarget} leftSection={<IconPlus size={16} stroke={1.8} />} onClick={openCreate} data-testid="timeline-create">
        {tx('newTimeline')}
      </Button>
    </Group>
  );

  const nothingYet = !isLoading && !timelinesError && allTimelines.length === 0 && readyRenders.length === 0;

  return (
    <Stack gap="xl" className={classes.enter}>
      {error && (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={16} />} withCloseButton onClose={() => setError(null)} data-testid="timelines-error">
          {error}
        </Alert>
      )}

      <Stack gap="sm">
        <Group justify="space-between" align="center" wrap="wrap" gap="sm">
          <Text className={classes.sectionTitle}>{tx('timelines')}</Text>
          {!nothingYet && actions}
        </Group>

        {isLoading ? (
          <HubListSkeleton />
        ) : timelinesError ? (
          <Alert color="yellow" variant="light" icon={<IconAlertCircle size={16} />}>
            {tx('theTimelinesCouldNot')} {humanizeErrorT(tx, timelinesError, tx('tryAgainInA'))}
          </Alert>
        ) : nothingYet ? (
          <div className={classes.empty} data-testid="timelines-empty">
            <Stack align="center" gap="sm">
              <EditorEmpty
                icon={IconTimeline}
                title={tx('nothingToEditYet')}
                hint={tx('startABlankTimeline')}
                action={{ label: tx('newTimeline2'), onClick: openCreate, testId: 'timeline-create' }}
              />
              <Group gap="xs">
                <Button component={Link} href={`/projects/${projectId}?tab=workflows`} size="sm" fz="xs" className={classes.touchTarget} variant="default" leftSection={<IconPlayerPlay size={15} />}>
                  {tx('runAWorkflow')}
                </Button>
                {importButton('subtle') /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
              </Group>
            </Stack>
          </div>
        ) : allTimelines.length === 0 ? (
          <EditorEmpty compact icon={IconTimeline} title={tx('noTimelinesYet')} hint={tx('pickARenderBelow')} action={{ label: tx('newTimeline3'), onClick: openCreate }} />
        ) : (
          <>
          <Group gap="sm" wrap="wrap" align="flex-end">
            <TextInput
              aria-label={tx('searchTimelines')}
              placeholder={tx('searchByName')}
              value={search}
              onChange={(e) => setSearch(e.currentTarget.value)}
              leftSection={<IconSearch size={16} stroke={1.6} />}
              classNames={{ input: classes.touchInput }}
              style={{ flex: '1 1 14rem' }}
              data-testid="timeline-search"
            />
            <Select
              aria-label={tx('sortTimelines')}
              value={sort}
              data={TIMELINE_SORT_OPTIONS.map((o) => ({ value: o.value, label: tx(`sort.${o.value}`) }))}
              allowDeselect={false}
              comboboxProps={{ withinPortal: true }}
              onChange={(value) => {
                if (isTimelineSort(value)) {
                  setSort(value);
                  saveTimelineSort(value);
                }
              }}
              classNames={{ input: classes.touchInput }}
              style={{ flex: '0 1 11rem' }}
              data-testid="timeline-sort"
            />
          </Group>
          {sortedTimelines.length === 0 ? (
            <Text size="sm" c="dimmed" data-testid="timelines-no-match">
              {tx('noTimelinesMatchQuery', { query: search.trim() })}
            </Text>
          ) : (
          <div className={classes.list} role="list" aria-label={tx('timelines2')} data-testid="timelines-table">
            {sortedTimelines.map((t) => (
              <div key={t.id} className={classes.row} role="listitem" data-testid={`timeline-row-${t.id}`}>
                <Link href={timelineEditorHref(projectId, t.id)} className={classes.rowLink} data-testid={`timeline-open-${t.id}`} aria-label={tx('open', { name: t.name })}>
                  <span className={`${classes.tile} ${classes.tileAccent}`} aria-hidden>
                    <IconTimeline size={20} stroke={1.5} />
                  </span>
                  <Box style={{ minWidth: 0 }}>
                    <Text size="sm" fw={600} truncate>
                      {t.name}
                    </Text>
                    <Text className={classes.meta} truncate>
                      {tx('edited')} {relativeTime(t.updatedAt).toLowerCase()}
                      <span className={classes.hideOnPhone}> · {t.seedStepResultId ? `from ${renderLabel(t.seedStepResultId)}` : tx('startedBlank')}</span>
                    </Text>
                  </Box>
                </Link>
                <GuestActivityBadge projectId={projectId} timelineId={t.id} />
                <Text className={`${classes.meta} ${classes.hideOnPhone}`} style={{ whiteSpace: 'nowrap' }}>
                  {tx('revision', { revision: t.revision })}
                </Text>
                <Menu position="bottom-end" withinPortal classNames={{ item: classes.menuItem }}>
                  <Menu.Target>
                    <EditorIcon icon={IconDots} size="lg" className={classes.menuButton} label={tx('moreFor', { name: t.name })} data-testid={`timeline-more-${t.id}`} />
                  </Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => startRename(t)} data-testid={`timeline-rename-${t.id}`}>
                      {tx('rename')}
                    </Menu.Item>
                    <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => void duplicate(t)}>
                      {tx('duplicate')}
                    </Menu.Item>
                    <Menu.Item leftSection={<IconMessageCircle size={14} />} component={Link} href={timelineReviewHref(projectId, t.id)}>
                      {tx('openReviewPage')}
                    </Menu.Item>
                    <Menu.Item leftSection={<IconLink size={14} />} onClick={() => void copyReviewLink(t)}>
                      {tx('copyReviewLink')}
                    </Menu.Item>
                    <Menu.Divider />
                    <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={() => setDeleting(t)} data-testid={`timeline-delete-${t.id}`}>
                      {tx('delete')}
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              </div>
            ))}
          </div>
          )}
          </>
        )}
      </Stack>

      {!isLoading && !timelinesError && trashed.length > 0 && (
        <Stack gap="sm" data-testid="timelines-trash">
          <Button
            variant="subtle"
            color="gray"
            size="sm" fz="xs"
            justify="flex-start"
            className={classes.touchTarget}
            leftSection={trashOpen ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
            onClick={() => setTrashOpen((open) => !open)}
            aria-expanded={trashOpen}
            data-testid="timelines-trash-toggle"
          >
            {tx('recentlyDeleted', { length: trashed.length })}
          </Button>
          <Collapse expanded={trashOpen}>
            <div className={classes.list} role="list" aria-label={tx('recentlyDeleted2')} data-testid="timelines-trash-list">
              {trashed.map((t) => (
                <div key={t.id} className={classes.row} role="listitem" data-testid={`timeline-trash-row-${t.id}`}>
                  <span className={classes.tile} aria-hidden>
                    <IconTrash size={20} stroke={1.5} />
                  </span>
                  <Box style={{ minWidth: 0, flex: 1 }}>
                    <Text size="sm" fw={600} truncate>
                      {t.name}
                    </Text>
                    <Text className={classes.meta}>{t.deletedAt && t.purgesAt ? trashSentence(t.deletedAt, t.purgesAt) : tx('deleted2')}</Text>
                  </Box>
                  <Button
                    size="sm" fz="xs"
                    variant="light"
                    className={classes.touchTarget}
                    leftSection={<IconArrowBackUp size={16} />}
                    loading={restoringId === t.id}
                    disabled={restoringId !== null && restoringId !== t.id}
                    onClick={() => void restoreFromTrash(t)}
                    aria-label={tx('restore2', { name: t.name })}
                    data-testid={`timeline-restore-${t.id}`}
                  >
                    {tx('restore')}
                  </Button>
                </div>
              ))}
            </div>
          </Collapse>
        </Stack>
      )}

      {readyRenders.length > 0 && (
        <Stack gap="sm">
          <Stack gap={2}>
            <Text className={classes.sectionTitle}>{tx('fromYourWorkflowRuns')}</Text>
            <Text size="xs" c="dimmed">
              {tx('finishedVideosYouHave')}
            </Text>
          </Stack>
          <Switch size="xs" label={tx('showIntermediateSteps')} checked={showIntermediate} onChange={(e) => setShowIntermediate(e.currentTarget.checked)} data-testid="renders-intermediate" />
          <div className={classes.list} role="list" aria-label={tx('workflowRenders')} data-testid="renders-list">
            {readyRuns.flatMap((g) => g.rows.map((r, i) => ({ r, head: showIntermediate && i === 0 ? g.runId : null }))).map(({ r, head }) => {
              const out = outputById.get(r.stepResultId);
              const stage = out ? renderStage(out) : null;
              return (
                <React.Fragment key={r.stepResultId}>
                {head && r.workflowName && r.runNumber != null && (
                  <Text size="xs" c="dimmed" fw={600} data-testid={`render-run-${head}`}>
                    {tx('runHeading', { v1: r.workflowName, v2: r.runNumber })}
                  </Text>
                )}
                <div className={classes.row} role="listitem">
                  <span className={classes.tile} aria-hidden>
                    <IconMovie size={20} stroke={1.5} />
                  </span>
                  <Box style={{ minWidth: 0, flex: 1 }}>
                    <Group gap={8} wrap="nowrap">
                      <Text size="sm" fw={600} truncate>
                        {renderLabel(r.stepResultId)}
                      </Text>
                      {stage && (
                        <Badge size="xs" fz="xs" variant="light" color={stage === 'final' ? 'violet' : 'gray'} style={{ flexShrink: 0 }}>
                          {RENDER_STAGE_LABEL[stage]}
                        </Badge>
                      )}
                    </Group>
                    <Text className={classes.meta}>
                      {r.durationSec ? `${formatDurationSec(r.durationSec)} · ` : ''}
                      {relativeTime(r.createdAt)}
                    </Text>
                  </Box>
                  <Button
                    size="xs" fz="xs"
                    variant="light"
                    onClick={() => void editRender(r.stepResultId)}
                    loading={openingRender === r.stepResultId}
                    disabled={openingRender !== null && openingRender !== r.stepResultId}
                    aria-label={tx.msg(msg('editor-helpers-2.fmt.editRender', { label: renderLabel(r.stepResultId) }))}
                    data-testid={`render-edit-${r.stepResultId}`}
                  >
                    {openingRender === r.stepResultId ? tx('opening') : tx('edit')}
                  </Button>
                </div>
                </React.Fragment>
              );
            })}
          </div>
        </Stack>
      )}

      <Modal opened={renaming !== null} onClose={() => setRenaming(null)} title={tx('renameTimeline')} radius="md" centered>
        <Stack gap="md">
          <TextInput
            label={tx('name')}
            value={renameValue}
            onChange={(e) => setRenameValue(e.currentTarget.value)}
            maxLength={200}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void rename();
            }}
            data-autofocus
            data-testid="timeline-rename-input"
          />
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={() => setRenaming(null)}>
              {tx('cancel')}
            </Button>
            <Button onClick={() => void rename()} loading={busy} disabled={renameValue.trim() === ''} data-testid="timeline-rename-confirm">
              {tx('rename2')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={deleting !== null} onClose={() => setDeleting(null)} title={tx('deleteTimeline')} radius="md" centered>
        <Stack gap="md">
          <Text size="sm" data-testid="timeline-delete-text">
            {tx('movesToRecentlyDeleted', { name: deleting?.name })}
          </Text>
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={() => setDeleting(null)}>
              {tx('cancel2')}
            </Button>
            <Button color="red" onClick={() => void remove()} loading={busy} data-testid="timeline-delete-confirm">
              {tx('delete2')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={creating} onClose={() => setCreating(false)} title={tx('newTimeline4')} radius="md" centered>
        <Stack gap="md">
          <TextInput
            label={tx('name2')}
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
            onFocus={(e) => e.currentTarget.select()}
            placeholder={seed ? renderLabel(seed) : DEFAULT_NEW_NAME}
            maxLength={200}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !busy) void create();
            }}
            data-autofocus
            data-testid="timeline-name"
          />
          <Select
            label={tx('startFrom')}
            description={tx('aWorkflowRenderBrings')}
            placeholder={tx('blankTimeline')}
            clearable
            value={seed}
            onChange={setSeed}
            data={seedRenders.map((r) => ({ value: r.stepResultId, label: renderLabel(r.stepResultId) }))}
            comboboxProps={{ withinPortal: true }}
            data-testid="timeline-seed"
          />
          <Switch size="xs" label={tx('showIntermediateSteps')} checked={showIntermediate} onChange={(e) => setShowIntermediate(e.currentTarget.checked)} data-testid="timeline-seed-intermediate" />
          {seed ? (
            <Text size="sm" c="dimmed" data-testid="timeline-format-seeded">
              {tx('renderTimelineKeepsSize')}
            </Text>
          ) : (
            <>
              <Select
                label={tx('format')}
                value={formatKey}
                onChange={(value) => value && setFormatKey(value)}
                data={TIMELINE_FORMATS.map((f) => ({ value: f.key, label: f.label }))}
                allowDeselect={false}
                comboboxProps={{ withinPortal: true }}
                data-testid="timeline-format"
              />
              <Select
                label={tx('frameRate')}
                description={
                  rateChoice === MATCH_FIRST_CLIP
                    ? matched.matched
                      ? tx('matchesYourFirstClip', { v1: describeFrameRate(matched.rate) })
                      : tx('noneOfYourClips')
                    : tx('framesPerSecond24')
                }
                value={rateChoice}
                onChange={(value) => value && setRateChoice(value)}
                data={[
                  ...FRAME_RATE_CHOICES.map((fps) => ({ value: String(fps), label: tx('framesPerSecond', { fps }) })),
                  { value: MATCH_FIRST_CLIP, label: tx('matchMyFirstClip') },
                ]}
                allowDeselect={false}
                comboboxProps={{ withinPortal: true }}
                data-testid="timeline-fps"
              />
              <Button
                variant="subtle"
                color="gray"
                size="compact-sm" fz="xs"
                justify="flex-start"
                leftSection={advancedOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
                onClick={() => setAdvancedOpen((open) => !open)}
                aria-expanded={advancedOpen}
                data-testid="timeline-advanced-toggle"
              >
                {tx('advanced')}
              </Button>
              <Collapse expanded={advancedOpen}>
                <Stack gap="sm">
                  <Select
                    label={tx('pixelShape')}
                    value={pixelAspect}
                    onChange={(value) => value && setPixelAspect(value)}
                    data={PIXEL_SHAPES.map((p) => ({ value: p.value, label: tx.msg(p.label) }))}
                    allowDeselect={false}
                    comboboxProps={{ withinPortal: true }}
                    data-testid="timeline-pixel-aspect"
                  />
                  <TimecodeInput
                    label={tx('startTimecode')}
                    description={tx('typeDigitsLike1215')}
                    frames={startFrames}
                    rate={chosenRate}
                    onCommit={setStartFrames}
                    data-testid="timeline-start-timecode"
                  />
                </Stack>
              </Collapse>
            </>
          )}
          {error && (
            <Text size="sm" c="red" data-testid="timeline-create-error">
              {error}
            </Text>
          )}
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={() => setCreating(false)}>
              {tx('cancel3')}
            </Button>
            <Button onClick={() => void create()} loading={busy} data-testid="timeline-create-confirm">
              {tx('create')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
