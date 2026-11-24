'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Badge, Button, Card, Group, Loader, Progress, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { describeError } from '@/lib/api/describe-error';
import {
  buildCount, buildStage, buildStates, isBuildRunning, newlyReady, visibleBuilds, type BuildStage,
} from '@/lib/avatars/builds';
import type { AvatarBuildsState } from '@/lib/hooks/use-avatar-builds';
import type { AvatarBuild } from '@/lib/types/avatar-builds';
import { useT } from '@/i18n';
import { useAvatarBuildTray } from './useAvatarBuildTray';

const DISMISSED_KEEP = 50;
const dismissedKey = (projectId: string) => `reelbolt:avatars:builds:dismissed:${projectId}`;

function loadDismissed(projectId: string): string[] {
  try {
    const raw = JSON.parse(window.localStorage.getItem(dismissedKey(projectId)) ?? '[]') as unknown;
    return Array.isArray(raw) ? raw.filter((id): id is string => typeof id === 'string') : [];
  } catch {
    return [];
  }
}

function saveDismissed(projectId: string, ids: readonly string[]): void {
  try {
    window.localStorage.setItem(dismissedKey(projectId), JSON.stringify(ids.slice(-DISMISSED_KEEP)));
  } catch {
    // Storage blocked: the dismissal lasts for this visit only.
  }
}

const stageColor = (stage: BuildStage): string => (stage === 'ready' ? 'green' : stage === 'failed' ? 'red' : 'dimmed');

/**
 * "Being made": the avatars the project is building in the background (docs/design/avatar-objects.md "Avatar
 * variants"): a variant made by the picture model, or an object pulled out of a video. Each row says where the
 * build is in plain words and can be stopped while it runs. When one turns ready the avatars are read again and a
 * toast says so; a failed one stays, with the reason, until it is dismissed. Running builds are also listed in the
 * header tray. A server without builds shows nothing.
 */
export function AvatarBuildsPanel({ projectId, state, onReady }: {
  projectId: string;
  /** The project's builds (`useAvatarBuilds`), owned by the tab so that a started build can refresh them. */
  state: AvatarBuildsState;
  /** A build just turned ready: the avatars (and files) are read again. */
  onReady: () => void | Promise<void>;
}) {
  const t = useT('avatars.builds');
  const tAll = useT();
  const { track, cancel: cancelBuild } = useAvatarBuildTray(projectId);
  const { builds, off, refresh } = state;
  const [seenRunning, setSeenRunning] = useState<ReadonlySet<string>>(() => new Set());
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(() => new Set(loadDismissed(projectId)));
  const [cancelling, setCancelling] = useState<string | null>(null);
  const before = useRef<Map<string, string>>(new Map());

  // Remember which builds this page saw running: only those are shown once they are ready or cancelled.
  const running = builds.filter(isBuildRunning);
  if (running.some((b) => !seenRunning.has(b.jobId))) {
    setSeenRunning(new Set([...seenRunning, ...running.map((b) => b.jobId)]));
  }

  const nameOf = useCallback((build: AvatarBuild) => build.name?.trim() || t('unnamed'), [t]);

  useEffect(() => {
    // A server without builds answers nothing usable: nothing is tracked or announced.
    if (off) return;
    for (const build of builds) if (isBuildRunning(build)) track(build);
    const ready = newlyReady(before.current, builds);
    before.current = buildStates(builds);
    if (ready.length === 0) return;
    for (const build of ready) {
      notifications.show({
        color: 'green',
        message: build.kind === 'extraction' ? t('readyObject', { name: nameOf(build) }) : t('readyVariant', { name: nameOf(build) }),
      });
    }
    void onReady();
  }, [builds, off, track, onReady, t, nameOf]);

  const cancel = async (build: AvatarBuild) => {
    setCancelling(build.jobId);
    try {
      await cancelBuild(build);
    } catch (e) {
      notifications.show({ color: 'red', message: describeError(tAll, e, t('cancelFailed')) });
    } finally {
      setCancelling(null);
      await Promise.resolve(refresh()).catch(() => undefined);
    }
  };

  const dismiss = (build: AvatarBuild) => {
    const next = [...dismissed, build.jobId];
    setDismissed(new Set(next));
    saveDismissed(projectId, next);
  };

  const shown = off ? [] : visibleBuilds(builds, seenRunning, dismissed);
  if (shown.length === 0) return null;

  const stageText = (build: AvatarBuild, stage: BuildStage): string => {
    const count = buildCount(build);
    if (stage === 'pictures' && count) return t('stageCount.pictures', count);
    if (stage === 'videos' && count) return t('stageCount.videos', count);
    if (stage === 'creating' && build.kind === 'extraction') return t('stage.creatingObject');
    return t(`stage.${stage}`);
  };
  const failureText = (build: AvatarBuild): string => {
    const code = build.failureCode?.trim();
    if (code && t.has(`failures.${code}`)) return t(`failures.${code}`);
    return build.message?.trim() || t('failures.other');
  };

  return (
    <Card withBorder radius="md" padding="md" data-testid="avatar-builds">
      <Stack gap="sm">
        <Text fw={650}>{t('title')}</Text>
        {shown.map((build) => {
          const stage = buildStage(build);
          const isRunning = isBuildRunning(build);
          const count = isRunning ? buildCount(build) : null;
          return (
            <Group key={build.jobId} justify="space-between" align="flex-start" wrap="wrap" gap="sm" data-testid={`avatar-build-${build.jobId}`} data-stage={stage}>
              <Stack gap={4} style={{ flex: '1 1 260px', minWidth: 0 }}>
                <Group gap="xs" wrap="wrap">
                  <Text size="sm" fw={600} truncate>{nameOf(build)}</Text>
                  <Badge variant="light" color="gray" fz="xs">{build.kind === 'extraction' ? t('kindExtraction') : t('kindVariant')}</Badge>
                </Group>
                <Group gap="xs" wrap="nowrap" align="center">
                  {isRunning && <Loader size={14} aria-hidden />}
                  <Text size="sm" c={stageColor(stage)} aria-live="polite" data-testid="avatar-build-stage">{stageText(build, stage)}</Text>
                </Group>
                {count && <Progress value={(count.done / count.total) * 100} size="sm" aria-label={stageText(build, stage)} />}
                {stage === 'failed' && <Text size="sm" data-testid="avatar-build-failure">{failureText(build)}</Text>}
              </Stack>
              {isRunning ? (
                <Button
                  variant="default" onClick={() => void cancel(build)} loading={cancelling === build.jobId} style={{ minHeight: 44 }}
                  aria-label={t('cancelFor', { name: nameOf(build) })}
                >
                  {t('cancel')}
                </Button>
              ) : (
                <Button variant="subtle" color="gray" onClick={() => dismiss(build)} style={{ minHeight: 44 }} aria-label={t('dismissFor', { name: nameOf(build) })}>
                  {t('dismiss')}
                </Button>
              )}
            </Group>
          );
        })}
      </Stack>
    </Card>
  );
}
