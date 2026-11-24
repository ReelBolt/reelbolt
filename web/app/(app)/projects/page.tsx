'use client';

import { Suspense, useCallback, useMemo, useState } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { Alert, Button, Group, Select, SegmentedControl, Skeleton, Stack, Text, TextInput, VisuallyHidden } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconChecks, IconFolderPlus, IconSearch, IconWand } from '@tabler/icons-react';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { ProjectCard } from '@/components/projects/ProjectCard';
import { ProjectForm } from '@/components/projects/ProjectForm';
import { ProjectRail } from '@/components/projects/ProjectRail';
import { BulkBar } from '@/components/projects/BulkBar';
import { ManageOrganizationModal } from '@/components/projects/ManageOrganizationModal';
import { TagActivityLink } from '@/components/tags/TagActivityLink';
import { projectOrganizationErrorMessage } from '@/lib/api/project-organization';
import { useProjectFolders } from '@/lib/hooks/use-project-folders';
import { useProjectTagMutations, useProjectTags } from '@/lib/hooks/use-project-tags';
import { parseOrganizationParams, toggleTag, withOrganizationParams } from '@/lib/projects/organization-url';
import { useDashboardOverview } from '@/lib/hooks/use-dashboard';
import { useProjects } from '@/lib/hooks/use-projects';
import { useAuth } from '@/lib/hooks/use-auth';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import type { ProjectOverview } from '@/lib/types/dashboard';
import { filterAndSortProjects, type FolderSelection, type ProjectFilter as Filter, type ProjectSort as Sort } from '@/lib/projects/filter';
import { useT } from '@/i18n';
import classes from '@/components/projects/projects.module.css';

export default function ProjectsPage() {
  // useSearchParams needs a Suspense boundary for static rendering.
  return (
    <Suspense fallback={null}>
      <ProjectsPageContent />
    </Suspense>
  );
}

function ProjectsPageContent() {
  const t = useT('projects.page');
  const to = useT('projects.organization');
  const tr = useT();
  const FILTERS: { value: Filter; label: string }[] = [
    { value: 'all', label: t('filterAll') },
    { value: 'running', label: t('filterRunning') },
    { value: 'renders', label: t('filterRenders') },
    { value: 'empty', label: t('filterEmpty') },
  ];
  const router = useRouter();
  const { user } = useAuth();
  const { openWithPrompt } = useAssistantShell();
  const { data: projects, isLoading, error, mutate } = useProjects();
  const { data: overview, mutate: mutateOverview } = useDashboardOverview();
  const [formOpened, setFormOpened] = useState(false);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const [sort, setSort] = useState<Sort>('activity');
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const { folder, tagIds } = useMemo(() => {
    const raw = parseOrganizationParams(new URLSearchParams(searchParams?.toString() ?? ''));
    const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    return {
      folder: raw.folder === 'all' || raw.folder === 'unfiled' || guid.test(raw.folder) ? raw.folder : 'all',
      tagIds: raw.tagIds.filter((id) => guid.test(id)),
    };
  }, [searchParams]);
  const { data: folders = [], error: foldersError, mutate: mutateFolders } = useProjectFolders();
  const { data: tags = [], error: tagsError, mutate: mutateTags } = useProjectTags();
  const { setOrganization } = useProjectTagMutations();
  const [manageOpened, setManageOpened] = useState(false);
  const [selecting, setSelecting] = useState(false);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');

  const select = useCallback(
    (next: { folder: FolderSelection; tagIds: string[] }) => {
      const qs = withOrganizationParams(new URLSearchParams(searchParams?.toString() ?? ''), next).toString();
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false });
    },
    [router, pathname, searchParams],
  );

  const byId = useMemo(
    () => new Map<string, ProjectOverview>((overview?.projects ?? []).map((p) => [p.projectId, p])),
    [overview],
  );
  const visible = useMemo(
    () => filterAndSortProjects(projects ?? [], byId, { query, filter, sort, folder, tagIds }),
    [projects, byId, query, filter, sort, folder, tagIds],
  );
  const total = projects?.length ?? 0;
  const unfiledCount = (projects ?? []).filter((p) => !p.folderId).length;
  const selection = folder !== 'all' || tagIds.length > 0;
  const filtering = query.trim() !== '' || filter !== 'all' || selection;
  const railFailed = !!foldersError || !!tagsError;
  const bulkLabel = selecting ? to('bulk.exit') : to('bulk.enter');
  const railFailedLabel = to('rail.loadFailed');
  const retryLabel = to('rail.retry');

  const moveByDrop = async (folderId: string | null, projectId: string) => {
    const p = (projects ?? []).find((x) => x.id === projectId);
    if (!p) return;
    try {
      await setOrganization(p.id, { tagIds: (p.tags ?? []).map((x) => x.id), folderId });
      const name = folders.find((f) => f.id === folderId)?.name ?? to('rail.unfiled');
      setAnnouncement(to('announce.moved', { name: p.name, folder: name }));
    } catch (err) {
      notifications.show({ title: to('move.failed'), message: projectOrganizationErrorMessage(err, tr), color: 'red' });
    }
  };

  const toggleSelecting = () => { setSelecting((v) => !v); setSelectedIds([]); };


  return (
    <>
      <PageHeader title={t('title')}>
        <Group gap="xs">
          <TagActivityLink />
          {total > 0 ? (
            <Button variant={selecting ? 'filled' : 'default'} leftSection={<IconChecks size={16} />} onClick={toggleSelecting} style={{ minHeight: 44 }} aria-pressed={selecting}>
              {bulkLabel}
            </Button>
          ) : null}
          <Button leftSection={<IconFolderPlus size={16} />} onClick={() => setFormOpened(true)} style={{ minHeight: 44 }}>
            {t('newProject')}
          </Button>
        </Group>
      </PageHeader>
      <VisuallyHidden role="status" aria-live="polite">{announcement}</VisuallyHidden>

      {error && (
        <Alert color="red" icon={<IconAlertCircle size={16} />} title={t('loadFailed')} mb="md">
          <Group justify="space-between">
            <Text size="sm">{t('loadFailedHint')}</Text>
            <Button size="xs" variant="light" color="red" onClick={() => mutate()}>{t('retry')}</Button>
          </Group>
        </Alert>
      )}

      {isLoading ? (
        <div className={classes.grid}>
          {Array.from({ length: 6 }).map((_, i) => <Skeleton key={i} height={280} radius="lg" />)}
        </div>
      ) : total === 0 && !error ? (
        <EmptyState title={t('emptyTitle')} description={t('emptyBody')}>
          <Group>
            <Button onClick={() => setFormOpened(true)} leftSection={<IconFolderPlus size={16} />} style={{ minHeight: 44 }}>{t('createProject')}</Button>
            <Button
              variant="default" leftSection={<IconWand size={16} />} style={{ minHeight: 44 }}
              onClick={() => openWithPrompt(t('planPrompt'))}
            >
              {t('planWithAssistant')}
            </Button>
          </Group>
        </EmptyState>
      ) : (
        <div className={classes.layout}>
          <div>
            <ProjectRail
              folders={folders}
              tags={tags}
              totalCount={total}
              unfiledCount={unfiledCount}
              folder={folder}
              tagIds={tagIds}
              dropTarget={dropTarget}
              onFolder={(f) => select({ folder: f, tagIds })}
              onToggleTag={(id) => select({ folder, tagIds: toggleTag(tagIds, id) })}
              onClearTags={() => select({ folder, tagIds: [] })}
              onManage={() => setManageOpened(true)}
              onDropOnFolder={(id, projectId) => void moveByDrop(id, projectId)}
              onDropTarget={setDropTarget}
            />
            {railFailed ? (
              <Alert color="red" mt="sm" icon={<IconAlertCircle size={16} />}>
                <Text size="sm">{railFailedLabel}</Text>
                <Button size="xs" variant="light" color="red" mt="xs" onClick={() => { void mutateFolders(); void mutateTags(); }}>{retryLabel}</Button>
              </Alert>
            ) : null}
          </div>
          <div style={{ minWidth: 0 }}>
          {selecting ? <BulkBar selectedIds={selectedIds} tags={tags} folders={folders} onDone={toggleSelecting} /> : null}
          <div className={classes.toolbar}>
            <TextInput
              aria-label={t('search')}
              placeholder={t('search')}
              leftSection={<IconSearch size={16} />}
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              size="md"
              rightSectionPointerEvents="all"
            />
            <div className={classes.filterScroll}>
              <SegmentedControl
                aria-label={t('filterLabel')}
                value={filter}
                onChange={(v) => setFilter(v as Filter)}
                data={FILTERS}
                size="sm"
              />
            </div>
            <Select
              aria-label={t('sortLabel')}
              value={sort}
              onChange={(v) => v && setSort(v as Sort)}
              allowDeselect={false}
              size="md"
              data={[
                { value: 'activity', label: t('sortActivity') },
                { value: 'name', label: t('sortName') },
                { value: 'created', label: t('sortCreated') },
                { value: 'files', label: t('sortFiles') },
              ]}
            />
          </div>

          {visible.length === 0 ? (
            <Stack align="center" py="xl" gap="xs">
              <Text fw={600}>{t('noMatch')}</Text>
              <Text size="sm" c="dimmed">{t('noMatchHint')}</Text>
              <Button variant="light" onClick={() => { setQuery(''); setFilter('all'); select({ folder: 'all', tagIds: [] }); }} style={{ minHeight: 44 }}>{t('clearFilters')}</Button>
            </Stack>
          ) : (
            <>
              <Text size="xs" c="dimmed" mb="sm" aria-live="polite">
                {filtering ? t('countFiltered', { visible: visible.length, total }) : t('count', { count: total })}
              </Text>
              <div className={classes.grid}>
                {visible.map((p) => (
                  <ProjectCard
                    key={p.id}
                    project={p}
                    overview={byId.get(p.id)}
                    organization={{
                      tags,
                      folders,
                      showFolderCrumb: folder === 'all',
                      selecting,
                      selected: selectedIds.includes(p.id),
                      onToggleSelect: () => setSelectedIds((ids) => (ids.includes(p.id) ? ids.filter((x) => x !== p.id) : [...ids, p.id])),
                      onDragStart: () => setAnnouncement(to('announce.pickedUp', { name: p.name })),
                    }}
                  />
                ))}
              </div>
            </>
          )}
          </div>
        </div>
      )}

      <ManageOrganizationModal opened={manageOpened} onClose={() => setManageOpened(false)} tags={tags} folders={folders} />

      <ProjectForm
        defaultFolderId={folder !== 'all' && folder !== 'unfiled' ? folder : null}
        opened={formOpened}
        onClose={() => setFormOpened(false)}
        onSuccess={(p) => {
          // alpha-launch T-018: the real transition — a project was just created. Fires once per
          // user (the funnel's marker makes a later creation a no-op), never on a page load, and
          // only with consent; a refusal here changes nothing the person can see.
          emitFunnelEvent('alpha.first_project', userKeyFromEmail(user?.email));
          void mutate();
          void mutateOverview();
          if (p) router.push(`/projects/${encodeURIComponent(p.id)}?tab=files`);
        }}
      />
    </>
  );
}
