'use client';
/* eslint-disable i18next/no-literal-string -- the remaining literals are ids, enum keys and t.rich tag handlers, not user-visible text */

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { Badge, Button, Group, Stack, Text } from '@mantine/core';
import { IconArrowRight, IconPlus } from '@tabler/icons-react';
import { ExecStatus } from '@/components/dashboard/sections';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import { NudgeList, type Nudge } from '@/components/dashboard/NudgeButton';
import dash from '@/components/dashboard/dashboard.module.css';
import { OutputModal } from '@/components/outputs/OutputModal';
import { OutputPlayer, type PlayableOutput } from '@/components/outputs/OutputPlayer';
import { OutputRail } from '@/components/outputs/OutputRail';
import { EarlierTakes } from '@/components/outputs/EarlierTakes';
import { FootageReadyCallout } from '@/components/workflow-apps/FootageReadyCallout';
import { StepTypeBadge } from '@/components/workflows/StepTypeBadge';
import type { OutputVideo } from '@/lib/api/outputs';
import { groupTakes } from '@/lib/outputs/takes';
import { timeAgo } from '@/lib/files/model';
import { formatBytes } from '@/lib/utils/bytes';
import { runFailureText } from '@/lib/utils/run-failure';
import { useDismissedRunFailures } from '@/lib/hooks/use-dismissed-runs';
import { executionHref, type DashboardExecution, type ProjectOverview } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';
import type { StepType, WorkflowDefinition } from '@/lib/types/workflow';
import { useFormat, useT } from '@/i18n';

export function toPlayable(projectId: string, projectName: string, o: OutputVideo): PlayableOutput {
  return {
    stepResultId: o.stepResultId, projectId, projectName, fileName: o.fileName, producedAt: o.producedAt,
    workflowName: o.workflowName, runNumber: o.runNumber, runStartedAt: o.runStartedAt, executionStatus: o.executionStatus,
    isFinal: o.isFinal, durationSec: o.durationSec, width: o.width, height: o.height, warnings: o.warnings,
    runHref: o.workflowDefinitionId
      ? `/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(o.workflowDefinitionId)}/executions/${encodeURIComponent(o.workflowExecutionId)}`
      : undefined,
  };
}

function Figure({ label, value, hint }: { label: string; value: string | number; hint?: string }) {
  return (
    <div className={dash.figure}>
      <span className={dash.figureValue}>{value}</span>
      <span className={dash.figureLabel}>{label}</span>
      {hint && <span className={dash.figureHint}>{hint}</span>}
    </div>
  );
}

export function ProjectNudges({ project, overview, workflowCount }: { project: Project; overview?: ProjectOverview; workflowCount: number }) {
  const t = useT('projects.sections');
  const nudges = useMemo<Nudge[]>(() => {
    const ctx = { projectId: project.id };
    const out: Nudge[] = [];
    const files = overview?.sourceFileCount ?? 0;
    if (files > 0 && workflowCount === 0) {
      out.push({
        key: 'suggest-workflow', label: t('nudge.suggestWorkflow'), context: ctx,
        prompt: t('nudge.suggestWorkflowPrompt', { name: project.name }),
      });
    } else if (files > 0) {
      out.push({
        key: 'improve-workflow', label: t('nudge.improveWorkflow'), context: ctx, send: true,
        prompt: t('nudge.improveWorkflowPrompt', { name: project.name }),
      });
    }
    if (files > 0) {
      out.push({ key: 'summarize', label: t('nudge.summarize'), context: ctx, send: true, prompt: t('nudge.summarizePrompt', { name: project.name }) });
    } else {
      out.push({ key: 'what-upload', label: t('nudge.whatUpload'), context: ctx, send: true, prompt: t('nudge.whatUploadPrompt', { name: project.name }) });
    }
    if ((overview?.failedExecutionCount ?? 0) > 0) {
      out.push({ key: 'why-failed', label: t('nudge.whyFailed'), context: ctx, send: true, prompt: t('nudge.whyFailedPrompt', { name: project.name }) });
    }
    if ((overview?.renderCount ?? 0) > 0) {
      out.push({ key: 'next-cut', label: t('nudge.nextCut'), context: ctx, prompt: t('nudge.nextCutPrompt', { name: project.name }) });
    }
    return out.slice(0, 4);
  }, [project, overview, workflowCount, t]);
  return <NudgeList nudges={nudges} />;
}

export function ProjectOverviewTab({
  project, overview, active, attention, outputs, workflows, onOpenTab, clipCount = 0,
}: {
  project: Project;
  overview?: ProjectOverview;
  active: DashboardExecution[];
  attention: DashboardExecution[];
  outputs: OutputVideo[];
  workflows: WorkflowDefinition[];
  onOpenTab: (tab: string) => void;
  /** Uploaded video clips in a project with no code: when above 0, Overview leads to the footage edit. */
  clipCount?: number;
}) {
  const t = useT('projects.sections');
  const tr = useT();
  const fm = useFormat();
  const [idx, setIdx] = useState<number | null>(null);
  // Same dismissal memory as the dashboard panel: closing a failed run there also closes it here
  // (and the reverse), while a failure the viewer has not dismissed still surfaces.
  const { dismissed, dismiss } = useDismissedRunFailures();
  const visibleAttention = attention.filter((r) => !dismissed.has(r.id));
  // Latest take of each video only — earlier takes live on the Renders tab.
  const playable = useMemo(() => groupTakes(outputs).slice(0, 12).map((g) => toPlayable(project.id, project.name, g.latest)), [outputs, project]);
  const videoCount = useMemo(() => groupTakes(outputs).length, [outputs]);
  const o = overview;

  return (
    <Stack gap="md">
      {clipCount > 0 && <FootageReadyCallout projectId={project.id} count={clipCount} />}
      <div className={dash.figures} aria-label={t('summary')}>
        <Figure label={t('uploadedFiles')} value={o?.sourceFileCount ?? 0} hint={o && o.totalBytes > 0 ? formatBytes(o.totalBytes, fm) : undefined} />
        <Figure label={t('workflows')} value={workflows.length} />
        <Figure label={t('videos')} value={videoCount} hint={outputs.length > videoCount ? t('earlierTakes', { count: outputs.length - videoCount }) : undefined} />
        <Figure label={t('lastActivity')} value={timeAgo(o?.lastActivityAt ?? project.updatedAt, undefined, tr, fm.locale)} />
      </div>

      {visibleAttention.length > 0 && (
        <Panel
          tone="attention"
          id="p-attention"
          title={t('lastRunFailed')}
          onDismiss={() => dismiss(visibleAttention.map((r) => r.id))}
          dismissLabel={t('dismissFailed')}
        >
          <Stack gap="md">
          {visibleAttention.slice(0, 2).map((r) => (
            <Group key={r.id} justify="space-between" wrap="wrap" gap="xs">
              <div style={{ minWidth: 0, flex: 1 }}>
                <Text size="sm" fw={600}>{r.workflowName}</Text>
                <Text size="xs" c="dimmed" lineClamp={2}>{r.errorMessage ? runFailureText(r.errorMessage, null, tr).summary : t('noErrorRecorded')}</Text>
              </div>
              <Button component={Link} href={executionHref(r)} variant="default" size="sm" style={{ minHeight: 44 }}>{t('openRun')}</Button>
            </Group>
          ))}
          </Stack>
        </Panel>
      )}

      {active.length > 0 && (
        <Panel title={t('runningNow', { count: active.length })} id="p-live">
          <div className={dash.divided}>
            {active.map((r) => (
              <Link key={r.id} href={executionHref(r)} className={dash.row}>
                <span className={`${dash.statusDot} ${r.status === 'Running' ? dash.live : ''}`} aria-hidden />
                <div className={dash.rowMain}><Text size="sm" fw={600} truncate>{r.workflowName}</Text></div>
                <ExecStatus status={r.status} needsAttention={r.runAttention} />
              </Link>
            ))}
          </div>
        </Panel>
      )}

      <div className={dash.layout}>
        <div className={dash.col}>
          <Panel title={t('latestRenders')} id="p-renders">
            {playable.length === 0 ? (
              <Group justify="space-between" gap="sm">
                <QuietText>{t('noRendersHint')}</QuietText>
                {clipCount === 0 && <Button variant="light" size="sm" onClick={() => onOpenTab('workflows')} style={{ minHeight: 44 }}>{t('goToWorkflows')}</Button>}
              </Group>
            ) : (
              <>
                <OutputRail outputs={playable.slice(0, 6)} onSelect={(p) => setIdx(playable.findIndex((x) => x.stepResultId === p.stepResultId))} />
                {playable.length > 6 && <Button variant="subtle" mt="xs" onClick={() => onOpenTab('renders')}>{t('allRenders')}</Button>}
                <OutputModal outputs={playable} index={idx} onIndexChange={setIdx} onClose={() => setIdx(null)} />
              </>
            )}
          </Panel>

          <Panel title={t('workflows')} id="p-workflows" action={{ label: t('manage'), href: `/projects/${encodeURIComponent(project.id)}?tab=workflows` }}>
            {workflows.length === 0 ? (
              <Group justify="space-between" gap="sm">
                <QuietText>{t('noWorkflowsShort')}</QuietText>
                <Button component={Link} href={`/projects/${encodeURIComponent(project.id)}/workflows/new`} size="sm" leftSection={<IconPlus size={15} />} style={{ minHeight: 44 }}>{t('newWorkflow')}</Button>
              </Group>
            ) : (
              <div className={dash.divided}>
                {workflows.slice(0, 4).map((wf) => (
                  <Link key={wf.id} href={`/projects/${encodeURIComponent(project.id)}/workflows/${encodeURIComponent(wf.id)}`} className={dash.row}>
                    <div className={dash.rowMain}>
                      <Text size="sm" fw={600} truncate>{wf.name}</Text>
                      <Text size="xs" c="dimmed">{t('steps', { count: wf.steps?.length ?? 0 })}</Text>
                    </div>
                    <IconArrowRight size={16} style={{ opacity: 0.45 }} aria-hidden />
                  </Link>
                ))}
              </div>
            )}
          </Panel>
        </div>
        <div className={dash.col}>
          <Panel title={t('askAssistant')} id="p-nudges"><ProjectNudges project={project} overview={overview} workflowCount={workflows.length} /></Panel>
        </div>
      </div>
    </Stack>
  );
}

export function ProjectWorkflowsTab({ projectId, workflows }: { projectId: string; workflows: WorkflowDefinition[] }) {
  const t = useT('projects.sections');
  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Text size="sm" c="dimmed">{t('workflowCount', { count: workflows.length })}</Text>
        <Button component={Link} href={`/projects/${encodeURIComponent(projectId)}/workflows/new`} leftSection={<IconPlus size={16} />} style={{ minHeight: 44 }}>{t('newWorkflow')}</Button>
      </Group>
      {workflows.length === 0 ? (
        <Panel id="wf-empty">
          <Stack align="flex-start" gap="xs">
            <Text fw={650}>{t('noWorkflows')}</Text>
            <QuietText>{t('noWorkflowsBody')}</QuietText>
          </Stack>
        </Panel>
      ) : (
        <div className={dash.panel} style={{ padding: '4px 16px' }}>
          <div className={dash.divided}>
            {workflows.map((wf) => {
              const counts: Partial<Record<StepType, number>> = {};
              for (const s of wf.steps ?? []) counts[(s.stepType || 'Agent') as StepType] = (counts[(s.stepType || 'Agent') as StepType] ?? 0) + 1;
              return (
                <Link key={wf.id} href={`/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(wf.id)}`} className={dash.row} style={{ minHeight: 64 }}>
                  <div className={dash.rowMain}>
                    <Text size="sm" fw={600} truncate>{wf.name}</Text>
                    <Group gap={6} mt={4}>
                      <Text size="xs" c="dimmed">{t('steps', { count: wf.steps?.length ?? 0 })}</Text>
                      {(Object.entries(counts) as [StepType, number][]).map(([st, n]) => (
                        <Group gap={2} key={st}><StepTypeBadge stepType={st} />{n > 1 && <Text size="xs" c="dimmed">x{n}</Text>}</Group>
                      ))}
                    </Group>
                  </div>
                  <IconArrowRight size={16} style={{ opacity: 0.45 }} aria-hidden />
                </Link>
              );
            })}
          </div>
        </div>
      )}
    </Stack>
  );
}

export function ProjectRendersTab({
  project, outputs, onChanged,
}: {
  project: Project;
  outputs: OutputVideo[];
  /** Called after a take is pinned or deleted, to refresh renders (and files). */
  onChanged?: () => void | Promise<unknown>;
}) {
  // Takes (docs/video-editing.md "Takes: the latest render first"): the rail shows each video's
  // LATEST take; earlier takes of the video being played sit under "N earlier takes".
  const t = useT('projects.sections');
  const groups = useMemo(() => groupTakes(outputs), [outputs]);
  const playable = useMemo(() => groups.map((g) => toPlayable(project.id, project.name, g.latest)), [groups, project]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const activeGroup = groups.find((g) => g.latest.stepResultId === activeId || g.earlier.some((o) => o.stepResultId === activeId))
    // Open on the newest FINISHED video, not whichever review-loop draft happened to render last.
    ?? groups.find((g) => g.latest.isFinal) ?? groups[0];
  const activeOutput = activeGroup
    ? [activeGroup.latest, ...activeGroup.earlier].find((o) => o.stepResultId === activeId) ?? activeGroup.latest
    : undefined;
  const active = activeOutput ? toPlayable(project.id, project.name, activeOutput) : undefined;
  const finals = playable.filter((p) => p.isFinal).length;

  if (!active) {
    return (
      <Panel id="renders-empty">
        <Stack align="flex-start" gap="xs">
          <Text fw={650}>{t('noRenders')}</Text>
          <QuietText>{t('noRendersBody')}</QuietText>
          <Badge variant="light" color="gray">{t('nothingRendered')}</Badge>
        </Stack>
      </Panel>
    );
  }
  return (
    <Stack gap="md">
      <div style={{ maxWidth: 960 }}>
        <OutputPlayer output={active} />
        {activeGroup && activeOutput && activeOutput !== activeGroup.latest && (
          <Text size="xs" c="dimmed" mt={4}>{t('watchingEarlier')}</Text>
        )}
      </div>
      {activeGroup && (
        <EarlierTakes
          projectId={project.id}
          group={activeGroup}
          onPlay={(o) => { setActiveId(o.stepResultId); window.scrollTo({ top: 0, behavior: 'smooth' }); }}
          onChanged={async () => { setActiveId(null); await onChanged?.(); }}
        />
      )}
      <div>
        <Group justify="space-between" mb="xs">
          <Text fw={650} size="sm">
            {[
              t('videoCount', { count: playable.length }),
              finals > 0 && finals < playable.length ? t('finalsDrafts', { finals, drafts: playable.length - finals }) : null,
              outputs.length > playable.length ? t('earlierTakes', { count: outputs.length - playable.length }) : null,
            ].filter(Boolean).join(' · ')}
          </Text>
          <Text size="xs" c="dimmed" className={dash.touchOnly}>{t('swipe')}</Text>
        </Group>
        <OutputRail outputs={playable} activeId={activeGroup?.latest.stepResultId} onSelect={(o) => { setActiveId(o.stepResultId); window.scrollTo({ top: 0, behavior: 'smooth' }); }} />
      </div>
    </Stack>
  );
}
