'use client';

import React, { use, useCallback, useEffect, useRef, useState } from 'react';
import { Button, Center, Group, Loader, Stack, Text } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import Link from 'next/link';
import { ApiError } from '@/lib/api/client';
import { createEditorStore, type EditorStore } from '@/lib/editing/store';
import { readStoredQuality } from '@/lib/editing/preview/device-budget';
import { createSyncClient, getEditTimeline, type SyncClient } from '@/lib/editing/sync';
import { EditorWorkspace } from '@/components/editor/EditorWorkspace';
import { editorHubHref } from '@/lib/editing/open-timeline';
import { initialiseEditorPage, pageToWorkspace } from '@/lib/editing/page-url';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface Props {
  params: Promise<{ id: string; timelineId: string }>;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'not-found' }
  | { kind: 'forbidden' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; store: EditorStore; sync: SyncClient };

/**
 * The editable timeline route: `/editor/{projectId}/timeline/{timelineId}`.
 *
 * A thin client route: it loads the document through the sync client's GET, mounts the store and
 * the sync client, and hands off to `EditorWorkspace`. The four load failures are distinguished and
 * said in words — a 404 and a 403 render different text, because collapsing them tells the user
 * their timeline was deleted when it was not.
 */
export default function TimelineEditorPage({ params }: Props) {
  const t = useT('editor.pages.editorTimelinePage');
  const { id: projectId, timelineId } = use(params);
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [reloadKey, setReloadKey] = useState(0);
  const disposers = useRef<(() => void)[]>([]);
  const [seedStepResultId, setSeedStepResultId] = useState<string | null>(null);

  // The fetch is the external system here: every state write happens after the await, never in the
  // effect body itself, and a response for a route the viewer already left is dropped.
  const tAll = useT();
  const loadFailed = t('theTimelineCouldNot');
  useEffect(() => {
    let cancelled = false;
    // T-001: the page is in the URL. Resolve it (unknown -> Edit, `color` -> `colour`), or restore
    // this device's last page when a link carries none, and normalise the URL before the store is
    // made so the first workspace render already shows the right page.
    const page = initialiseEditorPage({
      history: window.history,
      href: window.location.href,
      storage: window.localStorage,
    });
    getEditTimeline(projectId, timelineId)
      .then((response) => {
        if (cancelled) return;
        setSeedStepResultId(response.seedStepResultId);
        const store = createEditorStore({
          projectId,
          timelineId,
          doc: response.document,
          revision: response.revision,
          ui: { workspace: pageToWorkspace(page) },
        });
        // R04.5: the preview quality this person last picked on this device (Auto when they never did).
        store.setUi({ playbackResolution: readStoredQuality() });
        // R02.5: `canEdit` false (a viewer) opens it view-only; R02.1: watch for the same timeline in another tab.
        const sync = createSyncClient({ store, canEdit: response.canEdit !== false, presence: true, onIdentityChanged: () => setReloadKey((k) => k + 1) });
        sync.attachLifecycle(window);
        // P9: edits queued offline by an earlier session are reconciled against what just loaded;
        // R02.2: then a journal from an earlier visit is offered back (Restore / Discard).
        void sync.restoreQueued().then(() => sync.checkRecovery());
        disposers.current.forEach((d) => d());
        disposers.current = [() => sync.dispose()];
        setState({ kind: 'ready', store, sync });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 404) setState({ kind: 'not-found' });
        else if (error instanceof ApiError && error.status === 403) setState({ kind: 'forbidden' });
        else setState({ kind: 'error', message: describeError(tAll, error, loadFailed) });
      });
    return () => {
      cancelled = true;
      disposers.current.forEach((d) => d());
      disposers.current = [];
    };
  }, [projectId, timelineId, reloadKey, loadFailed, tAll]);

  const reload = useCallback(() => {
    setState({ kind: 'loading' });
    setReloadKey((k) => k + 1);
  }, []);

  const back = editorHubHref(projectId);
  const message = (testId: string, title: string, text: string, actions?: React.ReactNode) => (
    <Center mih="70dvh" px="md">
      <Stack align="center" gap="sm" maw={420} ta="center" data-testid={testId}>
        <IconAlertCircle size={28} stroke={1.4} color="var(--mantine-color-dimmed)" />
        <Text fw={600}>{title}</Text>
        <Text size="sm" c="dimmed">
          {text}
        </Text>
        <Group gap="xs" mt="xs">
          {actions}
          <Button component={Link} href={back} variant="default" size="sm">
            {t('allTimelines')}
          </Button>
        </Group>
      </Stack>
    </Center>
  );

  switch (state.kind) {
    case 'loading':
      return (
        <Center mih="70dvh" data-testid="timeline-loading">
          <Stack align="center" gap="sm">
            <Loader size="sm" type="dots" />
            <Text size="sm" c="dimmed">
              {t('openingTheTimeline')}
            </Text>
          </Stack>
        </Center>
      );
    case 'not-found':
      return message('timeline-not-found', t('timelineNotFound'), t('thereIsNoTimeline'));
    case 'forbidden':
      return message('timeline-forbidden', t('youDoNotHave'), t('theTimelineExistsBut'));
    case 'error':
      return message(
        'timeline-load-error',
        t('theTimelineCouldNot2'),
        state.message,
        <Button size="sm" onClick={reload}>
          {t('tryAgain')}
        </Button>,
      );
    case 'ready':
      return <EditorWorkspace store={state.store} sync={state.sync} onReload={reload} seedStepResultId={seedStepResultId} />;
    default:
      return null;
  }
}
