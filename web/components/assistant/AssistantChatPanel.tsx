'use client';

import { ActionIcon, Alert, Button, Group, Loader, Stack, Text, Tooltip, VisuallyHidden } from '@mantine/core';
import {
  IconAlertCircle,
  IconAlertTriangle,
  IconArrowDown,
  IconArrowUp,
  IconBan,
  IconCheck,
  IconCopy,
  IconCircleX,
  IconPlayerStopFilled,
  IconRefresh,
  IconWand,
} from '@tabler/icons-react';
import {
  ActionBarPrimitive,
  ComposerPrimitive,
  ErrorPrimitive,
  MessagePrimitive,
  ThreadPrimitive,
  useAui,
  useAuiState,
} from '@assistant-ui/react';
import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useT } from '@/i18n';
import { enT } from '@/lib/i18n-default';
import { welcomeFor } from './pageInfo';
import type { WelcomeContent } from './pageInfo';
import type { AssistantPendingPrompt } from './AssistantShellProvider';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import { announceImageDocumentChange, parseImageDocumentChange } from '@/lib/editor/assistant-events';
import { MarkdownText } from './MarkdownText';
import { AssistantAttachControl, withoutAttachId } from './AssistantAttachControl';
import { AssistantActionCards } from './AssistantActionCards';
import { AssistantCreditsNotice } from './AssistantCreditsNotice';
import { AssistantTurnTruncatedNotice } from './AssistantTurnTruncatedNotice';
import { turnRefusalCodeOf } from './assistantTurnRefusal';
import { captionStillTrue, reduceAssistantActions } from '@/lib/assistant/actions';
import type { AssistantThreadSetup } from './AssistantDrawer';
import { useAssistantHistory } from './AssistantProvider';
import classes from './AssistantChatPanel.module.css';
import { useLatest } from './useLatest';

interface AssistantChatPanelProps {
  onWorkflowDiff?: (diff: WorkflowEditDiff) => void;
  /** Rendered docked above the composer — the page passes the proposed-change review tray here. */
  review?: ReactNode;
  /**
   * Whether a server thread is bound yet. Optional, and defaulting to ready, because a panel
   * rendered without a thread lifecycle — its own unit tests, and any host that supplies a runtime
   * but no drawer — has no `createThread` to wait on. The shipped stack always passes it, from
   * `AssistantDrawer`, which is the only thing that can hold the shell's thread state.
   */
  thread?: AssistantThreadSetup;
  /** Page-aware greeting + chips. Absent = the workflow-editor welcome (the panel's historic one). */
  welcome?: WelcomeContent;
  /** A prompt a page asked to be shown; prefilled (or sent) once the thread is ready, then consumed. */
  pendingPrompt?: AssistantPendingPrompt | null;
  onPromptConsumed?: (id: number) => void;
  /** Called when a reply finishes streaming (running goes true to false). */
  onTurnSettled?: () => void;
}

const NO_RETRY = () => {};

/** Served before a thread is bound: nothing is being created here, so nothing blocks. */
const READY_THREAD: AssistantThreadSetup = { ready: true, error: null, retry: NO_RETRY };

export const ASSISTANT_SUGGESTIONS = [
  { title: enT('assistant.chat.suggestExplainTitle'), prompt: enT('assistant.chat.suggestExplainPrompt') },
  { title: enT('assistant.chat.suggestReviewTitle'), prompt: enT('assistant.chat.suggestReviewPrompt') },
  { title: enT('assistant.chat.suggestFasterTitle'), prompt: enT('assistant.chat.suggestFasterPrompt') },
  { title: enT('assistant.chat.suggestAgentsTitle'), prompt: enT('assistant.chat.suggestAgentsPrompt') },
] as const;

// Carried by context rather than by an inline `AssistantMessage` wrapper in `components`: an inline
// component is a new type on every render, which remounts every message (and resets its state).
const WorkflowDiffCallbackContext = createContext<((diff: WorkflowEditDiff) => void) | undefined>(undefined);

function WorkflowDiffExtractor() {
  const onWorkflowDiff = useContext(WorkflowDiffCallbackContext);
  const processedMessageIdsRef = useRef(new Set<string>());

  const messageId = useAuiState((s) => {
    return s.message.role === 'assistant' ? s.message.id : null;
  });

  const diffData = useAuiState((s) => {
    if (s.message.role !== 'assistant') {
      return null;
    }

    // Find data-workflowDiff in the unstable_data metadata
    const unstableData = (s.message.metadata.unstable_data || []) as Array<{ name: string; data: unknown }>;
    return unstableData.find((item) => item.name === 'workflowDiff')?.data ?? null;
  });

  useEffect(() => {
    if (!diffData || !onWorkflowDiff || !messageId) return;

    if (processedMessageIdsRef.current.has(messageId)) return;

    processedMessageIdsRef.current.add(messageId);
    onWorkflowDiff(diffData as WorkflowEditDiff);
  }, [messageId, diffData, onWorkflowDiff]);

  return null;
}

/**
 * Announces every picture change the assistant's tools made in this reply (`data-imageDocumentChanged`
 * frames) to whichever picture editor has the document open. The bus de-duplicates by `changeId`
 * across remounts — reopening the drawer re-renders every message — so this can simply announce
 * everything it sees, in order.
 */
function ImageDocumentChangeExtractor() {
  // The store's own array (a stable reference until a frame arrives), never a filtered copy: a
  // selector that builds a new array on every read would never settle.
  const unstableData = useAuiState((s) => (s.message.role === 'assistant' ? (s.message.metadata.unstable_data ?? null) : null));

  useEffect(() => {
    if (!unstableData) return;
    for (const item of unstableData as Array<{ name?: unknown; data?: unknown }>) {
      if (item?.name !== 'imageDocumentChanged') continue;
      const change = parseImageDocumentChange(item.data);
      if (change) announceImageDocumentChange(change);
    }
  }, [unstableData]);

  return null;
}

function AssistantStatusAnnouncer() {
  const t = useT();
  const tRef = useLatest(t);
  const [statusText, setStatusText] = useState<string>('');
  const lastStatusRef = useRef<string | null>(null);

  const statusType = useAuiState((s) => {
    const status = s.message.status;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    return status && typeof status === 'object' ? (status as any).type : null;
  });

  const messageText = useAuiState((s) => {
    return s.message.role === 'assistant' && s.message.content
      ? s.message.content
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          .filter((part: any) => part.type === 'text')
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          .map((part: any) => part.text)
          .join(' ')
      : '';
  });

  useEffect(() => {
    if (!statusType) return;

    // Only update when transitioning AWAY from "running"
    if (lastStatusRef.current === 'running' && statusType !== 'running') {
      setStatusText(tRef.current('assistant.chat.messageCompleted', { text: messageText.substring(0, 100) }));
    }

    lastStatusRef.current = statusType;
  }, [statusType, messageText, tRef]);

  return (
    <VisuallyHidden aria-live="polite" aria-atomic="true">
      {statusText}
    </VisuallyHidden>
  );
}

function AssistantMark({ size = 'sm' }: { size?: 'sm' | 'lg' }) {
  return (
    <span className={classes.mark} data-size={size} aria-hidden>
      <IconWand size={size === 'lg' ? 22 : 14} stroke={2} />
    </span>
  );
}

/**
 * Shown while a reply is in flight and nothing has arrived yet. The backend answers in one piece
 * after its tool calls finish (often 20–60s); meanwhile it streams a caption per tool call, which
 * replaces "Thinking" with what is actually happening, plus an elapsed-time counter. The counter
 * is aria-hidden; each caption is announced as it changes.
 */
function ThinkingIndicator({ caption }: { caption?: string | null }) {
  const t = useT();
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    const start = Date.now();
    const timer = setInterval(() => setSeconds(Math.floor((Date.now() - start) / 1000)), 1000);
    return () => clearInterval(timer);
  }, []);

  return (
    <div className={classes.thinking}>
      <span className={classes.dots} aria-hidden>
        <span />
        <span />
        <span />
      </span>
      {/* Keyed on the caption so each new step fades/slides in instead of swapping silently. */}
      <span role="status" key={caption ?? 'thinking'} className={classes.caption}>
        {caption ? t('assistant.chat.captionEllipsis', { caption }) : t('assistant.chat.thinking')}
      </span>
      {seconds >= 3 && (
        <span className={classes.elapsed} aria-hidden>
          {t('assistant.chat.elapsed', { seconds })}
        </span>
      )}
    </div>
  );
}

function PendingReply() {
  const showThinking = useAuiState((s) => {
    if (s.message.role !== 'assistant' || s.message.status?.type !== 'running') return false;
    return !s.message.content.some((part) => part.type === 'text' && part.text.length > 0);
  });
  // The server streams a plain-language caption (data-toolStatus) each time the assistant starts
  // a tool; the latest one replaces "Thinking" so the user sees what is actually happening.
  const latestCaption = useAuiState((s) => {
    if (s.message.role !== 'assistant') return null;
    const unstableData = (s.message.metadata.unstable_data || []) as Array<{ name: string; data: unknown }>;
    for (let i = unstableData.length - 1; i >= 0; i--) {
      const item = unstableData[i];
      if (item.name === 'toolStatus') {
        const text = (item.data as { caption?: unknown } | null)?.caption;
        if (typeof text === 'string' && text.length > 0) return text;
      }
    }
    return null;
  });
  // The runtime's own array — stable until a frame arrives — read to know whether a request still waits.
  const unstableData = useAuiState((s) => (s.message.role === 'assistant' ? (s.message.metadata.unstable_data ?? null) : null));
  const actions = useMemo(() => reduceAssistantActions(unstableData), [unstableData]);
  const caption = latestCaption !== null && captionStillTrue(latestCaption, actions) ? latestCaption : null;
  return showThinking ? <ThinkingIndicator caption={caption} /> : null;
}

/**
 * The heading of a reply's error state. A turn the server refused before starting (too many
 * assistant requests in this workspace) says so plainly; the server's own sentence follows it, as
 * `ErrorPrimitive.Message`. Anything else is the generic failure.
 */
function ReplyErrorHeading() {
  const t = useT();
  const refused = useAuiState((s) => {
    const status = s.message.status;
    if (status?.type !== 'incomplete' || status.reason !== 'error') return false;
    return turnRefusalCodeOf(status.error) !== null;
  });
  return refused ? <strong>{t('assistant.chat.busy')}</strong> : <strong>{t('assistant.chat.couldntReply')}</strong>;
}

function AssistantMessage() {
  const t = useT();
  return (
    <>
      <WorkflowDiffExtractor />
      <ImageDocumentChangeExtractor />
      <AssistantStatusAnnouncer />
      <MessagePrimitive.Root className={classes.assistantRow} data-role="assistant">
        <AssistantMark />
        <div className={classes.assistantBody}>
          <VisuallyHidden>{t('assistant.chat.assistantSaid')}</VisuallyHidden>
          <MessagePrimitive.Parts components={{ Text: MarkdownText }} />
          {/* A turn refused for lack of credits still answers in words; this says how to fix it. */}
          <AssistantCreditsNotice />
          {/* A re-attached reply that is only a PARTIAL replay says so, rather than passing a shorter
              answer off as the whole one (the hub's `data-turnTruncated` frame, first on a capped log). */}
          <AssistantTurnTruncatedNotice />
          {/* Below any text, above the thinking indicator: the reply replies in one piece at the
              end of its turn, so a request for permission arrives while there is no text yet. */}
          <AssistantActionCards />
          <PendingReply />
          <MessagePrimitive.Error>
            <ErrorPrimitive.Root className={classes.error} role="alert">
              <IconAlertTriangle size={16} className={classes.errorIcon} aria-hidden />
              <div className={classes.errorText}>
                <ReplyErrorHeading />
                <ErrorPrimitive.Message className={classes.errorDetail} />
              </div>
              <ActionBarPrimitive.Reload asChild>
                <Button size="compact-sm" variant="default" leftSection={<IconRefresh size={14} />}>
                  {t('assistant.chat.tryAgain')}
                </Button>
              </ActionBarPrimitive.Reload>
            </ErrorPrimitive.Root>
          </MessagePrimitive.Error>
          <ActionBarPrimitive.Root hideWhenRunning autohide="not-last" className={classes.actionBar}>
            <ActionBarPrimitive.Copy asChild>
              <ActionIcon variant="subtle" color="gray" size="md" aria-label={t('assistant.chat.copyReply')}>
                <MessagePrimitive.If copied>
                  <IconCheck size={15} />
                </MessagePrimitive.If>
                <MessagePrimitive.If copied={false}>
                  <IconCopy size={15} />
                </MessagePrimitive.If>
              </ActionIcon>
            </ActionBarPrimitive.Copy>
            <Tooltip label={t('assistant.chat.askAgain')} withArrow>
              <ActionBarPrimitive.Reload asChild>
                <ActionIcon variant="subtle" color="gray" size="md" aria-label={t('assistant.chat.regenerate')}>
                  <IconRefresh size={15} />
                </ActionIcon>
              </ActionBarPrimitive.Reload>
            </Tooltip>
          </ActionBarPrimitive.Root>
        </div>
      </MessagePrimitive.Root>
    </>
  );
}

/** A user's own words, shown as they wrote them — except an attach message's file id, which is the assistant's. */
function UserText({ text }: { text: string }) {
  return <p style={{ whiteSpace: 'pre-line' }}>{withoutAttachId(text)}</p>;
}

type RunWake = { status: 'Passed' | 'Failed' | 'Cancelled'; runLabel: string };

function readRunWake(custom: Record<string, unknown> | undefined): RunWake | null {
  const wake = custom?.runWake as Partial<RunWake> | undefined;
  if (!wake || typeof wake.runLabel !== 'string') return null;
  const status = wake.status === 'Failed' || wake.status === 'Cancelled' ? wake.status : 'Passed';
  return { status, runLabel: wake.runLabel };
}

/** A run the assistant was waiting on finished: a small neutral event row, never a user bubble. */
function RunWakeRow({ wake }: { wake: RunWake }) {
  const t = useT();
  const Icon = wake.status === 'Passed' ? IconCheck : wake.status === 'Failed' ? IconCircleX : IconBan;
  return (
    <MessagePrimitive.Root className={classes.eventRow} data-role="run-wake">
      <Text size="xs" c="dimmed" component="div" className={classes.eventText} role="status">
        <Icon size={14} aria-hidden="true" />
        <span>
          {t('assistant.chat.runFinished', { label: wake.runLabel, status: wake.status })}
        </span>
      </Text>
    </MessagePrimitive.Root>
  );
}

function UserMessage() {
  const t = useT();
  const custom = useAuiState((s) => s.message.metadata.custom);
  const wake = useMemo(() => readRunWake(custom), [custom]);
  if (wake) return <RunWakeRow wake={wake} />;
  return (
    <MessagePrimitive.Root className={classes.userRow} data-role="user">
      <VisuallyHidden>{t('assistant.chat.youSaid')}</VisuallyHidden>
      <div className={classes.userBubble}>
        <MessagePrimitive.Parts components={{ Text: UserText }} />
      </div>
    </MessagePrimitive.Root>
  );
}

function EmptyState({ ready, welcome }: { ready: boolean; welcome: WelcomeContent }) {
  return (
    <div className={classes.empty}>
      <AssistantMark size="lg" />
      <h3 className={classes.emptyTitle}>{welcome.heading}</h3>
      <p className={classes.emptyCopy}>{welcome.copy}</p>
      <div className={classes.suggestions}>
        {welcome.suggestions.map((s) => (
          // `send` makes a chip a send path of its own, so it is gated exactly like the Send button:
          // the empty state IS the pre-thread window, which makes these the likeliest way into it.
          <ThreadPrimitive.Suggestion key={s.title} prompt={s.prompt} send disabled={!ready} asChild>
            <button type="button" className={classes.suggestion}>
              <span className={classes.suggestionTitle}>{s.title}</span>
              <span className={classes.suggestionPrompt}>{s.prompt}</span>
            </button>
          </ThreadPrimitive.Suggestion>
        ))}
      </div>
    </div>
  );
}

/**
 * What the composer says while no server thread is bound: that one is being created, or that it
 * could not be and can be asked for again. Docked directly above the composer — the thing it is
 * explaining — rather than at the top of the drawer, and a live region in both states, since the
 * outcome arrives after the drawer is already on screen.
 */
function ThreadSetupNotice({ thread }: { thread: AssistantThreadSetup }) {
  const t = useT();
  if (thread.ready) return null;

  if (thread.error !== null) {
    return (
      <Alert
        color="red"
        variant="light"
        role="alert"
        data-testid="assistant-thread-error"
        title={t('assistant.chat.startFailedTitle')}
        icon={<IconAlertCircle size={16} />}
      >
        <Stack gap="xs" align="flex-start">
          <Text size="sm">{thread.error}</Text>
          <Button size="compact-sm" variant="light" color="red" onClick={thread.retry}>
            {t('assistant.chat.tryAgain')}
          </Button>
        </Stack>
      </Alert>
    );
  }

  return (
    <Group
      gap={8}
      role="status"
      aria-live="polite"
      data-testid="assistant-thread-pending"
      className={classes.threadNotice}
    >
      <Loader size="xs" />
      <Text size="sm" c="dimmed">
        {t('assistant.chat.starting')}
      </Text>
    </Group>
  );
}

/**
 * What the panel says about a restored conversation whose newest message is the user's and has no
 * reply. Two different truths look the same in the stored turns, and the server tells them apart
 * (`replyInProgress`): the reply is still being worked on — it is saved to this conversation even
 * if the panel is closed — or the request died and nothing is coming. Before this, such a thread
 * just sat there, and the customer could not tell a slow answer from a lost one.
 *
 * "Ask again" re-runs the turn on the SAME user message (a run whose parent is that message), and
 * the server recognises the unanswered message instead of storing the question twice.
 */
function UnansweredNotice() {
  const t = useT();
  const history = useAssistantHistory();
  const aui = useAui();
  const running = useAuiState((s) => s.thread.isRunning);
  const lastUserId = useAuiState((s) => {
    const messages = s.thread.messages;
    const last = messages[messages.length - 1];
    return last && last.role === 'user' ? last.id : null;
  });

  if (running || lastUserId === null || history.status !== 'ready') return null;

  if (history.replyInProgress) {
    return (
      <Group
        gap={8}
        role="status"
        aria-live="polite"
        data-testid="assistant-reply-in-progress"
        className={classes.threadNotice}
        wrap="nowrap"
      >
        <Loader size="xs" />
        <Text size="sm" c="dimmed" style={{ flex: 1 }}>
          {t('assistant.chat.stillWorking')}
        </Text>
        <Button size="compact-sm" variant="light" onClick={history.reload}>
          {t('assistant.chat.checkAgain')}
        </Button>
      </Group>
    );
  }

  return (
    <Alert
      color="yellow"
      variant="light"
      role="status"
      data-testid="assistant-no-reply"
      title={t('assistant.chat.noReplyTitle')}
      icon={<IconAlertTriangle size={16} />}
    >
      <Stack gap="xs" align="flex-start">
        <Text size="sm">
          {t('assistant.chat.noReplyBody')}
        </Text>
        <Button
          size="compact-sm"
          variant="light"
          color="yellow"
          leftSection={<IconRefresh size={14} />}
          onClick={() => aui.thread().startRun({ parentId: lastUserId })}
        >
          {t('assistant.chat.askAgain')}
        </Button>
      </Stack>
    </Alert>
  );
}

/** Says the assistant will come back by itself once the run(s) it is waiting on finish. */
function PendingWatchNotice() {
  const t = useT();
  const { pendingWatches, pendingWatchLabel } = useAssistantHistory();
  if (pendingWatches <= 0) return null;
  const text =
    pendingWatches === 1
      ? pendingWatchLabel
        ? t('assistant.chat.pickUpNamed', { label: pendingWatchLabel })
        : t('assistant.chat.pickUpRun')
      : t('assistant.chat.pickUpRuns', { count: pendingWatches });
  return (
    <Text size="xs" c="dimmed" role="status" data-testid="assistant-pending-watch">
      {text}
    </Text>
  );
}

function Composer({ thread }: { thread: AssistantThreadSetup }) {
  const t = useT();
  const { ready } = thread;

  return (
    // `onSubmit` is handled BEFORE the primitive's own: `ComposerPrimitive.Root` answers a submit
    // with `send()`, and radix's composed handler skips that when an earlier handler called
    // `preventDefault`. The primitive's keyboard-submit path reaches this form only through the
    // input — which is disabled above, and whose own keydown handler bails out before it can call
    // `requestSubmit()` — so this is a SECOND lock on the same door rather than the first. It is
    // here because that door is one a merely-greyed button would leave open, and the composer is
    // the only place it can be closed.
    <ComposerPrimitive.Root
      className={classes.composer}
      onSubmit={(event) => {
        if (!ready) event.preventDefault();
      }}
    >
      <ComposerPrimitive.Input
        className={classes.input}
        // Disabled, not merely greyed: the primitive's own Enter handler returns early on a
        // disabled input, so this closes the keyboard-submit path at its source rather than hiding
        // the outcome of it.
        disabled={!ready}
        placeholder={ready ? t('assistant.chat.placeholderReady') : t('assistant.chat.placeholderWaiting')}
        aria-label={t('assistant.chat.composerLabel')}
        rows={1}
        maxRows={8}
        submitMode="enter"
      />
      <div className={classes.composerBar}>
        <span className={classes.hint} aria-hidden>
          {t.rich('assistant.chat.hint', { enter: (chunks) => <kbd>{chunks}</kbd>, shift: (chunks) => <kbd>{chunks}</kbd> })}
        </span>
        {/* Beside the send button, and part of the same bar: an attachment is a way of composing a
            turn, not a separate action. It SENDS that turn once the upload lands, so it is a send
            path like the button and the chips, and is gated with them. */}
        <AssistantAttachControl disabled={!ready} />
        <ThreadPrimitive.If running={false}>
          <ComposerPrimitive.Send disabled={!ready} asChild>
            <ActionIcon variant="filled" color="violet" radius="xl" size="lg" aria-label={t('assistant.chat.send')} className={classes.send}>
              <IconArrowUp size={18} stroke={2.25} />
            </ActionIcon>
          </ComposerPrimitive.Send>
        </ThreadPrimitive.If>
        <ThreadPrimitive.If running>
          <ComposerPrimitive.Cancel asChild>
            <ActionIcon variant="default" radius="xl" size="lg" aria-label={t('assistant.chat.stop')}>
              <IconPlayerStopFilled size={14} />
            </ActionIcon>
          </ComposerPrimitive.Cancel>
        </ThreadPrimitive.If>
      </div>
    </ComposerPrimitive.Root>
  );
}

/**
 * Delivers a page's `openWithPrompt` request into the composer once a thread is bound and no turn
 * is running. Prefill by default; sends only when the caller asked for it.
 */
function PendingPromptDelivery({
  pending,
  ready,
  onConsumed,
}: {
  pending: AssistantPendingPrompt | null | undefined;
  ready: boolean;
  onConsumed?: (id: number) => void;
}) {
  const aui = useAui();
  const running = useAuiState((s) => s.thread.isRunning);

  useEffect(() => {
    if (!pending || !ready || running) return;
    aui.composer.setText(pending.prompt);
    if (pending.send) aui.composer.send();
    onConsumed?.(pending.id);
  }, [pending, ready, running, aui, onConsumed]);

  return null;
}

function TurnSettledNotifier({ onSettled }: { onSettled?: () => void }) {
  const running = useAuiState((s) => s.thread.isRunning);
  const wasRunning = useRef(false);
  useEffect(() => {
    if (wasRunning.current && !running) onSettled?.();
    wasRunning.current = running;
  }, [running, onSettled]);
  return null;
}

export function AssistantChatPanel({
  onWorkflowDiff,
  review,
  thread = READY_THREAD,
  welcome: welcomeProp,
  pendingPrompt,
  onPromptConsumed,
  onTurnSettled,
}: AssistantChatPanelProps) {
  const t = useT();
  const welcome = welcomeProp ?? welcomeFor('workflow', t);
  return (
    <WorkflowDiffCallbackContext.Provider value={onWorkflowDiff}>
      <ThreadPrimitive.Root className={classes.root}>
        <TurnSettledNotifier onSettled={onTurnSettled} />
        <PendingPromptDelivery pending={pendingPrompt} ready={thread.ready} onConsumed={onPromptConsumed} />
        <ThreadPrimitive.Viewport className={classes.viewport}>
          {/* The thread itself: only messages (and the empty state) live under this test id. */}
          <div className={classes.thread} data-testid="assistant-thread" role="region" aria-label={t('assistant.chat.conversation')}>
            <ThreadPrimitive.Empty>
              <EmptyState ready={thread.ready} welcome={welcome} />
            </ThreadPrimitive.Empty>
            <ThreadPrimitive.Messages components={{ UserMessage, AssistantMessage }} />
          </div>

          <ThreadPrimitive.ViewportFooter className={classes.footer}>
            <ThreadPrimitive.ScrollToBottom asChild>
              <ActionIcon
                variant="default"
                radius="xl"
                size="lg"
                aria-label={t('assistant.chat.scrollLatest')}
                className={classes.scrollToBottom}
              >
                <IconArrowDown size={16} />
              </ActionIcon>
            </ThreadPrimitive.ScrollToBottom>
            {review}
            <UnansweredNotice />
            <PendingWatchNotice />
            <ThreadSetupNotice thread={thread} />
            <Composer thread={thread} />
          </ThreadPrimitive.ViewportFooter>
        </ThreadPrimitive.Viewport>
      </ThreadPrimitive.Root>
    </WorkflowDiffCallbackContext.Provider>
  );
}
