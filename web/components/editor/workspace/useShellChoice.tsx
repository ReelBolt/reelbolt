'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import type { WorkspaceId } from '@/lib/editing/store';
import type { KeyAction } from '@/lib/editing/keymap';
import { keyActionLabelMsg } from '@/lib/editing/labels-msg';
import { useEditorPreferences } from '@/lib/hooks/use-editor-p9';
import type { ProposalAgent } from '@/lib/editing/sync/api';
import { introSeen, markIntroSeen, readStoredShell, resolveShell, storeShell, type EditorShell, type SimplePlace } from '@/lib/editing/shell-choice';
import { useT } from '@/i18n';
import type { PageLayoutCtx } from './usePageLayout';

/** How long a desktop with no stored choice waits for the saved preferences before it starts Simple. */
export const PREFERENCES_WAIT_MS = 2000;

/** What the Simple shell's side panel shows on the Edit place: the task list or the selection (home), or one task's own panel. */
export type SimpleView = 'home' | 'captions' | 'title' | 'sound' | 'format' | 'ask';

/** A request typed in the ask box: which helper, the words, and a counter so the same words asked twice open a fresh panel. */
export interface AskSeed {
  agent: ProposalAgent;
  brief: string;
  nonce: number;
}

/** Where "Open in the full editor" lands: a page, and optionally a tab of the Edit page's side groups. */
export interface FullTarget {
  workspace?: WorkspaceId;
  leftTab?: string;
  rightTab?: string;
}

const NOTICE_ID = 'editor-simple-open-full';

/**
 * Which shell the editor shows (Simple or Full), the Simple shell's place and side-panel view, and the ways from
 * Simple into the full editor. View state only: nothing here is written to the timeline.
 */
export function useShellChoice(ctx: PageLayoutCtx) {
  const { isPhone, isTablet, preferences, ui, setWorkspace, setLeftTab, setRightTab } = ctx;
  const tS = useT('editor-simple.shell');
  const [chosen, setChosen] = useState<EditorShell | null>(() => (typeof window === 'undefined' ? null : readStoredShell(window.localStorage)));
  const { error: preferencesError } = useEditorPreferences();
  const [waited, setWaited] = useState(false);
  useEffect(() => {
    const timer = window.setTimeout(() => setWaited(true), PREFERENCES_WAIT_MS);
    return () => window.clearTimeout(timer);
  }, []);
  const resolved = resolveShell({
    stored: chosen,
    deviceClass: isTablet ? 'tablet' : 'desktop',
    preferences: preferences ?? null,
    preferencesSettled: preferences !== undefined || preferencesError !== undefined || waited,
  });
  // The first answer stands for this visit: preferences that arrive late never flip the shell under the user's hands.
  const [latched, setLatched] = useState<EditorShell | null>(null);
  if (latched === null && resolved !== null) setLatched(resolved);
  /** Null while it is not known yet (a desktop with no stored choice, preferences still loading). The phone has its own surface. */
  const shell: EditorShell | null = isPhone ? 'full' : (chosen ?? latched ?? resolved);
  const simple = !isPhone && shell === 'simple';

  const [simplePlace, setPlace] = useState<SimplePlace>('edit');
  const [simpleView, setSimpleView] = useState<SimpleView>('home');
  const [askSeed, setAskSeed] = useState<AskSeed | null>(null);
  const setSimplePlace = useCallback((place: SimplePlace) => {
    setPlace(place);
    setSimpleView('home');
  }, []);
  const setShell = useCallback((next: EditorShell) => {
    storeShell(typeof window === 'undefined' ? null : window.localStorage, next);
    setChosen(next);
  }, []);

  // Simple has one page underneath, Edit. A link or a remembered page that points elsewhere opens the matching place
  // (decided while rendering, so the place is right on the first frame), and the page underneath returns to Edit.
  const pagePlace: SimplePlace | null = !simple ? null : ui.workspace === 'deliver' ? 'export' : ui.workspace === 'color' ? 'look' : null;
  if (pagePlace !== null && simplePlace !== pagePlace) {
    setPlace(pagePlace);
    setSimpleView('home');
  }
  useEffect(() => {
    if (simple && ui.workspace !== 'edit') setWorkspace('edit');
  }, [simple, ui.workspace, setWorkspace]);

  const openInFull = useCallback(
    (target?: FullTarget) => {
      setShell('full');
      if (target?.workspace) setWorkspace(target.workspace);
      if (target?.leftTab) setLeftTab(target.leftTab);
      if (target?.rightTab) setRightTab(target.rightTab);
    },
    [setShell, setWorkspace, setLeftTab, setRightTab],
  );

  // An action that needs the full editor: it runs there once the shell has switched (the command hook reads this).
  const pendingFullActionRef = useRef<KeyAction | null>(null);
  const openFullForAction = useCallback(
    (id: KeyAction, fromKeyboard: boolean) => {
      const go = () => {
        notifications.hide(NOTICE_ID);
        pendingFullActionRef.current = id;
        setShell('full');
      };
      // The palette is a deliberate pick, so it switches. A key press may be a slip: it is offered, never forced.
      if (!fromKeyboard) return go();
      notifications.show({
        id: NOTICE_ID,
        color: 'gray',
        autoClose: 8000,
        title: tS('needsFullTitle', { action: tS.msg(keyActionLabelMsg(id)) }),
        message: (
          <Group gap="xs" wrap="nowrap">
            <Text size="sm">{tS('needsFullBody')}</Text>
            <Button size="compact-sm" fz="xs" variant="light" onClick={go} data-testid="simple-open-full-notice">
              {tS('openFull')}
            </Button>
          </Group>
        ),
      });
    },
    [setShell, tS],
  );

  const [introOpen, setIntroOpen] = useState(() => (typeof window === 'undefined' ? false : !introSeen(window.localStorage)));
  const dismissIntro = useCallback(() => {
    markIntroSeen(typeof window === 'undefined' ? null : window.localStorage);
    setIntroOpen(false);
  }, []);

  return { shell, simple, setShell, simplePlace, setSimplePlace, simpleView, setSimpleView, askSeed, setAskSeed, openInFull, pendingFullActionRef, openFullForAction, simpleIntroOpen: simple && introOpen, dismissIntro };
}

export type ShellChoiceCtx = PageLayoutCtx & ReturnType<typeof useShellChoice>;
