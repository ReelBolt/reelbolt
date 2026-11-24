'use client';

import React, { use, useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Button, Center, Group, Loader, Stack, Text, Title } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { useOutputVideos } from '@/lib/hooks/use-outputs';
import { editorHubHref, openTimelineForRender, timelineEditorHref } from '@/lib/editing/open-timeline';
import { seedRuleSentence } from '@/lib/editing/humanize-error';
import { renderTitle } from '@/lib/utils/run-summary';
import { useT } from '@/i18n';

interface Props {
  params: Promise<{ id: string; stepResultId: string }>;
}

/**
 * `/projects/{projectId}/editor/{stepResultId}`: "Open in editor" on a workflow render.
 *
 * There is one video editor, the editable timeline, so this route only finds the timeline already
 * seeded from the render (the user's own edit) or seeds a new one, then replaces itself with it.
 * A render with no timeline to seed from is refused by the server and said so in words here.
 */
export default function OpenRenderInEditorPage({ params }: Props) {
  const { id: projectId, stepResultId } = use(params);
  const t = useT('projects.openInEditor');
  const router = useRouter();
  const { data: outputs, error: outputsError } = useOutputVideos(projectId);
  const [failure, setFailure] = useState<string | null>(null);
  const started = useRef(false);

  // The render's name becomes the timeline's, so wait until the render list has answered (or
  // failed) before seeding; the call happens once per visit.
  const settled = outputs !== undefined || outputsError !== undefined;
  useEffect(() => {
    if (!settled || started.current) return;
    started.current = true;
    const output = outputs?.find((o) => o.stepResultId === stepResultId);
    openTimelineForRender(projectId, stepResultId, output ? renderTitle(output) : undefined)
      .then((timelineId) => router.replace(timelineEditorHref(projectId, timelineId)))
      .catch((error: unknown) => {
        if (error instanceof ApiError && (error.status === 400 || error.status === 404)) {
          setFailure(t('notEditable'));
        } else if (error instanceof ApiError && error.status === 409 && error.code === 'SEED_WOULD_REVEAL_CENSORED') {
          setFailure(t('censoredSeed'));
        } else if (error instanceof ApiError && error.status === 403) {
          setFailure(t('noAccess'));
        } else {
          // A seed refused by validation names the rule it broke; every other failure stays generic.
          setFailure(seedRuleSentence(error) ?? t('openFailed'));
        }
      });
  }, [settled, outputs, projectId, stepResultId, router, t]);

  return (
    <Center mih="70dvh" px="md">
      {failure ? (
        <Stack align="center" gap="sm" maw={440} ta="center" data-testid="open-render-failed">
          <IconAlertCircle size={28} stroke={1.4} color="var(--mantine-color-dimmed)" />
          <Title order={3}>{t('failedTitle')}</Title>
          <Text size="sm" c="dimmed">
            {failure}
          </Text>
          <Group gap="xs" mt="xs">
            <Button component={Link} href={editorHubHref(projectId)} variant="default">
              {t('allTimelines')}
            </Button>
            <Button component={Link} href={`/projects/${projectId}?tab=workflows`}>
              {t('runWorkflow')}
            </Button>
          </Group>
        </Stack>
      ) : (
        <Stack align="center" gap="sm" data-testid="open-render-pending">
          <Loader size="sm" type="dots" />
          <Text size="sm" c="dimmed">
            {t('opening')}
          </Text>
        </Stack>
      )}
    </Center>
  );
}
