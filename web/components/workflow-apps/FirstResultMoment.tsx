'use client';

import { useId, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import useSWR from 'swr';
import { Button, CloseButton, Menu, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconChevronDown, IconDownload } from '@tabler/icons-react';
import { CheckedVideo } from '@/components/outputs/CheckedVideo';
import { fetcher } from '@/lib/api/client';
import { getOutputDownloadUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { applyWorkflowTemplate } from '@/lib/api/workflows';
import { useAuth } from '@/lib/hooks/use-auth';
import { useDownloadGuard } from '@/lib/outputs/output-check';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import type { WorkflowExecution } from '@/lib/types/workflow';
import { formatVideoLength } from '@/lib/utils/run-summary';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { SUBTITLES_RECIPE_KEY, runFacts, type ThirdStep } from './first-result';
import styles from './AppRunPage.module.css';

export interface FirstResultOutput {
  stepResultId: string;
  fileName: string;
  /** The finished file's own probed length, when the outputs list carries it. */
  durationSec?: number | null;
}

interface FirstResultMomentProps {
  projectId: string;
  executionId: string;
  output: FirstResultOutput;
  /** False where the page already shows the player right next to the moment (the run's own page). */
  showVideo?: boolean;
  /** False where the page announces the heading itself (the result pane's status line). */
  showTitle?: boolean;
  /** The existing way to change this result (the page's own change entry point). */
  onChange: () => void;
  third: ThirdStep;
  /** Brings this run's choices back with the Shape choice in focus; needed for the `shape` steps. */
  onTryShape?: () => void;
  onDismiss: () => void;
}

/**
 * "Your first video is ready": the one time a person opens their first finished video, the result
 * is framed as theirs, with what this run really did to their footage and three ways on. It sits in
 * the page (never over it) and closes for good.
 *
 * The facts are read from the run's own step outputs (see `runFacts`): a figure the run did not
 * report is simply absent. Nothing here changes how the result is downloaded, changed or re-run:
 * every button is an entry point that already exists.
 */
export function FirstResultMoment({
  projectId, executionId, output, showVideo = true, showTitle = true, onChange, third, onTryShape, onDismiss,
}: FirstResultMomentProps) {
  const t = useT('apps.run.first');
  const tAll = useT();
  const router = useRouter();
  const { user } = useAuth();
  const guardDownload = useDownloadGuard();
  const titleId = useId();
  const [settingUp, setSettingUp] = useState(false);
  // The step outputs are not part of the run list: read once, only while the moment is on screen.
  const { data: detail } = useSWR<WorkflowExecution>(`/api/v1/projects/${projectId}/executions/${executionId}`, fetcher, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
  const facts = runFacts(detail?.id === executionId ? detail.stepResults : null, output.durationSec);
  const before = facts.sourceSec !== null ? formatVideoLength(facts.sourceSec) : null;
  const after = facts.outputSec !== null ? formatVideoLength(facts.outputSec) : null;

  const downloadUrl = getOutputDownloadUrl(projectId, output.stepResultId, output.fileName);
  const withSubtitles = third === 'subtitlesOrShape' || third === 'subtitles';
  const withShape = third === 'subtitlesOrShape' || third === 'shape';

  const addSubtitles = async () => {
    if (settingUp) return;
    setSettingUp(true);
    try {
      const workflow = await applyWorkflowTemplate(projectId, SUBTITLES_RECIPE_KEY, true);
      router.push(`/projects/${projectId}/workflows/${workflow.id}`);
    } catch (err: unknown) {
      notifications.show({
        title: t('subtitlesFailedTitle'),
        message: describeError(tAll, err, t('tryAgainLater')),
        color: 'red',
      });
      setSettingUp(false);
    }
  };

  return (
    <section
      className={styles.firstResult}
      data-testid="first-result"
      {...(showTitle ? { 'aria-labelledby': titleId } : { 'aria-label': t('title') })}
    >
      <div className={styles.firstResultHead}>
        <div>
          {showTitle && <Title order={3} size="h4" id={titleId}>{t('title')}</Title>}
          <Text size="sm">{t('body')}</Text>
        </div>
        <CloseButton aria-label={t('dismiss')} onClick={onDismiss} size={44} />
      </div>

      {showVideo && (
        <CheckedVideo controls preload="metadata" playsInline src={getOutputVideoUrl(projectId, output.stepResultId)} aria-label={t('video')} />
      )}

      {before && after ? (
        <dl className={styles.facts} aria-label={t('factsLabel')} data-testid="first-result-facts">
          <dt>{t('beforeLabel')}</dt>
          <dd>{t('before', { count: facts.sourceClips ?? 1, length: before })}</dd>
          <dt>{t('afterLabel')}</dt>
          <dd>{facts.parts !== null ? t('afterParts', { length: after, count: facts.parts }) : t('after', { length: after })}</dd>
        </dl>
      ) : after ? (
        <Text size="sm" data-testid="first-result-facts">{t('lengthOnly', { length: after })}</Text>
      ) : null}

      <div className={styles.buttonRow}>
        <Button
          component="a"
          href={downloadUrl}
          download
          leftSection={<IconDownload size={16} />}
          style={{ minHeight: 44 }}
          // The same export transition the outputs player reports: once per person, with their consent.
          onClick={(e) => {
            emitFunnelEvent('alpha.first_export_initiated', userKeyFromEmail(user?.email));
            void guardDownload(downloadUrl, output.fileName)(e);
          }}
        >
          {t('download')}
        </Button>
        <Button variant="default" onClick={onChange} style={{ minHeight: 44 }}>
          {t('change')}
        </Button>
        {third === 'subtitlesOrShape' && (
          <Menu position="bottom-start" withinPortal>
            <Menu.Target>
              <Button variant="default" loading={settingUp} rightSection={<IconChevronDown size={16} aria-hidden="true" />} style={{ minHeight: 44 }}>
                {t('subtitlesOrShape')}
              </Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item onClick={() => void addSubtitles()}>{t('subtitles')}</Menu.Item>
              <Menu.Item onClick={onTryShape}>{t('shape')}</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        )}
        {third === 'subtitles' && (
          <Button variant="default" loading={settingUp} onClick={() => void addSubtitles()} style={{ minHeight: 44 }}>
            {t('subtitles')}
          </Button>
        )}
        {third === 'shape' && (
          <Button variant="default" onClick={onTryShape} style={{ minHeight: 44 }}>
            {t('shape')}
          </Button>
        )}
        {third === 'recipes' && (
          <Button component={Link} href={`/projects/${projectId}?tab=workflows`} variant="default" style={{ minHeight: 44 }}>
            {t('recipes')}
          </Button>
        )}
      </div>
      {withSubtitles && <Text size="xs">{t('subtitlesHint')}</Text>}
      {withShape && <Text size="xs">{t('shapeHint')}</Text>}
    </section>
  );
}
