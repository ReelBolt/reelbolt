'use client';

import { Fragment, useCallback, useEffect, useState } from 'react';
import {
  ActionIcon,
  Alert,
  Button,
  Collapse,
  Group,
  Loader,
  Paper,
  ScrollArea,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import {
  IconAlertCircle,
  IconCheck,
  IconHistory,
  IconMessages,
  IconPencil,
  IconSearch,
  IconPlus,
  IconTrash,
  IconX,
} from '@tabler/icons-react';
import { createThread, deleteThread, listThreads, renameThread } from '../../lib/api/assistant';
import { plainText, withoutAttachId } from '../../lib/assistant/plain-text';
import type { AssistantThreadSummary } from '../../lib/types/assistant';
import { useAssistant } from './AssistantShellProvider';
import { useFormat, useT } from '@/i18n';
import type { Format } from '@/i18n/format';
import type { T } from '@/i18n/translator';
import { describeError } from '@/lib/api/describe-error';
import { useLatest } from './useLatest';

/** The region the History toggle controls, so `aria-controls` has something to point at. */
const HISTORY_PANEL_ID = 'assistant-history-panel';

/** Every control here is a 44px-class target: the list is the densest touch surface in the app. */
const TARGET_SIZE = 44;

interface ThreadGroup {
  label: string;
  threads: AssistantThreadSummary[];
}

/** Today / Yesterday / Earlier buckets, in recency order, empty buckets omitted. */
function groupByRecency(t: T, threads: readonly AssistantThreadSummary[], now = new Date()): ThreadGroup[] {
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const startOfYesterday = startOfToday - 24 * 60 * 60 * 1000;
  const buckets: ThreadGroup[] = [
    { label: t('assistant.threads.today'), threads: [] },
    { label: t('assistant.threads.yesterday'), threads: [] },
    { label: t('assistant.threads.earlier'), threads: [] },
  ];
  for (const thread of threads) {
    const at = new Date(thread.updatedAt).getTime();
    const bucket = at >= startOfToday ? buckets[0] : at >= startOfYesterday ? buckets[1] : buckets[2];
    bucket.threads.push(thread);
  }
  return buckets.filter((bucket) => bucket.threads.length > 0);
}

function relativeTime(t: T, f: Format, iso: string, now = Date.now()): string {
  const diff = Math.max(0, now - new Date(iso).getTime());
  const minutes = Math.floor(diff / 60000);
  if (minutes < 1) return t('assistant.threads.justNow');
  if (minutes < 60) return t('assistant.threads.minutesAgo', { minutes });
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return t('assistant.threads.hoursAgo', { hours });
  return new Date(iso).toLocaleDateString(f.locale, { month: 'short', day: 'numeric' });
}

type ListStatus = 'idle' | 'loading' | 'ready' | 'error';

/** One list load's outcome, tagged with the request that produced it so a stale one is ignored. */
interface ListLoad {
  request: string;
  threads: AssistantThreadSummary[];
  failed: boolean;
  message: string | null;
}

/** Most recent first. The server already orders this way; a client-side sort keeps it true even
 * after a rename, which touches `updatedAt` without a round trip through the list. */
function byRecency(threads: readonly AssistantThreadSummary[]): AssistantThreadSummary[] {
  return [...threads].sort(
    (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
  );
}

function errorMessage(t: T, cause: unknown): string {
  return describeError(t, cause, t('assistant.threads.loadFailed'));
}

/**
 * The assistant's server-persisted conversations: the list, plus everything that acts on one.
 *
 * It holds NO selected-thread state of its own — the selected thread is the shell's `threadId`,
 * read here and written through `setThreadId`, so the drawer's runtime, the list's highlight and
 * the transport all agree on which conversation is open. What it does own is the list's own view
 * state: whether the panel is expanded, which row is mid-rename, and which row is asking to be
 * deleted.
 *
 * Choosing a conversation therefore closes the panel — the conversation is what the user came for,
 * and the runtime above this subtree may be re-created to restore it. The list re-loads on the next
 * expand, which is also what keeps it honest about a thread renamed on another surface.
 */
export function AssistantThreadList() {
  const t = useT();
  const tRef = useLatest(t);
  const f = useFormat();
  const { opened, threadId, setThreadId, selectionToken, context, threadsVersion } = useAssistant();

  const [expanded, setExpanded] = useState(false);
  const [load, setLoad] = useState<ListLoad | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameDraft, setRenameDraft] = useState('');
  const [pendingDeleteId, setPendingDeleteId] = useState<string | null>(null);
  const [search, setSearch] = useState('');

  const refresh = useCallback(() => setAttempt((count) => count + 1), []);

  // The request this render belongs to. The loading state is DERIVED from it rather than written
  // by the effect, so opening the panel reports "loading" on the same render that opens it, with
  // no second pass through React to get there.
  const request = `open#${attempt}#${threadsVersion}`;

  useEffect(() => {
    if (!expanded) return;

    let cancelled = false;

    listThreads()
      .then((result) => {
        if (cancelled) return;
        // A conversation is named after what was said first, which can be a message that carried an id.
        const named = result.map((thread) => ({ ...thread, title: plainText(withoutAttachId(thread.title)).trim() || tRef.current('assistant.threads.untitled') }));
        setLoad({ request, threads: byRecency(named), failed: false, message: null });
      })
      .catch((cause: unknown) => {
        if (cancelled) return;
        setLoad({ request, threads: [], failed: true, message: errorMessage(tRef.current, cause) });
      });

    return () => {
      cancelled = true;
    };
  }, [expanded, request, tRef]);

  // Conversations still waiting for a reply, checked each time the assistant opens (and after every
  // turn) even while History is collapsed, so a question asked before a reload or a navigation is
  // never out of sight: the customer otherwise landed in a fresh, empty chat with no hint that the
  // earlier one existed. Only while the drawer is OPEN, so a closed assistant costs nothing.
  const [waiting, setWaiting] = useState<AssistantThreadSummary[]>([]);
  useEffect(() => {
    if (!opened || expanded) return;

    let cancelled = false;
    listThreads()
      .then((rows) => {
        if (cancelled || !Array.isArray(rows)) return;
        setWaiting(byRecency(rows.filter((row) => row.awaitingReply === true || row.replyInProgress === true)));
      })
      .catch(() => {
        // A hint, not a feature: the History panel reports list failures properly.
      });

    return () => {
      cancelled = true;
    };
  }, [opened, expanded, threadsVersion]);
  const waitingElsewhere = waiting.filter((row) => row.id !== threadId);

  const status: ListStatus = !expanded
    ? 'idle'
    : load?.request === request
      ? load.failed
        ? 'error'
        : 'ready'
      : 'loading';
  const threads = load?.threads ?? [];
  const error = status === 'error' ? (load?.message ?? null) : null;

  /** Applies a local edit (a rename, a delete) to the rows already on screen, before the refetch. */
  const updateThreads = useCallback(
    (update: (rows: AssistantThreadSummary[]) => AssistantThreadSummary[]) => {
      setLoad((current) => (current ? { ...current, threads: update(current.threads) } : current));
    },
    [],
  );

  /**
   * Opens a brand-new conversation and makes it the selected one.
   *
   * `createThread` is ASYNCHRONOUS, so this is a writer that can land late. It stamps the shell's
   * selection token BEFORE the request and re-reads it AFTER: if it moved, the user chose something
   * else — opened another conversation, or asked for a new chat of their own — while this one was
   * in flight, and this result is stale. Binding it anyway is what QA-PROBE J caught: the user picks
   * conversation B, its history loads, and this create then lands and replaces B with a fresh empty
   * thread C, so the next message is filed under a conversation the user never chose.
   *
   * The stamp is taken from a ref the shell bumps synchronously on every claim, never from a value
   * captured in this render: a value from the closure would be the selection as of the render that
   * started the request, which cannot answer "did anything happen while I was waiting".
   */
  const startNewChat = useCallback(async () => {
    setBusy(true);
    setActionError(null);
    const stamp = selectionToken();
    try {
      const { id } = await createThread(context.projectId ? { projectId: context.projectId } : undefined);
      if (selectionToken() !== stamp) return;
      setThreadId(id);
      refresh();
    } catch (cause: unknown) {
      setActionError(errorMessage(tRef.current, cause));
    } finally {
      setBusy(false);
    }
  }, [context.projectId, refresh, selectionToken, setThreadId, tRef]);

  const submitRename = useCallback(
    async (thread: AssistantThreadSummary) => {
      const title = renameDraft.trim();
      setRenamingId(null);
      if (!title || title === thread.title) return;

      setBusy(true);
      setActionError(null);
      try {
        await renameThread(thread.id, title);
        updateThreads((rows) =>
          rows.map((row) => (row.id === thread.id ? { ...row, title } : row)),
        );
        refresh();
      } catch (cause: unknown) {
        setActionError(errorMessage(tRef.current, cause));
      } finally {
        setBusy(false);
      }
    },
    [refresh, renameDraft, updateThreads, tRef],
  );

  const confirmDelete = useCallback(
    async (thread: AssistantThreadSummary) => {
      setPendingDeleteId(null);
      setBusy(true);
      setActionError(null);
      try {
        await deleteThread(thread.id);
        updateThreads((rows) => rows.filter((row) => row.id !== thread.id));
        // Deleting the conversation you are in has to leave you somewhere: a fresh thread, so the
        // composer keeps a thread to send to instead of addressing a row that no longer exists.
        if (thread.id === threadId) await startNewChat();
        else refresh();
      } catch (cause: unknown) {
        setActionError(errorMessage(tRef.current, cause));
      } finally {
        setBusy(false);
      }
    },
    [refresh, startNewChat, threadId, updateThreads, tRef],
  );

  const query = search.trim().toLowerCase();
  const visible = query ? threads.filter((th) => th.title.toLowerCase().includes(query)) : threads;
  const groups = groupByRecency(t, visible);
  const currentTitle = threads.find((t) => t.id === threadId)?.title;

  const renderRow = (thread: AssistantThreadSummary) => {
                  const active = thread.id === threadId;

                  if (renamingId === thread.id) {
                    return (
                      <Group key={thread.id} gap={4} wrap="nowrap">
                        <TextInput
                          size="md"
                          flex={1}
                          value={renameDraft}
                          onChange={(event) => setRenameDraft(event.currentTarget.value)}
                          aria-label={t('assistant.threads.newTitleFor', { title: thread.title })}
                          onKeyDown={(event) => {
                            if (event.key === 'Enter') void submitRename(thread);
                            if (event.key === 'Escape') setRenamingId(null);
                          }}
                        />
                        <ActionIcon
                          size={TARGET_SIZE}
                          variant="light"
                          color="violet"
                          aria-label={t('assistant.threads.saveTitleFor', { title: thread.title })}
                          onClick={() => void submitRename(thread)}
                        >
                          <IconCheck size={18} stroke={2} />
                        </ActionIcon>
                        <ActionIcon
                          size={TARGET_SIZE}
                          variant="subtle"
                          color="gray"
                          aria-label={t('assistant.threads.cancelRename', { title: thread.title })}
                          onClick={() => setRenamingId(null)}
                        >
                          <IconX size={18} stroke={2} />
                        </ActionIcon>
                      </Group>
                    );
                  }

                  if (pendingDeleteId === thread.id) {
                    return (
                      <Group
                        key={thread.id}
                        gap={4}
                        wrap="nowrap"
                        role="group"
                        aria-label={t('assistant.threads.confirmDeleting', { title: thread.title })}
                      >
                        <Text size="sm" flex={1}>
                          {t('assistant.threads.deletePrompt')}
                        </Text>
                        <ActionIcon
                          size={TARGET_SIZE}
                          variant="light"
                          color="red"
                          aria-label={t('assistant.threads.confirmDelete', { title: thread.title })}
                          onClick={() => void confirmDelete(thread)}
                        >
                          <IconCheck size={18} stroke={2} />
                        </ActionIcon>
                        <ActionIcon
                          size={TARGET_SIZE}
                          variant="subtle"
                          color="gray"
                          aria-label={t('assistant.threads.cancelDelete', { title: thread.title })}
                          onClick={() => setPendingDeleteId(null)}
                        >
                          <IconX size={18} stroke={2} />
                        </ActionIcon>
                      </Group>
                    );
                  }

                  return (
                    <Group key={thread.id} gap={4} wrap="nowrap">
                      <Button
                        size="md"
                        h={TARGET_SIZE}
                        flex={1}
                        justify="flex-start"
                        variant={active ? 'light' : 'subtle'}
                        color={active ? 'violet' : 'gray'}
                        styles={{
                          root: {
                            borderLeft: active
                              ? '3px solid var(--mantine-color-violet-6)'
                              : '3px solid transparent',
                          },
                          label: { minWidth: 0 },
                        }}
                        aria-current={active ? 'true' : undefined}
                        aria-label={t('assistant.threads.open', { title: thread.title })}
                        onClick={() => {
                          // The panel has done its job once a conversation is chosen.
                          setExpanded(false);
                          // SYNCHRONOUS, so there is nothing to stamp and nothing to drop: this is
                          // the user speaking, it applies immediately, and `setThreadId` advances the
                          // selection token on the way through — which is what lets an asynchronous
                          // writer still in flight (this list's own "New chat", or the drawer's
                          // visit-create) notice that this pick superseded it.
                          setThreadId(thread.id);
                        }}
                      >
                        <Stack gap={0} align="flex-start" style={{ minWidth: 0 }}>
                          <Text size="sm" fw={active ? 600 : 500} truncate maw="100%">
                            {thread.title}
                          </Text>
                          <Text size="xs" c="dimmed">
                            {relativeTime(t, f, thread.updatedAt)}
                            {thread.replyInProgress ? t('assistant.threads.stillAnsweringTag') : thread.awaitingReply ? t('assistant.threads.noReplyTag') : ''}
                          </Text>
                        </Stack>
                      </Button>
                      <ActionIcon
                        size={TARGET_SIZE}
                        variant="subtle"
                        color="gray"
                        aria-label={t('assistant.threads.rename', { title: thread.title })}
                        onClick={() => {
                          setRenamingId(thread.id);
                          setRenameDraft(thread.title);
                        }}
                      >
                        <IconPencil size={18} stroke={2} />
                      </ActionIcon>
                      <ActionIcon
                        size={TARGET_SIZE}
                        variant="subtle"
                        color="red"
                        aria-label={t('assistant.threads.delete', { title: thread.title })}
                        onClick={() => setPendingDeleteId(thread.id)}
                      >
                        <IconTrash size={18} stroke={2} />
                      </ActionIcon>
                    </Group>
                  );
                };

  return (
    <Stack gap={0} data-testid="assistant-thread-list">
      <Group gap={8} px="sm" py={6} wrap="nowrap">
        {/* A labelled button, not a bare icon: the past conversations were hard to find behind an
            unlabelled clock. Its accessible name is still exactly "History". */}
        <Button
          size="md"
          h={TARGET_SIZE}
          variant="subtle"
          color="gray"
          leftSection={<IconHistory size={18} stroke={2} />}
          aria-expanded={expanded}
          aria-controls={HISTORY_PANEL_ID}
          onClick={() => setExpanded((value) => !value)}
        >
          {t('assistant.threads.history')}
        </Button>
        <Button
          size="md"
          h={TARGET_SIZE}
          variant="light"
          color="violet"
          leftSection={<IconPlus size={18} stroke={2} />}
          onClick={startNewChat}
          loading={busy}
        >
          {t('assistant.threads.newChat')}
        </Button>
      </Group>

      {!expanded && waitingElsewhere.length > 0 ? (
        <Paper
          mx="sm"
          mb="sm"
          p="xs"
          withBorder
          radius="md"
          role="status"
          aria-live="polite"
          data-testid="assistant-waiting-threads"
        >
          <Group gap={8} wrap="nowrap" justify="space-between">
            <Text size="sm" style={{ minWidth: 0 }} truncate>
              {waitingElsewhere[0].replyInProgress
                ? t('assistant.threads.waitingAnswering', { title: waitingElsewhere[0].title })
                : t('assistant.threads.waitingNoReply', { title: waitingElsewhere[0].title })}
              {waitingElsewhere.length > 1 ? t('assistant.threads.waitingMore', { count: waitingElsewhere.length - 1 }) : ''}
            </Text>
            <Button
              size="compact-sm"
              variant="light"
              color="violet"
              aria-label={t('assistant.threads.openWaiting', { title: waitingElsewhere[0].title })}
              onClick={() => setThreadId(waitingElsewhere[0].id)}
            >
              {t('assistant.threads.openShort')}
            </Button>
          </Group>
        </Paper>
      ) : null}

      <Collapse expanded={expanded} keepMounted={false}>
        <Paper
          id={HISTORY_PANEL_ID}
          role="region"
          aria-label={t('assistant.threads.historyRegion')}
          mx="sm"
          mb="sm"
          p="xs"
          withBorder
          radius="md"
        >
          {actionError ? (
            <Alert
              color="red"
              variant="light"
              role="alert"
              mb="xs"
              icon={<IconAlertCircle size={16} />}
            >
              {actionError}
            </Alert>
          ) : null}

          <ThreadListStates status={status} error={error} threads={threads} onRetry={refresh} />

          {status === 'ready' && threads.length > 6 ? (
            <TextInput
              size="sm"
              mb={4}
              placeholder={t('assistant.threads.search')}
              aria-label={t('assistant.threads.search')}
              leftSection={<IconSearch size={14} />}
              value={search}
              onChange={(event) => setSearch(event.currentTarget.value)}
            />
          ) : null}

          {status === 'ready' && threads.length > 0 && visible.length === 0 ? (
            <Text size="sm" c="dimmed" ta="center" py="sm" role="status">
              {t('assistant.threads.noMatch', { query: search.trim() })}
            </Text>
          ) : null}

          {status === 'ready' && visible.length > 0 ? (
            <ScrollArea.Autosize mah={240} type="auto">
              <Stack gap={2}>
                {groups.map((group) => (
                  <Stack key={group.label} gap={2} role="group" aria-label={group.label}>
                    <Text size="xs" fw={600} tt="uppercase" c="dimmed" px={8} pt={8} pb={2} style={{ letterSpacing: '0.04em' }}>
                      {group.label}
                    </Text>
                    {group.threads.map((thread) => (
                      <Fragment key={thread.id}>{renderRow(thread)}</Fragment>
                    ))}
                  </Stack>
                ))}
              </Stack>
            </ScrollArea.Autosize>
          ) : null}
        </Paper>
      </Collapse>
    </Stack>
  );
}

/**
 * The list's loading, empty and error states. Each is a live region (`status` is politely
 * announced; an error interrupts), because every one of them arrives from the network long after
 * the panel the user opened, with nothing else on screen changing to explain it.
 */
function ThreadListStates({
  status,
  error,
  threads,
  onRetry,
}: {
  status: ListStatus;
  error: string | null;
  threads: readonly AssistantThreadSummary[];
  onRetry: () => void;
}) {
  const t = useT();
  if (status === 'loading') {
    return (
      <Group gap={8} role="status" aria-live="polite" wrap="nowrap" px={4} py={6}>
        <Loader size="xs" />
        <Text size="sm" c="dimmed">
          {t('assistant.threads.loading')}
        </Text>
      </Group>
    );
  }

  if (status === 'error') {
    return (
      <Alert
        color="red"
        variant="light"
        role="alert"
        title={t('assistant.threads.unavailable')}
        icon={<IconAlertCircle size={16} />}
      >
        <Stack gap="xs" align="flex-start">
          <Text size="sm">{error}</Text>
          <Button size="md" h={TARGET_SIZE} variant="light" color="red" onClick={onRetry}>
            {t('assistant.threads.tryAgain')}
          </Button>
        </Stack>
      </Alert>
    );
  }

  if (status === 'ready' && threads.length === 0) {
    return (
      <Stack gap={4} align="center" py="md" role="status" aria-live="polite">
        <IconMessages size={28} stroke={1.5} color="var(--mantine-color-dimmed)" aria-hidden />
        <Text size="sm" c="dimmed" ta="center">
          {t('assistant.threads.empty')}
        </Text>
      </Stack>
    );
  }

  return null;
}
