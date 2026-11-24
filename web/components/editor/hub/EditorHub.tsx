'use client';

import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import React, { useEffect, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { Alert, Button, Container, Group, Select, Skeleton, Stack, Tabs, Text, Title } from '@mantine/core';
import { IconAlertCircle, IconFolder, IconMovie, IconPhotoEdit } from '@tabler/icons-react';
import { useProjects } from '@/lib/hooks/use-projects';
import { editorHubHref, isEditorHubView, isLegacyEditorHubView, type EditorHubView } from '@/lib/editing/open-timeline';
import { PicturesPanel } from '../PicturesPanel';
import { pickDefaultProject } from './default-project';
import { TimelinesView } from './TimelinesView';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import classes from './hub.module.css';
import { EditorEmpty } from '../ui';
import { useT } from '@/i18n';

const LAST_PROJECT_KEY = 'reelbolt:editor:project';

function readLastProject(): string | null {
  try {
    return window.localStorage.getItem(LAST_PROJECT_KEY);
  } catch {
    return null;
  }
}

function writeLastProject(projectId: string) {
  try {
    window.localStorage.setItem(LAST_PROJECT_KEY, projectId);
  } catch {
    /* storage unavailable: the URL still carries the project */
  }
}

/**
 * `/editor`: the one place every editor is opened from, scoped to one project.
 *
 * Two views share one header and one project switcher: **Videos** (the editable timelines and the
 * workflow renders that can become one) and **Pictures** (layered thumbnails). The project and the
 * view ride in the URL (`?project=…&view=pictures`), so every view can be linked to and the back
 * button behaves; the old `view=timelines` and `view=media` links are rewritten to `view=videos`.
 * The last project is remembered per browser as a convenience only.
 */
export function EditorHub() {
  const t = useT('editor.hub.editorHub');
  const router = useRouter();
  const searchParams = useSearchParams();
  const { data: projects, isLoading, error } = useProjects();
  const viewParam = searchParams?.get('view');
  const view: EditorHubView = isEditorHubView(viewParam) ? viewParam : 'videos';
  const projectParam = searchParams?.get('project') ?? null;

  // Links from before the hub had two tabs (`view=timelines`, `view=media`) land on Videos.
  const legacyView = isLegacyEditorHubView(viewParam);
  useEffect(() => {
    if (!legacyView) return;
    const params = new URLSearchParams(searchParams?.toString() ?? '');
    params.set('view', 'videos');
    router.replace(`/editor?${params.toString()}`, { scroll: false });
  }, [legacyView, router, searchParams]);

  // The remembered project is read after hydration only, so server and client markup agree.
  const [remembered, setRemembered] = useState<string | null>(null);
  useEffect(() => {
    const handle = window.setTimeout(() => setRemembered(readLastProject()), 0);
    return () => window.clearTimeout(handle);
  }, []);

  // A project named in the URL is trusted as given (a link is a statement of intent, and every tab
  // answers a foreign or deleted project with its own not-found state); the remembered one only if
  // it is still in the list; otherwise the first project.
  const projectId = projectParam ?? pickDefaultProject(projects, remembered);

  useEffect(() => {
    if (projectId) writeLastProject(projectId);
  }, [projectId]);

  const go = (nextProject: string | null, nextView: EditorHubView) =>
    router.replace(editorHubHref(nextProject, nextView), { scroll: false });

  return (
    <Container size="xl" px={{ base: 'xs', sm: 'md' }} py={{ base: 'xs', sm: 'lg' }}>
      <Stack gap="md">
        <Group justify="space-between" align="flex-end" wrap="wrap" gap="md">
          <Stack gap={4} className={classes.headerTitle}>
            <Group gap="xs" wrap="nowrap">
              <Title order={2}>{t('editor')}</Title>
              <EarlyPreviewLabel />
            </Group>
            <Text size="sm" c="dimmed" maw={520} visibleFrom="sm">
              {t('refineYourVideosOn')}
            </Text>
          </Stack>
          <Select
            aria-label={t('project')}
            className={classes.picker}
            classNames={{ input: classes.pickerInput }}
            placeholder={isLoading ? t('loadingProjects') : t('chooseAProject')}
            data={(projects ?? []).map((p) => ({ value: p.id, label: p.name }))}
            value={projectId}
            onChange={(id) => id && go(id, view)}
            allowDeselect={false}
            searchable={(projects?.length ?? 0) > 6}
            leftSection={<IconFolder size={16} stroke={1.6} />}
            disabled={isLoading || !projects?.length}
            data-testid="editor-project"
          />
        </Group>

        {error && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
            {t('yourProjectsCouldNot')} {humanizeErrorT(t, error, t('tryAgainInA'))}
          </Alert>
        )}

        <Tabs value={view} onChange={(v) => isEditorHubView(v) && go(projectId, v)} keepMounted={false}>
          <Tabs.List className={classes.tabs} mb={{ base: 'xs', sm: 'md' }}>
            <Tabs.Tab className={classes.touchTarget} value="videos" leftSection={<IconMovie size={16} stroke={1.6} />}>
              {t('videos')}
            </Tabs.Tab>
            <Tabs.Tab className={classes.touchTarget} value="pictures" leftSection={<IconPhotoEdit size={16} stroke={1.6} />}>
              {t('pictures')}
            </Tabs.Tab>
          </Tabs.List>

          {isLoading && !projectParam ? (
            <Stack gap="sm">
              <Skeleton height={14} width={120} radius="sm" />
              <Skeleton height={190} radius={12} />
            </Stack>
          ) : !projectId ? (
            <div className={classes.empty}>
              <Stack align="center" gap="sm">
                <EditorEmpty icon={IconFolder} title={t('createAProjectFirst')} hint={t('projectsHoldYourFootage')} />
                <Button component={Link} href="/projects" mt="xs" className={classes.touchTarget}>
                  {t('goToProjects')}
                </Button>
              </Stack>
            </div>
          ) : (
            <>
              <Tabs.Panel value="videos">
                <TimelinesView projectId={projectId} />
              </Tabs.Panel>
              <Tabs.Panel value="pictures">
                <PicturesPanel projectId={projectId} />
              </Tabs.Panel>
            </>
          )}
        </Tabs>
      </Stack>
    </Container>
  );
}
