'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { Button, Collapse, Loader, VisuallyHidden } from '@mantine/core';
import {
  IconAlertTriangle,
  IconCheck,
  IconChevronDown,
  IconCoin,
  IconFile,
  IconFolder,
  IconHandStop,
  IconInfoCircle,
  IconMinus,
  IconPalette,
  IconPencil,
  IconPhoto,
  IconPlayerPlay,
  IconPlugConnected,
  IconPlus,
  IconRobot,
  IconRoute,
  IconWand,
  IconX,
  IconClock,
  type TablerIcon,
} from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { respondToApproval } from '@/lib/api/assistant';
import { actionTitle, settledLine } from '@/lib/assistant/actions';
import { useFormat, useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import type { Format } from '@/i18n/format';
import { enT } from '@/lib/i18n-default';
import type {
  AssistantAction,
  AssistantActionChange,
  AssistantActionKind,
  AssistantApprovalDecision,
} from '@/lib/types/assistant';
import { AutoModeConfirmModal } from './AutoModeConfirmModal';
import classes from './ActionCard.module.css';
import { useLatest } from './useLatest';

/** What a card says when this screen lost the stream before the person answered it. */
export const CONNECTION_LOST_LINE = enT('assistant.card.connectionLost');

/** What a card says when the person pressed Stop while it was waiting. */
export const STOPPED_LINE = enT('assistant.card.stopped');

export interface ActionCardProps {
  action: AssistantAction;
  /**
   * The reply this request belongs to has stopped on this screen (the person pressed Stop, or the
   * connection went). A request left `pending` is shown as one this screen lost, NOT as one that ran
   * out of time: the server keeps the card open after a dropped connection, and the assistant brings
   * it back when the screen reconnects (`AssistantResumeOnReturn`).
   */
  abandoned?: boolean;
  /** The person pressed Stop (so a waiting card was declined), not a lost connection. */
  stopped?: boolean;
  /** How an answer is sent. Defaults to the real endpoint; a seam for the tests. */
  respond?: (id: string, decision: AssistantApprovalDecision, options?: { alwaysAllow?: boolean }) => Promise<void>;
  /** Called after "Always allow" went through: the server is now in auto mode. */
  onAlwaysAllowed?: () => void;
}

const KIND_ICON: Record<AssistantActionKind, TablerIcon> = {
  workflow: IconRoute,
  project: IconFolder,
  file: IconFile,
  style: IconPalette,
  picture: IconPhoto,
  run: IconPlayerPlay,
  connection: IconPlugConnected,
  agent: IconRobot,
  other: IconWand,
};

const CHANGE_ICON: Record<AssistantActionChange['kind'], TablerIcon> = {
  add: IconPlus,
  remove: IconMinus,
  change: IconPencil,
  info: IconInfoCircle,
};

const CHANGE_WORD: Record<AssistantActionChange['kind'], string> = {
  add: 'assistant.card.change.add',
  remove: 'assistant.card.change.remove',
  change: 'assistant.card.change.change',
  info: 'assistant.card.change.info',
};

/** What the person is waiting on after pressing a button. `always` is Allow + switch to auto mode. */
type Pressed = 'allow' | 'deny' | 'always';

/** Where an answer stands. `answered` is accepted by the server; its next frame is still on its way. */
type Answer =
  | { phase: 'idle' }
  | { phase: 'sending'; pressed: Pressed }
  | { phase: 'answered'; pressed: Pressed }
  | { phase: 'gone' }
  | { phase: 'retry' };

function ChangeList({ changes, label }: { changes: AssistantActionChange[]; label: string }) {
  const t = useT();
  const tRef = useLatest(t);
  if (changes.length === 0) return null;
  return (
    <ul className={classes.changes} aria-label={label}>
      {changes.map((change, index) => {
        const Icon = CHANGE_ICON[change.kind];
        return (
          <li key={index} className={classes.change}>
            <span className={classes.tag} data-kind={change.kind}>
              <Icon size={12} stroke={2.5} aria-hidden />
              {t(CHANGE_WORD[change.kind])}
            </span>
            <span className={classes.changeText}>{change.text}</span>
          </li>
        );
      })}
    </ul>
  );
}

function creditsFigure(f: Format, n: number): string {
  return f.number(Math.ceil(n * 10) / 10, { maximumFractionDigits: 1 });
}

/** Why a card showed up although auto mode is on. */
function autoNoteLine(t: T, f: Format, note: NonNullable<AssistantAction['autoNote']>): string {
  const limit = creditsFigure(f, note.limitCredits);
  return note.costCredits === undefined
    ? t('assistant.card.autoNoteUnknown', { limit })
    : t('assistant.card.autoNoteCost', { cost: creditsFigure(f, note.costCredits), limit });
}

/**
 * The cost line, in the server's own words for the amount. A plain amount ("about 12 credits",
 * "about 5 credits per picture; …") reads "This will use about 12 credits."; a sentence that also
 * says what a run needs to start ("usually 8–12 credits (needs 15 available to start; can cost
 * more)") is shown as it is, capitalised, so the figure a person agrees to is never reworded.
 */
function costLine(t: T, action: AssistantAction): string | null {
  if (action.cost) {
    const cost = action.cost.trim().replace(/[.\s]+$/, '');
    if (/^(about|up to)\b/i.test(cost) && !/\(needs\b/i.test(cost)) return t('assistant.card.costWillUse', { cost });
    return `${cost.charAt(0).toUpperCase()}${cost.slice(1)}.`;
  }
  return action.risk === 'paid' ? t('assistant.card.costWillUseCredits') : null;
}

/**
 * One permission request from the assistant, as a card the person answers.
 *
 * While `pending` it is the question: what the assistant wants to do, in which project, what will
 * change, what it costs or that it cannot be undone, and the buttons. Once it is answered it
 * collapses to one line saying what became of it, which expands to show what was asked.
 *
 * The same element carries it all the way through, so the place the question was asked is the place
 * the answer appears, and focus — which only moves if the person was using the card — has somewhere
 * to land when the buttons go.
 */
export function ActionCard({ action, abandoned = false, stopped = false, respond = respondToApproval, onAlwaysAllowed }: ActionCardProps) {
  const t = useT();
  const tRef = useLatest(t);
  const f = useFormat();
  const title = actionTitle(action, t);
  const titleId = useId();
  const detailsId = useId();
  const rootRef = useRef<HTMLDivElement>(null);
  // Two clicks in the same tick both see the state as idle; the ref is the real guard.
  const sendingRef = useRef(false);
  // Set when the person pressed something here, so focus follows the card when its buttons are gone.
  const usedRef = useRef(false);

  const [answer, setAnswer] = useState<Answer>({ phase: 'idle' });
  const [confirming, setConfirming] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const [announcement, setAnnouncement] = useState('');

  // A request this screen lost the stream of says the connection dropped (never "timed out": the server
  // still holds the card, and reconnecting shows it again); a request that was let through and then lost
  // its connection says so rather than spinning for ever.
  const interruptedWhileWorking = abandoned && action.state === 'allowed';
  const connectionLost = abandoned && action.state === 'pending';
  const state = abandoned && action.state === 'pending' ? 'expired' : action.state;
  const pending = state === 'pending';

  // A live region only announces text that CHANGES after it is in the DOM, so it mounts empty and is
  // written a tick later — once, when a question appears. The person is never moved to the card.
  useEffect(() => {
    if (!pending) return;
    const timer = setTimeout(() => setAnnouncement(tRef.current('assistant.card.permissionNeeded', { title })), 150);
    return () => clearTimeout(timer);
  }, [pending, title, tRef]);

  useEffect(() => {
    if (pending || !usedRef.current) return;
    usedRef.current = false;
    rootRef.current?.focus({ preventScroll: true });
  }, [pending]);

  const send = async (decision: AssistantApprovalDecision, choice: Pressed) => {
    if (sendingRef.current) return;
    sendingRef.current = true;
    usedRef.current = true;
    setAnswer({ phase: 'sending', pressed: choice });
    try {
      await respond(action.id, decision, choice === 'always' ? { alwaysAllow: true } : undefined);
      setAnswer({ phase: 'answered', pressed: choice });
      if (choice === 'always') onAlwaysAllowed?.();
    } catch (error) {
      // 404: unknown, someone else's or finished. 409: already decided. Either way nothing is left
      // to answer, and no amount of pressing will change that.
      const gone = error instanceof ApiError && (error.status === 404 || error.status === 409);
      setAnswer({ phase: gone ? 'gone' : 'retry' });
    } finally {
      sendingRef.current = false;
    }
  };

  // What the person pressed, while that answer is on its way or accepted and not yet echoed back.
  const pressed = answer.phase === 'sending' || answer.phase === 'answered' ? answer.pressed : null;
  // Nothing can be pressed while an answer is in flight or accepted, nor once the request is gone.
  const busy = pressed !== null || answer.phase === 'gone';
  const KindIcon = KIND_ICON[action.kind];
  const destructive = action.risk === 'destructive';
  const paid = action.risk === 'paid';
  const cost = costLine(t, action);

  const pendingBody = (
    <>
      <div className={classes.head}>
        <span className={classes.kindIcon} aria-hidden>
          <KindIcon size={16} />
        </span>
        <div className={classes.headText}>
          <span className={classes.eyebrow}>{t('assistant.card.needsOk')}</span>
          <h4 id={titleId} className={classes.title}>
            {title}
          </h4>
          {action.projectName && <span className={classes.project}>{t('assistant.card.inProject', { name: action.projectName })}</span>}
        </div>
      </div>

      {action.summary && <p className={classes.summary}>{action.summary}</p>}
      <ChangeList changes={action.changes} label={t('assistant.card.whatWillChange')} />

      {cost && (
        <p className={classes.note} data-tone="cost">
          <IconCoin size={16} aria-hidden />
          <span>{cost}</span>
        </p>
      )}
      {action.autoNote && (
        <p className={classes.note} data-tone="cost" data-testid="auto-note">
          <IconCoin size={16} aria-hidden />
          <span>{autoNoteLine(t, f, action.autoNote)}</span>
        </p>
      )}
      {destructive && (
        <p className={classes.note} data-tone="danger">
          <IconAlertTriangle size={16} aria-hidden />
          <span>{t('assistant.card.cantUndo')}</span>
        </p>
      )}

      {answer.phase === 'gone' && (
        <p className={classes.problem} role="alert">
          {t('assistant.card.gone')}
        </p>
      )}
      {answer.phase === 'retry' && (
        <p className={classes.problem} role="alert">
          {t('assistant.card.retry')}
        </p>
      )}

      <div className={classes.actions}>
        <Button
          className={classes.button}
          // red.8, not the default red: white on the default shade is under 4.5:1.
          color={destructive ? 'red.8' : undefined}
          leftSection={<IconCheck size={16} />}
          loading={pressed === 'allow' || pressed === 'always'}
          disabled={busy}
          onClick={() => void send('allow', 'allow')}
        >
          {paid ? t('assistant.card.allowPaid') : t('assistant.card.allow')}
        </Button>
        <Button
          className={classes.button}
          variant="default"
          leftSection={<IconX size={16} />}
          loading={pressed === 'deny'}
          disabled={busy}
          onClick={() => void send('deny', 'deny')}
        >
          {t('assistant.card.deny')}
        </Button>
      </div>
      <Button
        className={classes.link}
        variant="subtle"
        color="gray"
        size="compact-sm"
        disabled={busy}
        onClick={() => setConfirming(true)}
      >
        {t('assistant.card.alwaysAllow')}
      </Button>

      <AutoModeConfirmModal
        opened={confirming}
        onCancel={() => setConfirming(false)}
        onConfirm={() => {
          setConfirming(false);
          void send('allow', 'always');
        }}
      />
    </>
  );

  if (pending) {
    return (
      <div
        ref={rootRef}
        tabIndex={-1}
        className={classes.root}
        role="group"
        aria-labelledby={titleId}
        data-state="pending"
        data-risk={action.risk}
        data-mode={action.mode}
        data-action-id={action.id}
        data-testid="assistant-action"
      >
        <VisuallyHidden aria-live="polite" aria-atomic="true">
          {announcement}
        </VisuallyHidden>
        {pendingBody}
      </div>
    );
  }

  // ---- Settled: one line, expandable to what was asked. ----
  const line = interruptedWhileWorking
    ? t('assistant.card.interrupted')
    : connectionLost
      ? stopped
        ? t('assistant.card.stopped')
        : t('assistant.card.connectionLost')
      : settledLine({ ...action, state }, t);
  // A finished request with no outcome of its own reads "Done: <title>", which already says what it
  // was. Every other line — "Saved.", still working, declined, out of time, failed — would leave the
  // person asking "this what?", so the title goes under it.
  const showTitle = state !== 'done' || Boolean(action.outcome);
  const details = Boolean(action.summary) || action.changes.length > 0 || Boolean(action.projectName);

  const StateIcon: TablerIcon =
    state === 'done'
      ? IconCheck
      : state === 'failed' || interruptedWhileWorking
        ? IconAlertTriangle
        : state === 'declined'
          ? IconHandStop
          : IconClock;

  const lineBody = (
    <>
      <span className={classes.stateIcon} aria-hidden>
        {state === 'allowed' && !interruptedWhileWorking ? <Loader size="xs" /> : <StateIcon size={16} aria-hidden />}
      </span>
      <span className={classes.lineText}>
        <span className={classes.lineMain}>{line}</span>
        {showTitle && <span className={classes.lineTitle}>{title}</span>}
      </span>
      {state === 'done' && action.mode === 'auto' && <span className={classes.auto}>{t('assistant.card.doneAuto')}</span>}
      {details && (
        <IconChevronDown size={16} className={classes.chevron} data-expanded={expanded || undefined} aria-hidden />
      )}
    </>
  );

  return (
    <div
      ref={rootRef}
      tabIndex={-1}
      className={classes.root}
      data-state={state}
      data-risk={action.risk}
      data-mode={action.mode}
      data-action-id={action.id}
      data-testid="assistant-action"
    >
      {details ? (
        <button
          type="button"
          className={classes.line}
          aria-expanded={expanded}
          aria-controls={detailsId}
          onClick={() => setExpanded((open) => !open)}
        >
          {lineBody}
          <VisuallyHidden>{expanded ? t('assistant.card.hideAsked') : t('assistant.card.showAsked')}</VisuallyHidden>
        </button>
      ) : (
        <div className={classes.line}>{lineBody}</div>
      )}
      {details && (
        <Collapse expanded={expanded} id={detailsId} transitionDuration={150}>
          <div className={classes.details}>
            <p className={classes.detailsTitle}>{title}</p>
            {action.projectName && <span className={classes.project}>{t('assistant.card.inProject', { name: action.projectName })}</span>}
            {action.summary && <p className={classes.summary}>{action.summary}</p>}
            <ChangeList changes={action.changes} label={t('assistant.card.whatAsked')} />
          </div>
        </Collapse>
      )}
    </div>
  );
}
