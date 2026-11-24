'use client';

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import type { ReactNode } from 'react';
import { ActionIcon, Alert, Button, Drawer, Group, Loader, Popover, Stack, Switch, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useT } from '@/i18n';
import { useIsPhoneClass } from '@/lib/editing/use-device-class';
import { useKeyboardInset } from '@/components/editor/phone/use-keyboard-inset';
import { IconAlertCircle, IconAlertTriangle, IconSettings, IconWand } from '@tabler/icons-react';
import type { WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import { useAssistantPreferences } from '@/lib/hooks/use-assistant-preferences';
import { useAssistantDialogOpen } from '@/lib/assistant/dialog-guard';
import {
  enableNotifications,
  getNotifySwitch,
  isNotificationSupported,
  notificationPermission,
  setNotifySwitch,
  subscribeNotifySwitch,
} from '@/lib/assistant/browser-notify';
import { AssistantChatPanel } from './AssistantChatPanel';
import { AutoModeConfirmModal } from './AutoModeConfirmModal';
import { useAssistantHistory } from './AssistantProvider';
import type { AssistantThreadSetup } from './AssistantDrawer';
import type { AssistantPendingPrompt } from './AssistantShellProvider';
import type { WelcomeContent } from './pageInfo';
import type { NamedStep } from './diff-model';
import classes from './AssistantDrawer.module.css';
import { useLatest } from './useLatest';

export interface AssistantDrawerShellProps {
  opened: boolean;
  onClose: () => void;
  /** Whether a server thread is bound yet, and its failure's retry. Owned by the drawer, which is
   * the only component that can reach the shell's thread state — forwarded to the composer, which
   * is where every send path actually is. Optional so this shell stays renderable on its own;
   * absent means "not managed here", which is the standalone case. */
  thread?: AssistantThreadSetup;
  /** The page's own proposed-change review tray, docked above the composer. */
  review?: ReactNode;
  onWorkflowDiff?: (diff: WorkflowEditDiff) => void;
  /** The steps the page drew. The registered `review` node already binds them, so the shell only
   * carries it so that everything one page registered arrives at the chrome in one place. */
  currentSteps?: readonly NamedStep[];
  /** The conversation-history chrome, docked above the thread. Built by the drawer — the only
   * component that can reach the shell's thread state — and optional, so this shell stays
   * renderable on its own. */
  history?: ReactNode;
  welcome?: WelcomeContent;
  pendingPrompt?: AssistantPendingPrompt | null;
  onPromptConsumed?: (id: number) => void;
  onTurnSettled?: () => void;
}

/**
 * The assistant's chrome, and nothing else: one drawer whose shape follows the viewport. It knows
 * no page, no route and no shell context — everything it draws arrives as a prop, which is what
 * keeps it unit-testable without a router.
 *
 * The runtime provider sits OUTSIDE this Drawer on purpose: Mantine unmounts drawer content on
 * close, and a provider inside it threw the whole conversation away every time the drawer was
 * closed to look at the canvas — exactly when a user wants to compare a proposal with the graph.
 *
 * That same unmount remounts every message on reopen, and each message's diff extractor re-emits
 * its proposal. The set of confirmation tokens already handed to the page therefore lives here, for
 * the lifetime of the thread, and a token is forwarded at most once: a proposal that was applied,
 * discarded or superseded by a newer one never comes back. A still-pending proposal survives
 * close/reopen on its own, because the page holds it in state.
 */
export function AssistantDrawerShell({
  opened,
  onClose,
  thread,
  review,
  onWorkflowDiff,
  history,
  welcome,
  pendingPrompt,
  onPromptConsumed,
  onTurnSettled,
}: AssistantDrawerShellProps) {
  const t = useT();
  const tRef = useLatest(t);
  // One device rule everywhere (W3-D1). On a phone the assistant is a FULL-height sheet (the brief of
  // assistant-drawer-mobile overrides the earlier half-height decision): a readable thread beats a
  // sliver of canvas; the composer lifts by the soft keyboard's height.
  const isPhone = useIsPhoneClass();
  const keyboardInset = useKeyboardInset(isPhone && opened);
  const dialogOpen = useAssistantDialogOpen();
  // C3: from 1024px the assistant is a docked, NON-modal panel: no overlay, no focus trap, no scroll
  // lock, and a click outside does not close it, so the sidebar and the page stay usable beside it.
  const docked = useMediaQuery('(min-width: 64em)', false) === true && !isPhone;
  const seenTokensRef = useRef<Set<string> | null>(null);

  const handleWorkflowDiff = useCallback(
    (diff: WorkflowEditDiff) => {
      seenTokensRef.current ??= new Set<string>();
      const seen = seenTokensRef.current;
      if (seen.has(diff.confirmationToken)) return;
      seen.add(diff.confirmationToken);
      onWorkflowDiff?.(diff);
    },
    [onWorkflowDiff],
  );

  // C3: Mantine marks every drawer aria-modal and only manages focus with its trap, both wrong for
  // a docked non-modal panel: say it is not modal, move focus in on open, give it back on close.
  // (Escape is Mantine's own handler.) Depends on opened/docked only, so a confirm dialog opening
  // from inside the panel does not move focus around.
  useEffect(() => {
    if (!docked || !opened) return;
    const trigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const raf = requestAnimationFrame(() => {
      const dialog = document.querySelector<HTMLElement>('[data-testid="assistant-drawer"] [role="dialog"]');
      dialog?.setAttribute('aria-modal', 'false');
      const target = dialog?.querySelector<HTMLElement>(`[aria-label=${JSON.stringify(tRef.current('assistant.drawer.closePanel'))}]`) ?? dialog;
      target?.focus();
    });
    return () => {
      cancelAnimationFrame(raf);
      if (trigger?.isConnected) trigger.focus();
    };
  }, [docked, opened, tRef]);

  // Phone sheet: Mantine's own return-focus lands on <body> here (the trigger sits in the header the sheet
  // now covers), so remember the opener and give focus back to it on close — the phone editor's BottomSheet
  // does the same. `preventScroll` because the header trigger is already in view and the page must not jump.
  useEffect(() => {
    if (!isPhone || !opened) return;
    const trigger = document.activeElement instanceof HTMLElement && document.activeElement !== document.body ? document.activeElement : null;
    return () => {
      if (trigger?.isConnected) trigger.focus({ preventScroll: true });
    };
  }, [isPhone, opened]);

  // Docked: Mantine's Escape handler is window-wide, so Escape anywhere on the page would close the panel.
  // Only close when focus is inside it, nothing else has claimed the key (popover, menu, dialog, IME).
  useEffect(() => {
    if (!docked || !opened || dialogOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented || e.isComposing || e.keyCode === 229) return;
      const panel = document.querySelector('[data-testid="assistant-drawer"] [role="dialog"]');
      const active = document.activeElement;
      if (!panel || !active || !panel.contains(active)) return;
      if (active.closest('[aria-expanded="true"]') || document.querySelector('[role="menu"], [role="listbox"], .mantine-Popover-dropdown, [role="alertdialog"]')) return;
      onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [docked, opened, dialogOpen, onClose]);

  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      // Escape closes the dialog above the drawer, not the drawer with it (see `dialog-guard`).
      closeOnEscape={!dialogOpen && !docked}
      position={isPhone ? 'bottom' : 'right'}
      size={isPhone ? '100dvh' : 560}
      padding={0}
      withOverlay={!docked}
      trapFocus={!docked}
      lockScroll={!docked}
      // A full-height phone sheet reaches the top edge, where the app header is lifted to 201 while the
      // assistant is open (AppShell): sit above it so the header's controls never cover Close.
      zIndex={isPhone ? 210 : undefined}
      // On a phone Mantine's own return-focus lands on <body>; the effect above puts it back on the opener.
      returnFocus={!isPhone}
      closeOnClickOutside={!docked}
      overlayProps={isPhone ? { backgroundOpacity: 0.12 } : undefined}
      styles={isPhone ? { content: { marginBottom: keyboardInset, maxHeight: `calc(100dvh - ${keyboardInset}px)` } } : undefined}
      data-testid="assistant-drawer"
      classNames={{
        content: isPhone ? `${classes.content} ${classes.sheet}` : classes.content,
        inner: isPhone ? undefined : classes.inner,
        header: classes.header,
        close: classes.close,
        body: classes.body,
        title: classes.title,
      }}
      // Deliberately NOT 'Close assistant': the header trigger already answers to that name while
      // the drawer is open, and two controls sharing an accessible name is an ambiguity a screen
      // reader user cannot resolve. This one closes the panel; that one closes the whole assistant.
      closeButtonProps={{ 'aria-label': t('assistant.drawer.closePanel') }}
      title={
        <>
          <span className={classes.mark} aria-hidden>
            <IconWand size={14} stroke={2} />
          </span>
          <span className={classes.titleText}>
            <span className={classes.name}>{t('assistant.drawer.name')}</span>
            <AssistantTagline />
          </span>
        </>
      }
    >
      {/* Under the header, and part of it to the eye: the one setting that decides whether anything
          the assistant does waits for the person. Not inside the title, which is the dialog's name. */}
      {isPhone ? (
        <AssistantPhoneSettings />
      ) : (
        <>
          <AssistantModeBar />
          <AssistantNotifyToggle />
        </>
      )}
      {/* Above the thread, never instead of it: a history that failed to load is reported here and
          the conversation on screen — composer included — keeps working underneath. */}
      <AssistantHistoryNotice />
      {history}
      <AssistantChatPanel onWorkflowDiff={handleWorkflowDiff} review={review}
        thread={thread}
        welcome={welcome}
        pendingPrompt={pendingPrompt}
        onPromptConsumed={onPromptConsumed}
        onTurnSettled={onTurnSettled}
      />
    </Drawer>
  );
}

/**
 * What the assistant says about itself, from the person's own setting: it asks before it changes
 * anything, or — in auto mode — it does not. Never claims the first before the setting is known to
 * be it: until the server answers, the setting reads as the default, which is to ask.
 */
function AssistantTagline() {
  const t = useT();
  const { mode } = useAssistantPreferences();
  return (
    <span className={classes.tagline}>
      {t(mode === 'auto' ? 'assistant.drawer.taglineAuto' : 'assistant.drawer.taglineAsk')}
    </span>
  );
}

/**
 * The "Ask before changes" switch. On means ask (the default). Turning it OFF goes through a
 * confirmation that says plainly what auto mode includes; turning it back on needs none, since it
 * only ever adds a check.
 */
function AssistantModeBar({ onRequestAuto }: { onRequestAuto?: () => void } = {}) {
  const t = useT();
  const { mode, loaded, error, setMode, autoApproveCostLimitCredits } = useAssistantPreferences();
  const [confirming, setConfirming] = useState(false);
  const asking = mode === 'ask';

  return (
    <div className={classes.modeBar} data-mode={mode}>
      <Switch
        size="md"
        label={t('assistant.drawer.askBeforeChanges')}
        checked={asking}
        // Not flippable before it is known what it would be flipping — but a failed read must not
        // leave the person with a switch that can never be used.
        disabled={!loaded && !error}
        onChange={(event) => {
          if (event.currentTarget.checked) void setMode('ask');
          else if (onRequestAuto) onRequestAuto();
          else setConfirming(true);
        }}
      />
      {!asking && (
        <span className={classes.modeNote}>
          <IconAlertTriangle size={14} aria-hidden />
          {t('assistant.drawer.autoMode')}
        </span>
      )}
      {!asking && autoApproveCostLimitCredits !== undefined && (
        <span className={`${classes.modeNote} ${classes.modeLimit}`} data-testid="auto-cost-limit">
          {t('assistant.drawer.costLimit', { limit: autoApproveCostLimitCredits })}
        </span>
      )}
      {!onRequestAuto && (
        <AutoModeConfirmModal
          opened={confirming}
          onCancel={() => setConfirming(false)}
          onConfirm={() => {
            setConfirming(false);
            void setMode('auto');
          }}
        />
      )}
    </div>
  );
}

/** C4: on a phone sheet the two switches live behind one settings button instead of two rows. */
export function AssistantPhoneSettings() {
  const t = useT();
  const { mode, setMode } = useAssistantPreferences();
  // The confirmation lives HERE, outside the popover: pressing its button is a click outside the
  // dropdown, which closes the popover and would unmount a dialog rendered inside it.
  const [confirming, setConfirming] = useState(false);
  return (
    <div className={classes.settingsBar} data-mode={mode}>
      <Text size="sm" c="dimmed">
        {t(mode === 'auto' ? 'assistant.drawer.autoOn' : 'assistant.drawer.asksBefore')}
      </Text>
      <Popover position="bottom-end" withArrow shadow="md" trapFocus>
        <Popover.Target>
          <ActionIcon size={44} variant="subtle" color="gray" aria-label={t('assistant.drawer.settings')}>
            <IconSettings size={20} aria-hidden />
          </ActionIcon>
        </Popover.Target>
        <Popover.Dropdown>
          <div className={classes.settingsPanel}>
            <AssistantModeBar onRequestAuto={() => setConfirming(true)} />
            <AssistantNotifyToggle />
          </div>
        </Popover.Dropdown>
      </Popover>
      <AutoModeConfirmModal
        opened={confirming}
        onCancel={() => setConfirming(false)}
        onConfirm={() => {
          setConfirming(false);
          void setMode('auto');
        }}
      />
    </div>
  );
}

const noopSubscribe = () => () => {};

/**
 * Opt-in browser notifications for answers that arrive while the person is elsewhere. Off by
 * default; the browser's permission prompt appears only when this switch is turned on. Hidden where
 * the browser has no notifications at all.
 */
export function AssistantNotifyToggle() {
  const t = useT();
  const supported = useSyncExternalStore(noopSubscribe, isNotificationSupported, () => false);
  const on = useSyncExternalStore(subscribeNotifySwitch, getNotifySwitch, () => false);
  const [blocked, setBlocked] = useState(() => notificationPermission() === 'denied');
  // C4: a browser that already blocked notifications cannot be asked again from here, so the switch
  // is not offered at all (the explanation only appears after a refusal in this session).
  const [blockedAtOpen] = useState(() => notificationPermission() === 'denied');
  if (!supported || (blockedAtOpen && !on)) return null;

  return (
    <div className={classes.modeBar}>
      <Switch
        size="md"
        label={t('assistant.drawer.notifyLabel')}
        checked={on}
        aria-describedby={blocked ? 'assistant-notify-help' : undefined}
        onChange={(event) => {
          if (!event.currentTarget.checked) {
            setNotifySwitch(false);
            return;
          }
          void enableNotifications().then((permission) => setBlocked(permission === 'denied'));
        }}
      />
      {blocked && (
        <Text id="assistant-notify-help" size="xs" c="dimmed">
          {t('assistant.drawer.notifyBlocked')}
        </Text>
      )}
    </div>
  );
}

/**
 * What the drawer says about the thread's history: that it is still arriving, or that it could not
 * be read and can be asked for again. Both are live regions — the load starts after the drawer is
 * already on screen, so nothing else moves when it finishes or fails.
 */
function AssistantHistoryNotice() {
  const t = useT();
  const { status, error, reload } = useAssistantHistory();

  if (status === 'loading') {
    return (
      <Group
        gap={8}
        px="sm"
        py={6}
        wrap="nowrap"
        role="status"
        aria-live="polite"
        data-testid="assistant-history-loading"
      >
        <Loader size="xs" />
        <Text size="sm" c="dimmed">
          {t('assistant.drawer.loadingThread')}
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
        data-testid="assistant-history-error"
        title={t('assistant.drawer.loadThreadFailed')}
        icon={<IconAlertCircle size={16} />}
      >
        <Stack gap="xs" align="flex-start">
          <Text size="sm">{error}</Text>
          <Button size="md" variant="light" color="red" onClick={reload}>
            {t('assistant.drawer.tryAgain')}
          </Button>
        </Stack>
      </Alert>
    );
  }

  return null;
}
