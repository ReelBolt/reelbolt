'use client';
/* eslint-disable i18next/no-literal-string -- the remaining literals are ids, enum keys and t.rich tag handlers, not user-visible text */

import { Suspense, use, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { ActionIcon, Box, Button, Center, Loader, Menu, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconBrandGithub, IconCloud, IconDeviceDesktop, IconDotsVertical, IconEdit, IconFiles, IconHistory, IconInfoCircle, IconPalette, IconTopologyRing, IconTrash, IconUpload, IconVideo,
} from '@tabler/icons-react';
import { AssistantPageContext } from '@/components/assistant/AssistantPageContext';
import { useCloudProviders } from '@/components/cloud-storage/hooks';
import { ProjectCloudStorageTab } from '@/components/cloud-storage/ProjectCloudStorageTab';
import { ProjectHistoryTab } from '@/components/projects/history/ProjectHistoryTab';
import { TabErrorBoundary } from '@/components/shared/TabErrorBoundary';
import { ProjectProfileTab } from '@/components/design-system/ProjectProfileTab';
import { ProjectFolderLinksTab } from '@/components/folder-sync/ProjectFolderLinksTab';
import { StorageQuotaBanner } from '@/components/storage/StorageQuotaNotice';
import { FileManager } from '@/components/files/FileManager';
import { ProjectForm } from '@/components/projects/ProjectForm';
import { ProjectOverviewTab, ProjectRendersTab, ProjectWorkflowsTab } from '@/components/projects/ProjectSections';
import { AppsGallery } from '@/components/workflow-apps/AppsGallery';
import { RunNudge } from '@/components/workflow-apps/RunNudge';
import { hasFilesAndNoRun } from '@/components/workflow-apps/run-nudge';
import { OneClickRun } from '@/components/workflow-apps/OneClickRun';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import { FootageReadyCallout, footageClipCount } from '@/components/workflow-apps/FootageReadyCallout';
import { CATEGORY_FILTERS, type CategoryFilter } from '@/components/workflow-apps/gallery-model';
import { useWorkflowAppsEnabled } from '@/components/shell/WorkflowAppsFlag';
import { ProjectGitHubTab } from '@/components/projects/ProjectGitHubTab';
import { ProjectRepositoryIndicator } from '@/components/projects/ProjectRepositoryIndicator';
import { ProjectVoicesTab } from '@/components/projects/ProjectVoicesTab';
import { ProjectAvatarsTab, useAvatarsAvailable } from '@/components/avatars/ProjectAvatarsTab';
import { avatarUsesByFile } from '@/lib/avatars/model';
import { useProjectAvatars } from '@/lib/hooks/use-avatars';
import { ProjectPronunciationTab } from '@/components/projects/ProjectPronunciationTab';
import { IconLanguage, IconMicrophone, IconUserCircle } from '@tabler/icons-react';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { PageHeader } from '@/components/shared/PageHeader';
import { LoadState } from '@/components/shared/LoadState';
import { deleteProject } from '@/lib/api/projects';
import { documentForImage, thumbnailEditorHref } from '@/lib/editor/open-thumbnail-editor';
import type { ProjectFile } from '@/lib/types/project';
import { useDashboardOverview } from '@/lib/hooks/use-dashboard';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { plainErrorMessage } from '@/lib/api/network-error';
import { useOutputVideos } from '@/lib/hooks/use-outputs';
import { earlierTakeStorageKeys, groupTakes } from '@/lib/outputs/takes';
import { useProject } from '@/lib/hooks/use-projects';
import { useStorageUsage } from '@/lib/hooks/use-storage';
import { useWorkflowApps } from '@/lib/hooks/use-workflow-apps';
import { useWorkflows } from '@/lib/hooks/use-workflows';
import { isStorageNearlyFull, storageUsageSentence } from '@/lib/types/storage';
import { useFormat, useT } from '@/i18n';

// Every tab rendered below MUST be listed here: an unlisted value is treated as unknown and falls back
// to Overview, which is how the Voices tab was once unreachable from both a click and `?tab=voices`.
const TABS = ['overview', 'files', 'workflows', 'renders', 'voices', 'avatars', 'pronunciation', 'profile', 'repository', 'cloud', 'folders', 'history'] as const;
type Tab = (typeof TABS)[number];

function ProjectDetail({ id }: { id: string }) {
  const t = useT('projects.detail');
  const tr = useT();
  const fm = useFormat();
  const router = useRouter();
  const pathname = usePathname();
  const search = useSearchParams();
  const { data: project, isLoading, error, mutate: mutateProject } = useProject(id);
  const { data: files, error: filesError, mutate: mutateFiles } = useProjectFiles(id);
  const { data: workflows } = useWorkflows(id);
  const appsEnabled = useWorkflowAppsEnabled();
  const { welcomeProject } = useOnboarding();
  const starterKey = (() => {
    const first = welcomeProject();
    return first && first.id === id ? first.starter : null;
  })();
  const { data: appCards, error: appCardsError } = useWorkflowApps(appsEnabled && starterKey ? id : undefined);
  const { data: outputs, mutate: mutateOutputs } = useOutputVideos(id);
  const { data: overview, error: overviewError, mutate: mutateOverview } = useDashboardOverview();
  // The Cloud storage tab exists only when this server has at least one provider set up.
  const { data: cloudProviders } = useCloudProviders();
  const cloudAvailable = Boolean(cloudProviders?.some((p) => p.configured));
  // Workspace storage: at ≥ 90 % the page warns, whoever is looking and on whichever tab.
  const { data: storage } = useStorageUsage();
  // The Avatars tab exists only while the API answers with a kind switched on, people or objects (every avatar
  // route is 404 with both operator switches off).
  const { available: avatarsAvailable, requirements: avatarRequirements } = useAvatarsAvailable(id);
  const { data: avatars } = useProjectAvatars(id, avatarsAvailable);
  const avatarFiles = useMemo(() => avatarUsesByFile(avatars), [avatars]);

  const [editOpened, setEditOpened] = useState(false);
  const [deleteOpened, setDeleteOpened] = useState(false);
  const [deleteLoading, setDeleteLoading] = useState(false);
  const pickerRef = useRef<(() => void) | null>(null);
  const registerPicker = useCallback((open: () => void) => { pickerRef.current = open; }, []);

  const param = search.get('tab');
  const requestedTab: Tab = (TABS as readonly string[]).includes(param ?? '') ? (param as Tab) : 'overview';
  // `?tab=cloud` on a server with no cloud storage set up falls back to Overview, like any unknown tab.
  // `?tab=avatars` (the retention email links there) waits for the requirements answer, then falls back
  // to Overview when avatars are switched off on this server.
  const tab: Tab = (requestedTab === 'cloud' && cloudProviders && !cloudAvailable)
    || (requestedTab === 'avatars' && !avatarsAvailable) ? 'overview' : requestedTab;

  // Panels are lazy: a tab's content mounts only after that tab has been opened at least once, and
  // from then on stays mounted (the `keepMounted` contract below). The Files panel is the one
  // exception — it is rendered unconditionally because the page-wide drop zone and upload input
  // live there. Overview is the default tab, so it is visited from the first render.
  const [visited, setVisited] = useState<ReadonlySet<Tab>>(() => new Set<Tab>(['overview', tab]));
  // The URL can change without `setTab` (the browser's back button, a link straight to `?tab=`),
  // and a tab that is showing has been visited: keep it mounted from then on too.
  if (!visited.has(tab)) setVisited(new Set(visited).add(tab));

  const setTab = useCallback((next: string) => {
    if ((TABS as readonly string[]).includes(next)) {
      setVisited((prev) => (prev.has(next as Tab) ? prev : new Set(prev).add(next as Tab)));
    }
    const qs = new URLSearchParams(search.toString());
    qs.set('tab', next);
    router.replace(`${pathname}?${qs.toString()}`, { scroll: false });
  }, [router, pathname, search]);

  // The strip scrolls sideways on a phone: keep the open tab in view (also after a `?tab=` link).
  const tabListRef = useRef<HTMLDivElement>(null);
  // The strip only exists once the project has loaded, so that is a dependency too.
  const stripReady = Boolean(project) && !isLoading && !error;
  useEffect(() => {
    if (!stripReady) return;
    const el = tabListRef.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
    // jsdom has no scrollIntoView.
    if (el && typeof el.scrollIntoView === 'function') el.scrollIntoView({ block: 'nearest', inline: 'center' });
  }, [tab, stripReady]);

  // Takes (docs/video-editing.md "Takes: the latest render first"): Files lists each video's latest
  // take; the renders it replaced are one click away instead of flooding the list.
  const [showEarlierTakes, setShowEarlierTakes] = useState(false);
  const earlierTakeKeys = useMemo(() => earlierTakeStorageKeys(outputs ?? []), [outputs]);
  const hiddenTakeCount = useMemo(
    () => (files ?? []).filter((f) => earlierTakeKeys.has(f.storageKey)).length, [files, earlierTakeKeys]);
  const visibleFiles = useMemo(
    () => (files && !showEarlierTakes ? files.filter((f) => !earlierTakeKeys.has(f.storageKey)) : files),
    [files, showEarlierTakes, earlierTakeKeys]);

  const projectOverview = useMemo(() => overview?.projects.find((p) => p.projectId === id), [overview, id]);
  const active = useMemo(() => (overview?.activeExecutions ?? []).filter((e) => e.projectId === id), [overview, id]);
  const attention = useMemo(() => (overview?.needsAttention ?? []).filter((e) => e.projectId === id), [overview, id]);

  // One definition: the Renders count is VIDEOS (latest take of each), the same number the tab itself shows.
  const videoCount = useMemo(() => groupTakes(outputs ?? []).length, [outputs]);

  if (isLoading || error || !project) {
    return (
      <LoadState
        loading={isLoading}
        // A project that answered with nothing is "not found"; every other failure is worded by its own cause.
        error={error ?? (isLoading ? undefined : { status: 404 })}
        onRetry={() => void mutateProject()}
        backHref="/projects"
        backLabel={t('backToProjects')}
        resource={t('resource')}
      />
    );
  }

  const handleDeleteProject = async () => {
    setDeleteLoading(true);
    try {
      await deleteProject(id);
      notifications.show({ title: t('deletedTitle'), message: t('deletedMessage', { name: project.name }), color: 'green' });
      router.push('/projects');
    } catch {
      notifications.show({ title: t('deleteFailedTitle'), message: t('deleteFailedMessage'), color: 'red' });
    } finally {
      setDeleteLoading(false);
    }
  };

  const refreshFiles = async () => { await mutateFiles(); void mutateOverview(); };
  const editAsThumbnail = async (file: ProjectFile) => {
    try {
      router.push(thumbnailEditorHref(id, await documentForImage(id, file)));
    } catch (cause: unknown) {
      notifications.show({ title: t('thumbnailFailed'), message: plainErrorMessage(cause), color: 'red' });
    }
  };
  const fileCount = visibleFiles?.length ?? 0;
  // Clips and no code, with apps on: the page leads to the footage edit from Files and Overview.
  const clipCount = appsEnabled ? footageClipCount(files) : 0;
  // The first project the welcome flow made here, with the app its answer chose: after an upload that app is one click away.
  const starterCard = starterKey ? appCards?.apps.find((a) => a.source === starterKey) : undefined;
  // Never a second first run: anything already run or rendered in this project ends the one-click card.
  const hasRun = (outputs?.length ?? 0) > 0
    || (projectOverview ? projectOverview.renderCount + projectOverview.activeExecutionCount + projectOverview.failedExecutionCount > 0 : false);
  const starterPending = appsEnabled && Boolean(starterKey) && fileCount > 0 && ((!appCards && !appCardsError) || (!overview && !overviewError));
  const oneClickWorkflowId = appsEnabled && fileCount > 0 && !hasRun && overview ? (starterCard?.workflowId ?? null) : null;
  const categoryParam = search.get('category');
  const initialCategory: CategoryFilter = CATEGORY_FILTERS.find((c) => c.value === categoryParam)?.value ?? 'all';

  return (
    <>
      {/* The project this page is about, for the globally mounted assistant. Renders nothing. */}
      <AssistantPageContext projectId={id} />
      <PageHeader title={project.name} breadcrumbs={[{ label: t('breadcrumb'), href: '/projects' }, { label: project.name }]}>
        <Button
          leftSection={<IconUpload size={16} />} style={{ minHeight: 44 }}
          onClick={() => { setTab('files'); pickerRef.current?.(); }}
        >
          {t('uploadFiles')}
        </Button>
        <Menu position="bottom-end" withinPortal>
          <Menu.Target>
            <ActionIcon variant="default" size={44} aria-label={t('actions')}><IconDotsVertical size={18} /></ActionIcon>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Item leftSection={<IconEdit size={15} />} onClick={() => setEditOpened(true)}>{t('editNameDescription')}</Menu.Item>
            <Menu.Item color="red" leftSection={<IconTrash size={15} />} onClick={() => setDeleteOpened(true)}>{t('deleteProject')}</Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </PageHeader>
      {project.description && <Text size="sm" c="dimmed" maw={680} mt={-8} mb="md">{project.description}</Text>}
      {/* Visible from every tab, so a linked repository (and a failing sync) is never a surprise. */}
      <ProjectRepositoryIndicator projectId={id} onOpen={() => setTab('repository')} />
      {/* ≥ 90 % of the plan's storage: said once, above every tab, because any of them can be the one
          where the next upload is refused. */}
      {storage && isStorageNearlyFull(storage) && (
        <Box mb="md">
          <StorageQuotaBanner
            title={t('storageTitle')}
            message={t('storageMessage', { usage: storageUsageSentence(storage, tr, fm) })}
            testId="project-storage-warning"
          />
        </Box>
      )}

      {/*
        `keepMountedMode="display-none"` is load-bearing, not cosmetic. Mantine v9 keeps an inactive
        panel's state when you ask for `keepMounted`, but by default it does that with React's
        <Activity mode="hidden"> — which UNMOUNTS the hidden subtree's effects and detaches its DOM
        refs. Every part of the upload surface lives in the Files panel: the page-wide drop listeners
        (`useWindowFileDrop`), the upload queue, and the hidden <input type="file"> the header button
        clicks through (`registerPicker`). Under Activity they are all inert until something switches
        to the Files tab, so a file dropped anywhere on the project page did nothing and "Upload files"
        only changed the tab. Hiding the panel with `display: none` keeps it genuinely mounted - which
        is exactly what the comment on the Files panel below has always claimed - while staying hidden.
      */}
      <Tabs value={tab} onChange={(v) => v && setTab(v)} keepMounted keepMountedMode="display-none">
        <Tabs.List mb="md" className="project-tabs-list" ref={tabListRef}>
          <Tabs.Tab value="overview" leftSection={<span className="tab-icon"><IconInfoCircle size={16} /></span>} px="xs" className="project-tab">{t('tabOverview')}</Tabs.Tab>
          <Tabs.Tab value="files" leftSection={<span className="tab-icon"><IconFiles size={16} /></span>} px="xs" className="project-tab">{t('tabFiles')}{fileCount ? ` (${fileCount})` : ''}</Tabs.Tab>
          <Tabs.Tab value="workflows" leftSection={<span className="tab-icon"><IconTopologyRing size={16} /></span>} px="xs" className="project-tab">{appsEnabled ? t('tabApps') : t('tabWorkflows')}{workflows?.length ? ` (${workflows.length})` : ''}</Tabs.Tab>
          <Tabs.Tab value="renders" leftSection={<span className="tab-icon"><IconVideo size={16} /></span>} px="xs" className="project-tab">{t('tabRenders')}{videoCount ? ` (${videoCount})` : ''}</Tabs.Tab>
          <Tabs.Tab value="voices" leftSection={<span className="tab-icon"><IconMicrophone size={16} /></span>} px="xs" className="project-tab">{t('tabVoices')}</Tabs.Tab>
          {avatarsAvailable && (
            <Tabs.Tab value="avatars" leftSection={<span className="tab-icon"><IconUserCircle size={16} /></span>} px="xs" className="project-tab">{t('tabAvatars')}</Tabs.Tab>
          )}
          <Tabs.Tab value="pronunciation" leftSection={<span className="tab-icon"><IconLanguage size={16} /></span>} px="xs" className="project-tab">{t('tabPronunciation')}</Tabs.Tab>
          <Tabs.Tab value="profile" leftSection={<span className="tab-icon"><IconPalette size={16} /></span>} px="xs" className="project-tab">{t('tabProfile')}</Tabs.Tab>
          <Tabs.Tab value="repository" leftSection={<span className="tab-icon"><IconBrandGithub size={16} /></span>} px="xs" className="project-tab">{t('tabRepository')}</Tabs.Tab>
          {cloudAvailable && (
            <Tabs.Tab value="cloud" leftSection={<span className="tab-icon"><IconCloud size={16} /></span>} px="xs" className="project-tab">{t('tabCloud')}</Tabs.Tab>
          )}
          <Tabs.Tab value="folders" leftSection={<span className="tab-icon"><IconDeviceDesktop size={16} /></span>} px="xs" className="project-tab">{t('tabFolders')}</Tabs.Tab>
          <Tabs.Tab value="history" leftSection={<span className="tab-icon"><IconHistory size={16} /></span>} px="xs" className="project-tab">{t('tabHistory')}</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="overview">
          {/* Files and no run, and neither the one-click card nor the clips callout is leading: the quiet nudge. */}
          {projectOverview && project && hasFilesAndNoRun(projectOverview) && !hasRun && !oneClickWorkflowId && clipCount === 0 && (
            <Box mb="md"><RunNudge projectId={id} projectName={project.name} /></Box>
          )}
          {visited.has('overview') && (
            <ProjectOverviewTab
              project={project} overview={projectOverview} active={active} attention={attention}
              outputs={outputs ?? []} workflows={workflows ?? []} onOpenTab={setTab} clipCount={clipCount}
            />
          )}
        </Tabs.Panel>

        {/* Mounted even while another tab is showing, so a file dropped anywhere on the page uploads. */}
        <Tabs.Panel value="files">
          {hiddenTakeCount > 0 && (
            <Button variant="subtle" size="compact-sm" mb="xs" onClick={() => setShowEarlierTakes((v) => !v)}>
              {showEarlierTakes
                ? t('hideEarlierTakes')
                : t('showEarlierTakes', { count: hiddenTakeCount })}
            </Button>
          )}
          {starterPending ? null : oneClickWorkflowId ? (
            <Box mb="md"><OneClickRun projectId={id} workflowId={oneClickWorkflowId} /></Box>
          ) : (
            clipCount > 0 && <Box mb="md"><FootageReadyCallout projectId={id} count={clipCount} /></Box>
          )}
          {visibleFiles ? (
            <FileManager projectId={id} files={visibleFiles} onChanged={refreshFiles} registerPicker={registerPicker} onEditAsThumbnail={(f) => void editAsThumbnail(f)} avatarUsesByFile={avatarFiles} />
          ) : (
            <LoadState loading={!filesError} error={filesError} onRetry={() => void mutateFiles()} />
          )}
        </Tabs.Panel>

        <Tabs.Panel value="workflows">
          {visited.has('workflows') && (appsEnabled
            ? <AppsGallery key={initialCategory} projectId={id} initialCategory={initialCategory} />
            : <ProjectWorkflowsTab projectId={id} workflows={workflows ?? []} />)}
        </Tabs.Panel>
        <Tabs.Panel value="renders">
          {visited.has('renders') && (
            <ProjectRendersTab project={project} outputs={outputs ?? []} onChanged={async () => { await mutateOutputs(); await refreshFiles(); }} />
          )}
        </Tabs.Panel>
        <Tabs.Panel value="voices">
          {visited.has('voices') && <ProjectVoicesTab projectId={id} />}
        </Tabs.Panel>
        {avatarsAvailable && avatarRequirements && (
          <Tabs.Panel value="avatars">
            {visited.has('avatars') && <ProjectAvatarsTab projectId={id} requirements={avatarRequirements} />}
          </Tabs.Panel>
        )}
        <Tabs.Panel value="pronunciation">
          {visited.has('pronunciation') && <ProjectPronunciationTab projectId={id} />}
        </Tabs.Panel>
        <Tabs.Panel value="profile">
          {visited.has('profile') && <TabErrorBoundary><ProjectProfileTab projectId={id} /></TabErrorBoundary>}
        </Tabs.Panel>
        {/* `?github=` is the short token the OAuth callback redirects back with; the tab reads it
            once and clears it from the URL, so a reload does not re-announce a finished connection. */}
        <Tabs.Panel value="repository">
          {visited.has('repository') && (
            <ProjectGitHubTab
              projectId={id}
              connectOutcome={search.get('github')}
            />
          )}
        </Tabs.Panel>
        {/* `?cloud=` is what the account-connection callback returns with; the tab reads it once and
            strips it from the URL. */}
        {cloudAvailable && (
          <Tabs.Panel value="cloud">
            {visited.has('cloud') && <ProjectCloudStorageTab projectId={id} />}
          </Tabs.Panel>
        )}
        {/* Always available, whatever this server has set up: an empty list is the way the desktop
            app is discovered, and the tab is where the app's own guide is linked from. */}
        <Tabs.Panel value="folders">
          {visited.has('folders') && <ProjectFolderLinksTab projectId={id} />}
        </Tabs.Panel>
        <Tabs.Panel value="history">
          {visited.has('history') && <TabErrorBoundary><ProjectHistoryTab projectId={id} /></TabErrorBoundary>}
        </Tabs.Panel>
      </Tabs>

      <ProjectForm opened={editOpened} onClose={() => setEditOpened(false)} onSuccess={() => { void mutateProject(); void mutateOverview(); }} project={project} />
      <ConfirmModal
        opened={deleteOpened} onClose={() => setDeleteOpened(false)} onConfirm={handleDeleteProject}
        title={t('deleteConfirmTitle')} message={t('deleteConfirmMessage')} loading={deleteLoading}
      />
    </>
  );
}

export default function ProjectDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  return (
    <Suspense fallback={<Center h={300}><Loader /></Center>}>
      <ProjectDetail id={id} />
    </Suspense>
  );
}
