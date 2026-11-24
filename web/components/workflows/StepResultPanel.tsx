'use client';

import { useMemo } from 'react';
import Link from 'next/link';
import { useT, useFormat } from '@/i18n';
import { Alert, Badge, Button, Card, Group, Paper, ScrollArea, SimpleGrid, Stack, Tabs, Text, Tooltip } from '@mantine/core';
import { IconAlertCircle, IconDownload, IconExternalLink, IconPencil, IconTimeline } from '@tabler/icons-react';
import type { StepType, WorkflowStepResult } from '@/lib/types/workflow';
import { formatDate, formatDurationLong } from '@/lib/utils/format';
import { readWarnings } from '@/lib/utils/run-summary';
import { stepChargeLabel } from '@/lib/utils/billing';
import { RunWarningsAlert } from '@/components/outputs/RunWarningsAlert';
import { getOutputDownloadUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { JsonViewer } from '@/components/workflows/JsonViewer';
import { ExecutionEventCard, isTechnicalEvent } from '@/components/workflows/ExecutionEventCard';
import { EditRoomTranscriptPanel } from '@/components/workflows/EditRoomTranscriptPanel';
import { isRoomStepType } from '@/components/workflows/RoomStepConfigEditor';
import { VIDEO_EDITOR_COMING_SOON_LABEL, useVideoEditorEnabled } from '@/components/shell/VideoEditorFlag';
import { stepComputeBadge } from '@/lib/utils/compute-target';
import type { ExecutionStreamEvent } from '@/lib/hooks/use-execution-stream';
import type { StepChangeStepCapability } from '@/lib/types/change-requests';
import { CR_COPY } from '@/components/change-requests/change-request-copy';
import { CheckedOutputMedia } from '@/components/outputs/CheckedVideo';
import { useDownloadGuard } from '@/lib/outputs/output-check';
import { fidelityBadge, readSubjectFidelity } from '@/lib/avatars/facts';

interface StepResultLiveTokenMetrics {
  inputTokens: number;
  outputTokens: number;
}

export interface StepResultPanelProps {
  projectId: string;
  stepResult: WorkflowStepResult;
  /** The owning workflow step's type — gates the room-only "Discussion" tab below. */
  stepType?: StepType;
  stepEvents: ExecutionStreamEvent[];
  liveTokenMetrics: StepResultLiveTokenMetrics;
  artifactJson: string | null;
  artifactError: string | null;
  artifactLoading: boolean;
  /**
   * The execution page's "Details" disclosure: token counts, the raw Data tab and engine-level
   * event detail are for people debugging a workflow, not for someone waiting on their video.
   */
  showTechnical?: boolean;
  /**
   * Device id -> machine name for the workspace's paired machines (E8-UI), resolved by the
   * CALLER from `GET /api/v1/runners` — the same split the runners page uses, so this panel
   * stays presentational and never fetches. Optional: without it, a step that ran on a runner is
   * still labelled, just without the machine's name.
   */
  runnerNames?: Record<string, string>;
  /** What this step result accepts as a change request (from `GET change-capabilities`); absent while loading. */
  changeCapability?: StepChangeStepCapability;
  /** Called when "Request changes" is pressed on a supported step (the caller owns the modal / the film panel). */
  onRequestChanges?: (capability: StepChangeStepCapability) => void;
}

const DEFAULT_STATUS = 'Pending';

function statusColor(status: WorkflowStepResult['status']): string {
  if (status === 'Completed') return 'green';
  if (status === 'Running') return 'blue';
  if (status === 'Failed') return 'red';
  return 'gray';
}

export function StepResultPanel({
  projectId,
  stepResult,
  stepType,
  stepEvents,
  liveTokenMetrics,
  artifactJson,
  artifactError,
  artifactLoading,
  showTechnical = false,
  runnerNames,
  changeCapability,
  onRequestChanges,
}: StepResultPanelProps) {
  // The video editor's flag, from the shell. This is a client component, so it cannot read the
  // environment; with the editor off, the "Open in Editor" affordance below must not be offered —
  // a button that promises an editor and lands on a coming-soon page is the same defect one click
  // later (see components/shell/VideoEditorFlag.tsx).
  const t = useT('stepResults');
  const guardDownload = useDownloadGuard();
  const f = useFormat();
  const videoEditorOn = useVideoEditorEnabled();
  const fidelity = useMemo(() => fidelityBadge(readSubjectFidelity(stepResult.outputJson)), [stepResult.outputJson]);
  const warnings = useMemo(() => readWarnings(stepResult.outputJson), [stepResult.outputJson]);
  // Where this step actually ran, from the persisted compute target (E8-UI). Null — and so no
  // badge at all — when the run's data does not say, which is the honest answer for a row written
  // before D12 and for a target this build does not know.
  const computeBadge = useMemo(
    () => stepComputeBadge(stepResult, runnerNames),
    [stepResult, runnerNames],
  );
  const hasError = !!stepResult.errorDetails;
  const hasIterationOrCompletion =
    (stepResult.iterationNumber !== null && stepResult.iterationNumber !== undefined) || !!stepResult.completedAt;
  // All three room step types (EditRoom/GraphicsRoom/ColorGradeRoom) run the SAME shared
  // group-chat infrastructure and persist the same `chatTranscriptJson`/transcript artifact, so
  // the discussion tab is gated on "is a room", never on EditRoom alone.
  const isRoom = stepType !== undefined && isRoomStepType(stepType);

  const hasTimeline = useMemo(() => {
    if (stepResult.artifactStorageKey?.endsWith('-timeline.json')) return true;
    if (stepResult.outputJson) {
      try {
        const parsed = JSON.parse(stepResult.outputJson);
        if (parsed.timelineStorageKey || parsed.outputTimeline || parsed.timeline) return true;
      } catch {
        if (stepResult.outputJson.includes('timelineStorageKey')) return true;
      }
    }
    if (stepResult.outputStorageKey && stepResult.status === 'Completed') return true;
    return false;
  }, [stepResult]);

  const statusKey = stepResult.status ?? DEFAULT_STATUS;
  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Group gap="xs" wrap="wrap">
          <Badge size="lg" variant="light" color={statusColor(stepResult.status)}>
            {t.has(`status.${statusKey}`) ? t(`status.${statusKey}`) : stepResult.status}
          </Badge>
          {computeBadge && (
            <Tooltip label={computeBadge.tooltip} multiline maw={320} openDelay={200}>
              <Badge size="lg" variant="light" color={computeBadge.color} style={{ cursor: 'help' }}>
                {computeBadge.label}
              </Badge>
            </Tooltip>
          )}
        </Group>
        <Text size="sm" c="dimmed">{formatDate(stepResult.executedAt)}</Text>
      </Group>

      {warnings.length > 0 && <RunWarningsAlert warnings={warnings} compact />}
      {fidelity && (
        <Tooltip label={t(`fidelity.${fidelity}Hint`)} multiline w={260}>
          <Badge color={fidelity === 'mismatch' ? 'orange' : 'gray'} variant="light" data-testid="subject-fidelity-badge" data-status={fidelity}>
            {t(`fidelity.${fidelity}`)}
          </Badge>
        </Tooltip>
      )}

      {changeCapability?.supported && onRequestChanges && (
        <Group>
          <Button
            variant="light" size="compact-md" leftSection={<IconPencil size={16} />}
            onClick={() => onRequestChanges(changeCapability)} data-testid="step-request-changes"
          >
            {changeCapability.route === 'composition-revision' ? CR_COPY.filmButton : CR_COPY.button}
          </Button>
        </Group>
      )}

      {stepResult.failureCode && (
        <Text size="xs" c="red" data-testid="step-failure-code">
          {t.rich('failureCode', { code: stepResult.failureCode, b: (chunks) => <b>{chunks}</b> })}
          {stepResult.failureClass ? ` (${stepResult.failureClass})` : ''}
        </Text>
      )}

      {stepResult.fromCache && !showTechnical && (
        <Text size="xs" c="teal">{t('reusedFromRun')}</Text>
      )}
      {stepResult.reusedFromStepResultId && (
        <Text size="xs" c="teal" data-testid="step-kept-from-earlier-run">
          {t('keptFromEarlier')}
        </Text>
      )}

      <SimpleGrid cols={{ base: 1, xs: 2 }}>
        <Paper withBorder p="sm" radius="md">
          <Text size="xs" c="dimmed">{t('took')}</Text>
          <Text fw={700}>{formatDurationLong(Math.round(stepResult.durationMs))}</Text>
        </Paper>
        {stepChargeLabel(stepResult.creditsCharged) && (
        <Paper withBorder p="sm" radius="md">
          <Text size="xs" c="dimmed">{t('creditsCharged')}</Text>
          <Text fw={700} data-testid="step-credits-charged">{stepChargeLabel(stepResult.creditsCharged)}</Text>
        </Paper>
        )}
        {showTechnical && (
        <Paper withBorder p="sm" radius="md">
          <Text size="xs" c="dimmed">{t('tokensUsed')}</Text>
          <Text fw={700}>{f.number(stepResult.tokensUsed)}</Text>
          {/* A cache-served step spent nothing this run, so the figure above is 0 and would
              otherwise read as "this step was free" rather than "this step was reused". */}
          {stepResult.fromCache && (
            <Text size="xs" c="teal">
              {t('reusedFromCache', { saved: stepResult.cachedTokensSaved ? f.number(stepResult.cachedTokensSaved) : 'none' })}
            </Text>
          )}
        </Paper>
        )}
      </SimpleGrid>
      {showTechnical && (
      <SimpleGrid cols={{ base: 1, xs: 2 }}>
        <Paper withBorder p="sm" radius="md">
          <Text size="xs" c="dimmed">{t('inputTokensLive')}</Text>
          <Text fw={700}>{f.number(liveTokenMetrics.inputTokens)}</Text>
        </Paper>
        <Paper withBorder p="sm" radius="md">
          <Text size="xs" c="dimmed">{t('outputTokensLive')}</Text>
          <Text fw={700}>{f.number(liveTokenMetrics.outputTokens)}</Text>
        </Paper>
      </SimpleGrid>
      )}

      <Tabs defaultValue="overview" keepMounted={false}>
        <Tabs.List>
          <Tabs.Tab value="overview">
            <Group gap={6} wrap="nowrap">
              <Text size="sm">{t('tabs.overview')}</Text>
              {hasError && (
                <span
                  aria-label={t('errorDetailsAria')}
                  style={{
                    width: 6,
                    height: 6,
                    borderRadius: '50%',
                    background: 'var(--mantine-color-red-6)',
                    display: 'inline-block',
                  }}
                />
              )}
            </Group>
          </Tabs.Tab>
          {showTechnical && <Tabs.Tab value="data">{t('tabs.data')}</Tabs.Tab>}
          {isRoom && <Tabs.Tab value="discussion">{t('tabs.discussion')}</Tabs.Tab>}
          <Tabs.Tab value="events">{t('tabs.activity')}</Tabs.Tab>
        </Tabs.List>

        <ScrollArea.Autosize mah={520} mt="sm">
          <Tabs.Panel value="overview">
            <Stack gap="md">
              {hasError && (
                <Alert icon={<IconAlertCircle size={16} />} title={t('whatWentWrong')} color="red" variant="light">
                  <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{stepResult.errorDetails}</Text>
                </Alert>
              )}

              {hasIterationOrCompletion && (
                <Card withBorder padding="sm" radius="md">
                  <Stack gap="xs">
                    {stepResult.iterationNumber !== null && stepResult.iterationNumber !== undefined && (
                      <Group justify="space-between">
                        <Text size="sm" c="dimmed">{t('attempt')}</Text>
                        <Text size="sm" fw={500}>#{stepResult.iterationNumber}</Text>
                      </Group>
                    )}
                    {stepResult.completedAt && (
                      <Group justify="space-between">
                        <Text size="sm" c="dimmed">{t('finished')}</Text>
                        <Text size="sm" fw={500}>{formatDate(stepResult.completedAt)}</Text>
                      </Group>
                    )}
                  </Stack>
                </Card>
              )}

              {hasTimeline && (
                <Card withBorder padding="xs" radius="md">
                  <Group justify="space-between" align="center" wrap="nowrap">
                    <Group gap="xs" wrap="nowrap">
                      <IconTimeline size={18} style={{ color: 'var(--mantine-color-blue-4)', flexShrink: 0 }} />
                      <div>
                        <Text size="sm" fw={500}>
                          {videoEditorOn ? t('openCutInEditor') : VIDEO_EDITOR_COMING_SOON_LABEL}
                        </Text>
                        <Text size="xs" c="dimmed">{t('seeEveryClip')}</Text>
                      </div>
                    </Group>
                    {videoEditorOn && (
                      <Button
                        component={Link}
                        href={`/projects/${projectId}/editor/${stepResult.id}`}
                        variant="subtle"
                        size="xs"
                        rightSection={<IconExternalLink size={14} />}
                      >
                        {t('openInEditor')}
                      </Button>
                    )}
                  </Group>
                </Card>
              )}

              {stepResult.outputStorageKey && (
                <Card withBorder padding="sm" radius="md">
                  <Text size="sm" fw={600} mb="xs">{t('videoFromStep')}</Text>
                  <CheckedOutputMedia
                    controls
                    style={{ width: '100%', borderRadius: "var(--mantine-radius-xs)" }}
                    src={getOutputVideoUrl(projectId, stepResult.id)}
                    fileName={stepResult.outputStorageKey}
                    alt={stepResult.outputStorageKey.split('/').pop() ?? ''}
                    data-testid="step-output-media"
                  />
                  <Button
                    component="a"
                    href={getOutputDownloadUrl(projectId, stepResult.id, stepResult.outputStorageKey.split('/').pop() ?? 'video.mp4')}
                    onClick={guardDownload(getOutputDownloadUrl(projectId, stepResult.id, stepResult.outputStorageKey.split('/').pop() ?? 'video.mp4'), stepResult.outputStorageKey.split('/').pop() ?? 'video.mp4')}
                    download
                    variant="light"
                    mt="xs"
                    style={{ minHeight: 44 }}
                    leftSection={<IconDownload size={16} />}
                    data-testid="step-video-download"
                  >
                    {t('downloadVideo')}
                  </Button>
                </Card>
              )}

              <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                {stepResult.output || t('noOutput')}
              </Text>
            </Stack>
          </Tabs.Panel>

          {showTechnical && (
          <Tabs.Panel value="data">
            <Stack gap="md">
              {hasTimeline && (
                <Card withBorder padding="xs" radius="md">
                  <Group justify="space-between" align="center" wrap="nowrap">
                    <Group gap="xs" wrap="nowrap">
                      <IconTimeline size={18} style={{ color: 'var(--mantine-color-blue-4)', flexShrink: 0 }} />
                      <Text size="sm">{t('timelineManifest')}</Text>
                    </Group>
                    {videoEditorOn ? (
                      <Button
                        component={Link}
                        href={`/projects/${projectId}/editor/${stepResult.id}`}
                        variant="subtle"
                        size="xs"
                        rightSection={<IconExternalLink size={14} />}
                      >
                        {t('openInEditor')}
                      </Button>
                    ) : (
                      <Text size="xs" c="dimmed" data-testid="video-editor-coming-soon">
                        {VIDEO_EDITOR_COMING_SOON_LABEL}
                      </Text>
                    )}
                  </Group>
                </Card>
              )}
              {stepResult.artifactStorageKey && (
                <Card withBorder padding="sm" radius="md">
                  <Text size="sm" fw={600} mb="xs">
                    {isRoom ? t('roomTranscriptArtifact') : t('stepArtifact')}
                  </Text>
                  {artifactLoading && <Text size="sm" c="dimmed">{t('loadingArtifact')}</Text>}
                  {artifactError && (
                    <Alert icon={<IconAlertCircle size={14} />} color="yellow" variant="light">
                      {t('artifactError', { error: artifactError })}
                    </Alert>
                  )}
                  {artifactJson && <JsonViewer label={t('artifactJson')} value={artifactJson} />}
                </Card>
              )}

              {stepResult.inputJson && (
                <Card withBorder padding="sm" radius="md">
                  <JsonViewer label={t('inputJson')} value={stepResult.inputJson} />
                </Card>
              )}

              {stepResult.outputJson && (
                <Card withBorder padding="sm" radius="md">
                  <JsonViewer label={t('outputJson')} value={stepResult.outputJson} />
                </Card>
              )}

              {!stepResult.artifactStorageKey && !stepResult.inputJson && !stepResult.outputJson && (
                <Text size="sm" c="dimmed">{t('noStructuredData')}</Text>
              )}
            </Stack>
          </Tabs.Panel>
          )}

          {isRoom && (
            <Tabs.Panel value="discussion">
              <EditRoomTranscriptPanel
                stepResult={stepResult}
                stepEvents={stepEvents}
                artifactJson={artifactJson}
                artifactError={artifactError}
                artifactLoading={artifactLoading}
              />
            </Tabs.Panel>
          )}

          <Tabs.Panel value="events">
            <Stack gap="xs">
              {stepEvents.length === 0 ? (
                <Text size="sm" c="dimmed">{t('noUpdates')}</Text>
              ) : (
                stepEvents
                  .filter((event) => showTechnical || !isTechnicalEvent(event))
                  .map((event, index) => (
                    <ExecutionEventCard key={`${event.id}-${index}`} event={event} technical={showTechnical} />
                  ))
              )}
            </Stack>
          </Tabs.Panel>
        </ScrollArea.Autosize>
      </Tabs>
    </Stack>
  );
}
