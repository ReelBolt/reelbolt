'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { Anchor, Badge, Button, Group, Progress, Skeleton, Stack, Text, ThemeIcon } from '@mantine/core';
import {
  IconAlertTriangle, IconArrowRight, IconCheck, IconMovie, IconPlayerPlay, IconSettings, IconWand,
} from '@tabler/icons-react';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import { OutputModal } from '@/components/outputs/OutputModal';
import { OutputRail } from '@/components/outputs/OutputRail';
import type { PlayableOutput } from '@/components/outputs/OutputPlayer';
import { VideoThumb } from '@/components/outputs/VideoThumb';
import { getOutputPreviewUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { outputMediaKind } from '@/lib/outputs/media-kind';
import { formatElapsed, timeAgo } from '@/lib/files/model';
import type { LiveExecutionState } from '@/lib/hooks/use-dashboard';
import { useDismissedRunFailures } from '@/lib/hooks/use-dismissed-runs';
import { runStatusDisplay } from '@/lib/utils/run-status';
import { runFailureText } from '@/lib/utils/run-failure';
import type { NeedsAttention as NeedsAttentionInfo } from '@/lib/types/workflow';
import { executionHref, type DashboardExecution, type OverviewOutput, type ProjectOverview } from '@/lib/types/dashboard';
import type { InferenceProviderCapability } from '@/lib/types/inference-provider';
import type { Project } from '@/lib/types/project';
import { Panel, QuietText } from './Panel';
import classes from './dashboard.module.css';
import { useT, msg, type Msg } from '@/i18n';

const STATUS_COLOR: Record<string, string> = {
  Queued: 'gray', Running: 'blue', Passed: 'green', Failed: 'red', Cancelled: 'gray', Stopped: 'gray',
};

export function ExecStatus({ status, needsAttention }: { status: string; needsAttention?: NeedsAttentionInfo | null }) {
  const d = runStatusDisplay({ status, needsAttention });
  const attention = d.label === 'Needs attention';
  // Filled with auto-contrast dark text: the light variant's yellow text is about 2:1 on its pale background.
  return <Badge variant={attention ? 'filled' : 'light'} autoContrast={attention} size="sm" color={attention ? d.color : STATUS_COLOR[status] ?? 'gray'}>{d.label}</Badge>;
}

/** Re-renders once a second so "running for 2m 14s" actually runs. */
function useTick(active: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [active]);
  return now;
}

export function LiveRuns({
  runs, live, loading, firstProjectId,
}: {
  runs: DashboardExecution[];
  live: Record<string, LiveExecutionState>;
  loading: boolean;
  firstProjectId?: string;
}) {
  const t = useT('dashboard.live');
  const now = useTick(runs.length > 0);
  return (
    <Panel title={runs.length > 0 ? t('titleCount', { count: runs.length }) : t('title')} id="live-runs">
      {loading ? (
        <Skeleton height={56} radius="md" />
      ) : runs.length === 0 ? (
        <Group justify="space-between" gap="sm">
          <QuietText>{t('empty')}</QuietText>
          {firstProjectId && (
            <Button component={Link} href={`/projects/${firstProjectId}?tab=workflows`} variant="light" size="sm" leftSection={<IconPlayerPlay size={16} />} style={{ minHeight: 44 }}>
              {t('run')}
            </Button>
          )}
        </Group>
      ) : (
        <div className={classes.divided}>
          {runs.map((r) => {
            const l = live[r.id];
            return (
              <Link key={r.id} href={executionHref(r)} className={classes.row}>
                <span className={`${classes.statusDot} ${r.status === 'Running' ? classes.live : ''}`}
                  style={r.status === 'Queued' ? { background: 'var(--mantine-color-gray-5)' } : undefined} aria-hidden />
                <div className={classes.rowMain}>
                  <Text size="sm" fw={600} truncate>{r.workflowName}</Text>
                  <Text size="xs" c="dimmed" truncate>
                    {t('subline', {
                      project: r.projectName,
                      kind: r.status === 'Running' && r.startedAt ? 'running' : r.status === 'Queued' ? 'queued' : 'none',
                      elapsed: r.status === 'Running' && r.startedAt ? formatElapsed(r.startedAt, now) : '',
                    })}
                  </Text>
                  {l?.stepLabel && <Text size="xs" c="var(--mantine-color-violet-text)" truncate>{l.stepLabel}</Text>}
                  {l?.percent != null && <Progress value={l.percent} size="xs" mt={6} aria-label={t('stepProgress')} />}
                </div>
                <ExecStatus status={r.status} needsAttention={r.runAttention} />
              </Link>
            );
          })}
        </div>
      )}
    </Panel>
  );
}

export function NeedsAttention({ runs }: { runs: DashboardExecution[] }) {
  const t = useT('dashboard.attention');
  const { openWithPrompt } = useAssistantShell();
  // Dismissed failures stay gone across pages and reloads; a NEW failure still surfaces, and the
  // panel lists only what is left, so closing it never hides a run the viewer has not seen yet.
  const { dismissed, dismiss } = useDismissedRunFailures();
  const visible = runs.filter((r) => !dismissed.has(r.id));
  if (visible.length === 0) return null;
  return (
    <Panel
      tone="attention"
      id="needs-attention"
      onDismiss={() => dismiss(visible.map((r) => r.id))}
      dismissLabel={t('dismiss')}
      title={
        <Group gap={8}><IconAlertTriangle size={18} color="var(--mantine-color-red-6)" aria-hidden />
          {visible.length === 1 ? t('one') : t('many', { count: visible.length })}</Group>
      }
    >
      <div className={classes.divided}>
        {visible.map((r) => (
          <div key={r.id} className={classes.row} style={{ alignItems: 'flex-start', flexWrap: 'wrap' }}>
            <div className={classes.rowMain} style={{ minWidth: 200 }}>
              <Text size="sm" fw={600}>{r.workflowName} <Text span size="xs" c="dimmed" fw={400}>{t('inProject', { project: r.projectName, ago: timeAgo(r.completedAt ?? r.startedAt) })}</Text></Text>
              <Text size="xs" c="dimmed" lineClamp={3} title={runFailureText(r.errorMessage).summary} style={{ color: 'light-dark(var(--mantine-color-gray-7), var(--mantine-color-dark-1))' }}>{runFailureText(r.errorMessage).summary}</Text>
            </div>
            <Group gap={8} wrap="nowrap">
              <Button
                size="sm" variant="white" color="violet" leftSection={<IconWand size={16} />} style={{ minHeight: 44 }}
                onClick={() => openWithPrompt(
                  t('prompt', { name: r.workflowName, project: r.projectName, id: r.id }),
                  { projectId: r.projectId, workflowId: r.workflowDefinitionId, executionId: r.id },
                  { send: true },
                )}
              >
                {t('why')}
              </Button>
              <Button component={Link} href={executionHref(r)} size="sm" variant="default" style={{ minHeight: 44 }}>{t('open')}</Button>
            </Group>
          </div>
        ))}
      </div>
    </Panel>
  );
}

function toPlayable(o: OverviewOutput): PlayableOutput {
  return { ...o, runHref: undefined };
}

export function LatestRenders({ outputs, loading }: { outputs: OverviewOutput[]; loading: boolean }) {
  const t = useT('dashboard.renders');
  const [index, setIndex] = useState<number | null>(null);
  const playable = useMemo(() => outputs.map(toPlayable), [outputs]);
  return (
    <Panel title={t('title')} id="latest-renders">
      {loading ? (
        <Skeleton height={140} radius="md" />
      ) : outputs.length === 0 ? (
        <Stack gap={6} align="flex-start">
          <QuietText>{t('empty')}</QuietText>
        </Stack>
      ) : (
        <>
          <OutputRail outputs={playable} showProject onSelect={(o) => setIndex(playable.findIndex((p) => p.stepResultId === o.stepResultId))} />
          <OutputModal outputs={playable} index={index} onIndexChange={setIndex} onClose={() => setIndex(null)} />
        </>
      )}
    </Panel>
  );
}

export function RecentRuns({ runs, loading }: { runs: DashboardExecution[]; loading: boolean }) {
  // The same dismissal memory as the "needs your attention" warning: this history list is fed the
  // server's `recentExecutions` directly, so without this the failed run the viewer just closed
  // would still be listed here (with its red "Failed" badge) — and again after a refresh, which is
  // the "the alert came back" the viewer reported. A failure they have NOT dismissed still shows.
  const t = useT('dashboard.recent');
  const { dismissed } = useDismissedRunFailures();
  const visible = runs.filter((r) => !dismissed.has(r.id));
  // Every recent run dismissed: the panel has nothing left to say. "No finished runs yet." would be
  // a lie — there ARE finished runs, the viewer closed them — so the panel goes away instead, the
  // way the "needs your attention" panel does.
  if (!loading && runs.length > 0 && visible.length === 0) return null;
  return (
    <Panel title={t('title')} id="recent-runs">
      {loading ? (
        <Skeleton height={120} radius="md" />
      ) : runs.length === 0 ? (
        <QuietText>{t('empty')}</QuietText>
      ) : (
        <div className={classes.divided}>
          {visible.slice(0, 6).map((r) => (
            <Link key={r.id} href={executionHref(r)} className={classes.row}>
              <div className={classes.rowMain}>
                <Text size="sm" fw={600} truncate>{r.workflowName}</Text>
                <Text size="xs" c="dimmed" truncate>{r.projectName} - {timeAgo(r.completedAt ?? r.startedAt)}</Text>
              </div>
              {r.outputFileName && <IconMovie size={16} aria-label={t('produced')} style={{ opacity: 0.6 }} />}
              <ExecStatus status={r.status} needsAttention={r.runAttention} />
            </Link>
          ))}
        </div>
      )}
    </Panel>
  );
}

export function ContinueProjects({
  projects, overview, loading,
}: {
  projects: Project[];
  overview: Map<string, ProjectOverview>;
  loading: boolean;
}) {
  const t = useT('dashboard.continue');
  const sorted = useMemo(
    () => [...projects].sort((a, b) =>
      Date.parse(overview.get(b.id)?.lastActivityAt ?? b.updatedAt) - Date.parse(overview.get(a.id)?.lastActivityAt ?? a.updatedAt)),
    [projects, overview],
  );
  return (
    <Panel title={t('title')} action={{ label: t('all'), href: '/projects' }} id="continue-projects">
      {loading ? (
        <Skeleton height={180} radius="md" />
      ) : (
        <div className={classes.divided}>
          {sorted.slice(0, 5).map((p) => {
            const o = overview.get(p.id);
            return (
              <Link key={p.id} href={`/projects/${p.id}`} className={classes.row}>
                <div className={classes.projectThumb}>
                  {o?.latestOutput
                    ? <VideoThumb src={getOutputVideoUrl(p.id, o.latestOutput.stepResultId)} showPlay={false} fileName={o.latestOutput.fileName} imageSrc={outputMediaKind(o.latestOutput.fileName) === 'image' ? getOutputPreviewUrl(p.id, o.latestOutput.stepResultId, 320) : undefined} />
                    : <ThemeIcon variant="light" color="gray" radius="md" w="100%" h={47}><IconMovie size={20} /></ThemeIcon>}
                </div>
                <div className={classes.rowMain}>
                  <Group gap={6} wrap="nowrap">
                    <Text size="sm" fw={600} truncate>{p.name}</Text>
                    {o && o.activeExecutionCount > 0 && <Badge size="xs" variant="light" color="blue">{t('running')}</Badge>}
                  </Group>
                  <Text size="xs" c="dimmed" truncate>
                    {t('line', {
                      summary: o
                        ? t('stats', { files: o.sourceFileCount, workflows: o.workflowCount, renders: o.renderCount })
                        : t('empty'),
                      ago: timeAgo(o?.lastActivityAt ?? p.updatedAt),
                    })}
                  </Text>
                </div>
                <IconArrowRight size={16} style={{ opacity: 0.45 }} aria-hidden />
              </Link>
            );
          })}
        </div>
      )}
    </Panel>
  );
}

const CAPABILITY_ITEMS: { cap: InferenceProviderCapability; label: Msg; hint: Msg; core: boolean }[] = [
  { cap: 'Chat', label: msg('dashboard.caps.chat.label'), hint: msg('dashboard.caps.chat.hint'), core: true },
  { cap: 'Transcription', label: msg('dashboard.caps.transcription.label'), hint: msg('dashboard.caps.transcription.hint'), core: true },
  { cap: 'Vision', label: msg('dashboard.caps.vision.label'), hint: msg('dashboard.caps.vision.hint'), core: true },
  { cap: 'SpeechSynthesis', label: msg('dashboard.caps.speech.label'), hint: msg('dashboard.caps.speech.hint'), core: false },
  { cap: 'VideoGeneration', label: msg('dashboard.caps.video.label'), hint: msg('dashboard.caps.video.hint'), core: false },
  { cap: 'Embedding', label: msg('dashboard.caps.embedding.label'), hint: msg('dashboard.caps.embedding.hint'), core: false },
  { cap: 'ImageGeneration', label: msg('dashboard.caps.image.label'), hint: msg('dashboard.caps.image.hint'), core: false },
  // The six per-tool picture capabilities (erase, fill, expand, background, relight, upscale) are not
  // listed one by one: a tool with no row of its own still runs on this one when its kind can do the
  // job, so "not set up" would be wrong for most of them. The providers page shows each.
  { cap: 'ImageEdit', label: msg('dashboard.caps.imageEdit.label'), hint: msg('dashboard.caps.imageEdit.hint'), core: false },
  { cap: 'Segmentation', label: msg('dashboard.caps.segmentation.label'), hint: msg('dashboard.caps.segmentation.hint'), core: false },
  { cap: 'Depth', label: msg('dashboard.caps.depth.label'), hint: msg('dashboard.caps.depth.hint'), core: false },
];

/**
 * Owner/Admin of the active workspace, or a platform admin. Full card while a core capability has
 * nothing to run on; a single line for the optional ones.
 *
 * It takes the CAPABILITIES the caller can actually run rather than a provider list (A17-UI): the
 * two roles read different endpoints — a platform admin sees the platform-managed rows and asks "is
 * a default set?", while a workspace Owner/Admin sees `selectable` (platform rows plus the
 * workspace's own, and no `isDefault`, deliberately) and asks "can this workspace run this at all?".
 * Passing the wrong list would have made every workspace that relies on ReelBolt's own providers
 * look unconfigured, which is the opposite of what this panel is for.
 */
export function SetupChecklist({ availableCapabilities, providersHref = '/admin/inference-providers', compact = false }: {
  availableCapabilities: InferenceProviderCapability[] | undefined;
  /**
   * Collapse to one optional line, whatever is missing (A09). Used for a workspace Owner/Admin who is
   * not a platform administrator: a hosted user cannot act on "Chat model not configured", so the full
   * checklist is noise for them.
   */
  compact?: boolean;
  /** Where "add a provider" goes: the platform list or the workspace's own. */
  providersHref?: string;
}) {
  const t = useT('dashboard.setup');
  const { openWithPrompt } = useAssistantShell();
  if (!availableCapabilities) return null;
  const has = (cap: InferenceProviderCapability) => availableCapabilities.includes(cap);
  const items = CAPABILITY_ITEMS.map((i) => ({ ...i, done: has(i.cap) }));
  const coreMissing = items.filter((i) => i.core && !i.done);
  const optionalMissing = items.filter((i) => !i.core && !i.done);
  if (coreMissing.length === 0 && optionalMissing.length === 0) return null;

  if (coreMissing.length === 0 || compact) {
    const missing = compact ? [...coreMissing, ...optionalMissing] : optionalMissing;
    return (
      <Panel id="setup">
        <Group gap="sm" wrap="nowrap" align="flex-start">
          <ThemeIcon variant="light" color="gray" radius="md"><IconSettings size={18} /></ThemeIcon>
          <div>
            <Text size="sm" fw={600}>{t('optionalTitle')}</Text>
            <Text size="xs" c="dimmed">{t('optionalList', { list: missing.map((i) => t.msg(i.label).toLowerCase()).join(', ') })}</Text>
            {coreMissing.length === 0 ? (
              <Text size="xs" c="dimmed">{t('optionalBody')}</Text>
            ) : null}
            <Anchor component={Link} href={providersHref} size="sm" style={{ display: 'inline-flex', alignItems: 'center', minHeight: 44 }}>{t('open')}</Anchor>
          </div>
        </Group>
      </Panel>
    );
  }
  const done = items.filter((i) => i.core && i.done).length;
  const coreTotal = items.filter((i) => i.core).length;
  return (
    <Panel title={t('title', { done, total: coreTotal })} action={{ label: t('providers'), href: providersHref }} id="setup">
      <Stack gap={4}>
        {items.map((i) => (
          <Group key={i.cap} gap="sm" wrap="nowrap" mih={44}>
            <ThemeIcon size={22} radius="xl" variant={i.done ? 'filled' : 'light'} color={i.done ? 'green' : i.core ? 'yellow' : 'gray'}>
              {i.done ? <IconCheck size={13} /> : <span aria-hidden style={{ fontSize: 12 }}>{i.core ? '!' : ''}</span>}
            </ThemeIcon>
            <div style={{ minWidth: 0 }}>
              <Text size="sm" fw={500}>{t.msg(i.label)}{!i.core && <Text span size="xs" c="dimmed">{' '}{t('optional')}</Text>}</Text>
              <Text size="xs" c="dimmed" truncate>{i.done ? t('available') : t.msg(i.hint)}</Text>
            </div>
          </Group>
        ))}
      </Stack>
      <Button
        mt="sm" variant="light" size="sm" fullWidth leftSection={<IconWand size={16} />} style={{ minHeight: 44 }}
        onClick={() => openWithPrompt(t('prompt', { missing: coreMissing.map((i) => t.msg(i.label)).join(', ') }))}
      >
        {t('ask')}
      </Button>
    </Panel>
  );
}
