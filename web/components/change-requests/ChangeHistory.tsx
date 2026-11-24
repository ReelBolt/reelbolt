'use client';

import { useState } from 'react';
import Link from 'next/link';
import { Anchor, Badge, Button, Group, Modal, Paper, Stack, Text, Title } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconArrowRight, IconPencil, IconPlayerPlay } from '@tabler/icons-react';
import { getOutputVideoUrl } from '@/lib/api/outputs';
import { runHref } from '@/lib/api/revisions';
import type { StepChangeRequest } from '@/lib/types/change-requests';
import type { CompositionRevision, CompositionVersion, RevisionHistory, RevisionStatus } from '@/lib/types/revisions';
import { formatCredits } from '@/lib/utils/usage-labels';
import { formatRunDate } from '@/lib/utils/run-summary';
import { msg, useFormat, useT, type Msg } from '@/i18n';
import { InlineJobStatus } from '@/components/activity';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import { isActiveState } from '@/lib/activity/notifier';
import { crCopy, itemKindOf, useCrT, type CrT } from './change-request-copy';
import { CheckedVideo } from '@/components/outputs/CheckedVideo';

/** The same customer-facing words the execution page uses for a run's status. */
export const REVISION_STATUS_LABEL: Record<RevisionStatus, Msg> = {
  Queued: msg('revisions.status.queued'),
  Running: msg('revisions.status.running'),
  Passed: msg('revisions.status.passed'),
  Failed: msg('revisions.status.failed'),
  Cancelled: msg('revisions.status.cancelled'),
};

const REVISION_STATUS_COLOR: Record<RevisionStatus, string> = {
  Queued: 'gray', Running: 'blue', Passed: 'green', Failed: 'red', Cancelled: 'orange',
};

/** What the player shows: a version's finished video, or its animation. */
interface Playing {
  key: string;
  title: string;
  stepResultId: string;
}

/** The finished video a version plays by default: its primary deliverable, else nothing (the animation plays). */
export function primaryDeliverable(version: CompositionVersion) {
  const deliverables = version.deliverables ?? [];
  return deliverables.find((d) => d.isPrimary) ?? deliverables[deliverables.length - 1] ?? null;
}

/**
 * A note under a version whose run should end with a finished video it does not (yet) have: still
 * being made, or the run stopped before it.
 */
export function finalVideoNote(version: CompositionVersion): Msg | null {
  if (!version.finalVideoExpected || (version.deliverables?.length ?? 0) > 0) return null;
  if (version.runStatus === 'Queued' || version.runStatus === 'Running') {
    return msg('revisions.history.noteInProgress');
  }
  return msg('revisions.history.noteMissing');
}


/** One change in the "Changes" list: a video change that has not produced a version, or a change request. */
export type ChangeItem =
  | { kind: 'revision'; at: string; revision: CompositionRevision }
  | { kind: 'request'; at: string; request: StepChangeRequest };

export interface HistoryParts {
  /** Every version of the video, newest first. */
  versions: CompositionVersion[];
  /** Changes newest first, each run once. */
  changes: ChangeItem[];
  /** The change request whose own run is the one being viewed ("this run is a change of ..."). */
  own: StepChangeRequest | null;
}

const time = (at: string) => {
  const ms = new Date(at).getTime();
  return Number.isNaN(ms) ? 0 : ms;
};

/**
 * The run's history in two parts. Versions: every version, newest first. Changes: the change requests, plus the video
 * changes that have not (yet) produced a version (waiting, working, failed or stopped); a finished video change IS its
 * version, so it is listed there only. A combined run writes BOTH a video change and a change request: it is listed once,
 * as the request (matched on the run), whose targets name everything it changes.
 */
export function historyParts(history: RevisionHistory | undefined | null, requests: StepChangeRequest[], executionId: string): HistoryParts {
  const versions = [...(history?.versions ?? [])].sort((a, b) => (time(b.createdAt) - time(a.createdAt)) || (b.versionNumber - a.versionNumber));
  const producedVersionIds = new Set(versions.map((v) => v.id));
  const requestRuns = new Set(requests.map((r) => r.executionId));
  const own = requests.find((r) => r.executionId === executionId) ?? null;
  const changes: ChangeItem[] = [
    ...(history?.revisions ?? [])
      .filter((r) => !(r.resultVersionId && producedVersionIds.has(r.resultVersionId)) && !requestRuns.has(r.executionId))
      .map((revision) => ({ kind: 'revision' as const, at: revision.createdAt, revision })),
    ...requests
      .filter((r) => r.executionId !== executionId)
      .map((request) => ({ kind: 'request' as const, at: request.createdAt, request })),
  ];
  changes.sort((a, b) => (time(b.at) - time(a.at)) || (a.kind === b.kind ? 0 : a.kind === 'revision' ? -1 : 1));
  return { versions, changes, own };
}

export interface ChangeHistoryProps {
  projectId: string;
  /** The run this page shows: the version it produced is highlighted. */
  executionId: string;
  history: RevisionHistory | undefined | null;
  requests: StepChangeRequest[];
  /** "Change from this version": preselect it in the video form. Absent: the action is hidden. */
  onChangeVersion?: (versionId: string) => void;
}

/** The merged history: every version of the video with a player, then the changes asked for, each run once. */
export function ChangeHistory({ projectId, executionId, history, requests, onChangeVersion }: ChangeHistoryProps) {
  const t = useT('revisions.history');
  const tc = useCrT();
  const [playing, setPlaying] = useState<Playing | null>(null);
  const isPhone = useMediaQuery('(max-width: 48em)');
  const { versions, changes, own } = historyParts(history, requests, executionId);
  const changesTitle = tc('historyTitle');
  const ownRunText = tc('historyOwnRun');
  const ownRunLinkText = tc('historyOwnRunLink');
  const canChange = !!onChangeVersion && !!history && history.canRevise && history.availability !== 'Unavailable';

  if (versions.length === 0 && changes.length === 0 && !own) return null;

  return (
    <Stack gap="lg" data-testid="change-history">
      {versions.length > 0 && (
        <section aria-labelledby="change-history-versions" data-testid="revision-history">
          <Title order={3} size="h4" mb="md" id="change-history-versions">{t('title')}</Title>
          <Stack gap="sm" component="ul" m={0} p={0} style={{ listStyle: 'none' }} aria-labelledby="change-history-versions">
            {versions.map((version) => (
              <li key={`v-${version.id}`}>
                <VersionCard
                  projectId={projectId}
                  version={version}
                  isThisRun={version.executionId === executionId}
                  onPlay={(target) => setPlaying(target)}
                  onChange={canChange && version.hasRender ? () => onChangeVersion!(version.id) : undefined}
                />
              </li>
            ))}
          </Stack>
        </section>
      )}

      {(changes.length > 0 || own) && (
        <section aria-labelledby="change-history-changes" data-testid="change-request-history">
          <Title order={3} size="h4" mb="md" id="change-history-changes">{changesTitle}</Title>
          {own && (
            <Text size="sm" mb="sm" data-testid="change-request-lineage">
              {ownRunText}{' '}
              <Anchor component={Link} href={runHref(projectId, own.sourceWorkflowDefinitionId ?? own.workflowDefinitionId, own.sourceExecutionId)} size="sm" style={LINK_STYLE}>
                {ownRunLinkText}
              </Anchor>
            </Text>
          )}
          {changes.length > 0 && (
            <Stack gap="sm" component="ul" m={0} p={0} style={{ listStyle: 'none' }} aria-labelledby="change-history-changes">
              {changes.map((item) => (
                <li key={item.kind === 'revision' ? `r-${item.revision.id}` : `c-${item.request.id}`}>
                  {item.kind === 'revision' ? (
                    <RevisionCard projectId={projectId} revision={item.revision} isThisRun={item.revision.executionId === executionId} />
                  ) : (
                    <ChangeRequestCard projectId={projectId} request={item.request} />
                  )}
                </li>
              ))}
            </Stack>
          )}
        </section>
      )}

      <Modal
        opened={playing !== null}
        onClose={() => setPlaying(null)}
        title={playing?.title ?? ''}
        size="xl"
        centered
        fullScreen={!!isPhone}
        keepMounted={false}
        closeButtonProps={{ size: 44, 'aria-label': t('closePlayer') }}
      >
        {playing && (
          <CheckedVideo
            key={playing.key}
            controls
            autoPlay
            playsInline
            src={getOutputVideoUrl(projectId, playing.stepResultId)}
            aria-label={playing.title}
            data-testid="revision-video"
            style={{ width: '100%', maxHeight: '75vh', background: 'black', borderRadius: 8 }}
          />
        )}
      </Modal>
    </Stack>
  );
}

function VersionCard({
  projectId,
  version,
  isThisRun,
  onPlay,
  onChange,
}: {
  projectId: string;
  version: CompositionVersion;
  isThisRun: boolean;
  onPlay: (target: Playing) => void;
  onChange?: () => void;
}) {
  const t = useT('revisions.history');
  const label = t('version', { number: version.versionNumber });
  const finished = primaryDeliverable(version);
  const animation = version.hasRender && version.renderStepResultId ? version.renderStepResultId : null;
  const note = finalVideoNote(version);
  return (
    <Paper
      withBorder
      p="sm"
      radius="md"
      data-testid="revision-version"
      aria-current={isThisRun ? 'true' : undefined}
      style={isThisRun ? { borderColor: 'var(--mantine-primary-color-filled)', borderWidth: 2 } : undefined}
    >
      <Stack gap={6}>
        <Group gap="xs" justify="space-between" wrap="wrap">
          <Group gap="xs" wrap="wrap">
            <Text fw={700}>{label}</Text>
            {isThisRun && <Badge size="sm" variant="light">{t('madeByThisRun')}</Badge>}
            {version.cutPolicy === 'KeepCut' && version.cutKept === true && (
              <Badge size="sm" variant="light" color="green">{t('sameCut')}</Badge>
            )}
            {version.cutPolicy === 'KeepCut' && version.cutKept === false && (
              <Badge size="sm" variant="light" color="yellow" title={t('editAgainTip')}>{t('editAgain')}</Badge>
            )}
            {version.cutPolicy === 'ReEdit' && <Badge size="sm" variant="light" color="violet">{t('reEdited')}</Badge>}
          </Group>
          <Text size="xs" c="dimmed">{formatRunDate(version.createdAt) ?? ''}</Text>
        </Group>
        <Text size="sm" lineClamp={4} style={{ overflowWrap: 'anywhere' }}>
          {version.instruction ?? t('original')}
        </Text>
        {note && <Text size="xs" c="dimmed" data-testid="revision-final-note">{t.msg(note)}</Text>}
        <Group gap="xs" wrap="wrap">
          {finished && (
            <Button
              size="xs"
              mih={44}
              variant="light"
              leftSection={<IconPlayerPlay size={14} />}
              onClick={() => onPlay({ key: `${version.id}-final`, title: t('finishedTitle', { label }), stepResultId: finished.stepResultId })}
              aria-label={t('playLabel', { label })}
            >
              {t('play')}
            </Button>
          )}
          {animation && (
            <Button
              size="xs"
              mih={44}
              variant={finished ? 'subtle' : 'light'}
              leftSection={<IconPlayerPlay size={14} />}
              onClick={() => onPlay({ key: `${version.id}-animation`, title: finished ? t('animationTitle', { label }) : label, stepResultId: animation })}
              aria-label={finished ? t('playAnimationLabel', { label }) : t('playLabel', { label })}
            >
              {finished ? t('animationOnly') : t('play')}
            </Button>
          )}
          {onChange && (
            <Button size="xs" mih={44} variant="subtle" leftSection={<IconPencil size={14} />} onClick={onChange} aria-label={t('changeLabel', { label })}>
              {t('changeThis')}
            </Button>
          )}
          {version.executionId && version.workflowId && !isThisRun && (
            <Anchor component={Link} href={runHref(projectId, version.workflowId, version.executionId)} size="sm" style={LINK_STYLE}>
              {t('seeRun')} <IconArrowRight size={12} style={{ verticalAlign: 'middle' }} />
            </Anchor>
          )}
        </Group>
      </Stack>
    </Paper>
  );
}

function RevisionCard({ projectId, revision, isThisRun }: { projectId: string; revision: CompositionRevision; isThisRun: boolean }) {
  const t = useT('revisions.history');
  const inFlight = revision.status === 'Queued' || revision.status === 'Running';
  // The activity model's live row for this revision's run: stage, percent and a confirmed stop.
  const job = useJobFor((j) => j.executionId === revision.executionId || (revision.status === 'Queued' && j.type === 'revision' && j.subject?.sourceExecutionId === revision.executionId));
  return (
    <Paper
      withBorder
      p="sm"
      radius="md"
      data-testid="revision-request"
      aria-current={isThisRun ? 'true' : undefined}
      style={isThisRun ? { borderColor: 'var(--mantine-primary-color-filled)', borderWidth: 2 } : undefined}
    >
      <Stack gap={6}>
        <Group gap="xs" justify="space-between" wrap="wrap">
          <Group gap="xs" wrap="wrap">
            <Text fw={700}>{t('changeRequest')}</Text>
            <Badge size="sm" color={REVISION_STATUS_COLOR[revision.status] ?? 'gray'}>
              {REVISION_STATUS_LABEL[revision.status] ? t.msg(REVISION_STATUS_LABEL[revision.status]) : revision.status}
            </Badge>
          </Group>
          <Text size="xs" c="dimmed">{formatRunDate(revision.createdAt) ?? ''}</Text>
        </Group>
        {inFlight && job && isActiveState(job.state) && <InlineJobStatus job={job} />}
        <Text size="sm" lineClamp={4} style={{ overflowWrap: 'anywhere' }}>{revision.instruction}</Text>
        {revision.baseVersionNumber != null && (
          <Text size="xs" c="dimmed">{t('startingFrom', { number: revision.baseVersionNumber })}</Text>
        )}
        {revision.status === 'Failed' && revision.errorMessage && (
          <Text size="sm" c="red" style={{ whiteSpace: 'pre-wrap' }}>{revision.errorMessage}</Text>
        )}
        {!isThisRun && (
          <Anchor component={Link} href={runHref(projectId, revision.workflowId, revision.executionId)} size="sm" style={LINK_STYLE}>
            {inFlight ? t('follow') : t('seeRun')} <IconArrowRight size={12} style={{ verticalAlign: 'middle' }} />
          </Anchor>
        )}
      </Stack>
    </Paper>
  );
}

/** Run links reach the 44px touch target. */
const LINK_STYLE = { display: 'inline-flex', alignItems: 'center', minHeight: 44 } as const;

const CHANGE_STATUS_COLOR: Record<string, string> = {
  Pending: 'gray', Queued: 'gray', Running: 'blue', Completed: 'green', Passed: 'green', Failed: 'red', Cancelled: 'orange',
};

/** "Narration (2 lines) + Video graphics": every target of a request that has several. */
function targetsTitle(tcr: CrT, r: StepChangeRequest): string | null {
  const targets = r.targets ?? [];
  if (targets.length < 2) return null;
  return targets
    .map((x) => {
      if (x.kind === 'film') return tcr('historyFilmTarget');
      return x.itemIds.length > 0
        ? tcr('historyItemsOf', { label: x.stepLabel, kind: itemKindOf(x.stepType, ''), count: x.itemIds.length })
        : x.stepLabel;
    })
    .join(' + ');
}

function ChangeRequestCard({ projectId, request: r }: { projectId: string; request: StepChangeRequest }) {
  const tcr = useCrT();
  const fm = useFormat();
  const tChange = useT('apps.run.change');
  const CR_COPY = crCopy(tcr);
  const first = r.targets?.[0];
  // Never the step type or raw item ids: the step's label, with a count when it names items.
  const fallbackTitle = first
    ? (r.itemIds.length > 0 ? tcr('historyItemsOf', { label: first.stepLabel, kind: itemKindOf(first.stepType, ''), count: r.itemIds.length }) : first.stepLabel)
    : tcr('set.summaryAll');
  // The activity model's live row for this request's run: stage, percent and a confirmed stop.
  const job = useJobFor((j) => j.executionId === r.executionId
    || ((r.status === 'Pending' || r.status === 'Queued') && (j.type === 'changeRequest' || j.type === 'revision') && j.subject?.sourceExecutionId === r.executionId));
  const statusLabel = (status: string): string => {
    switch (status) {
      case 'Pending': case 'Queued': return tcr('history.waiting');
      case 'Running': return tcr('history.working');
      case 'Completed': case 'Passed': return tcr('history.finished');
      case 'Failed': return tcr('history.failed');
      case 'Cancelled': return tcr('history.stopped');
      default: return status;
    }
  };
  return (
    <Paper withBorder p="sm" radius="md" data-testid="change-request-row">
      <Group justify="space-between" wrap="wrap" gap="xs">
        {r.inputChanges && r.inputChanges.length > 0 ? (
          // An app's fields changed (docs/design/app-inputs.md 26): each field before and after, not the re-made steps.
          <Stack gap={0} style={{ minWidth: 0 }} data-testid="change-request-input-changes">
            {r.inputChanges.map((c) => (
              <Text key={c.inputId} size="sm" fw={500} style={{ overflowWrap: 'anywhere' }}>
                {tChange('changeLine', { label: c.label, before: c.before, after: c.after })}
              </Text>
            ))}
          </Stack>
        ) : (
          <Text size="sm" fw={500} style={{ overflowWrap: 'anywhere' }}>
            {targetsTitle(tcr, r) ?? (r.instruction || fallbackTitle)}
          </Text>
        )}
        <Badge variant="light" color={CHANGE_STATUS_COLOR[r.status] ?? 'gray'}>{statusLabel(r.status)}</Badge>
      </Group>
      {job && isActiveState(job.state) && (
        <div style={{ marginTop: 6 }}>
          <InlineJobStatus job={job} />
        </div>
      )}
      <Group gap="md" mt={4} wrap="wrap">
        <Text size="xs" c="dimmed">{formatRunDate(r.createdAt, fm.locale)}</Text>
        {r.plan && <Text size="xs" c="dimmed">{CR_COPY.historyRedone(r.plan.redone)}, {CR_COPY.historyReused(r.plan.reused)}</Text>}
        {r.costCredits !== null && <Text size="xs" c="dimmed">{CR_COPY.historyCost(formatCredits(r.costCredits, fm.locale))}</Text>}
        <Anchor component={Link} href={runHref(projectId, r.workflowDefinitionId, r.executionId)} size="sm" style={LINK_STYLE}>
          {CR_COPY.historyOpen}
        </Anchor>
      </Group>
    </Paper>
  );
}
