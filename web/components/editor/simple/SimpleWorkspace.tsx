'use client';

import React from 'react';
import { Button, CloseButton, MantineThemeProvider, UnstyledButton } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconAdjustments, IconArrowLeft, IconChevronLeft, IconKeyboard, IconScissors, IconSend, IconWand } from '@tabler/icons-react';
import type { SaveState } from '@/lib/editing/sync';
import type { SimplePlace } from '@/lib/editing/shell-choice';
import { SIMPLE_PLACES } from '@/lib/editing/shell-choice';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import { RETRYABLE } from '../shell/StatusBar';
import { saveStateText } from '../shell/save-state-text';
import { EditorIcon } from '../ui';
import classes from './simple.module.css';
import { useT } from '@/i18n';

const PLACE_ICONS: Record<SimplePlace, React.ComponentType<{ size?: number | string; 'aria-hidden'?: boolean }>> = {
  edit: IconScissors,
  look: IconAdjustments,
  export: IconSend,
};

export interface SimpleWorkspaceProps {
  name: string;
  save: SaveState;
  /** Where "back" goes: the hub's Timelines view of this project. */
  backHref: string;
  onRetry: () => void;
  onSignIn: () => void;
  layout: 'desktop' | 'tablet';
  place: SimplePlace;
  onPlace: (place: SimplePlace) => void;
  /** The Simple | Full switch. */
  shellSwitch: React.ReactNode;
  /** Background results waiting for the user's OK. */
  pending: React.ReactNode;
  onShortcuts: () => void;
  /** The one-line note shown the first time, with its two actions. */
  introOpen: boolean;
  onDismissIntro: () => void;
  onOpenFull: () => void;
  /** Sync notices and the offline relink bar: the same nodes the full editor shows. */
  banner: React.ReactNode;
  dialogs: React.ReactNode;
  /** The media list. */
  media: React.ReactNode;
  /** The viewer zone: the same picture and transport nodes the full editor uses, and what full screen needs. */
  picture: React.ReactNode;
  transport: React.ReactNode;
  attachViewer: (element: HTMLDivElement | null) => void;
  fullscreenOn: boolean;
  viewerTheme: React.ComponentProps<typeof MantineThemeProvider>['theme'];
  /** The "Ask for a change" line. */
  ask: React.ReactNode;
  timeline: React.ReactNode;
  /** The side panel: its heading, an optional way back to the task list, and its body. */
  panelTitle: string;
  onPanelBack?: () => void;
  panel: React.ReactNode;
  /**
   * The suggestions panel (the AI tools). Once opened it stays mounted, hidden while a place or another task is
   * showing, so picking Look or Export never throws away what it was doing. Null until the first time it opens.
   */
  aiPanel: React.ReactNode;
  /** Whether the suggestions panel is the one showing. */
  aiActive: boolean;
  /** Show the suggestions panel from wherever the user is. */
  onOpenAi: () => void;
}

/**
 * The Simple shell's frame. It owns layout only: every piece it shows (viewer, transport, timeline, media list, the
 * panel's body, notices and dialogs) is the same node the full editor builds, handed in by the view hook.
 */
export function SimpleWorkspace({ name, save, backHref, onRetry, onSignIn, layout, place, onPlace, shellSwitch, pending, onShortcuts, introOpen, onDismissIntro, onOpenFull, banner, dialogs, media, picture, transport, attachViewer, fullscreenOn, viewerTheme, ask, timeline, panelTitle, onPanelBack, panel, aiPanel, aiActive, onOpenAi }: SimpleWorkspaceProps) {
  const t = useT('editor-simple.shell');
  const coarse = useMediaQuery('(pointer: coarse)', false, { getInitialValueInEffect: true }) ?? false;
  const saveText = saveStateText(t, save);
  const canRetry = RETRYABLE.has(save.status) && save.problem !== 'deleted' && save.problem !== 'noPermission';
  return (
    <div className={classes.root} data-testid="editor-workspace" data-shell="simple" data-place={place} data-layout={layout === 'tablet' ? 'tablet' : undefined}>
      <header className={classes.header} data-testid="simple-header">
        <div className={classes.headerStart}>
          <EditorIcon icon={IconChevronLeft} size="lg" href={backHref} label={t('backToAllTimelines')} tooltip={t('allTimelines')} data-testid="back-to-timelines" />
          <h1 className={classes.name} title={name} data-testid="page-heading">
            {name}
          </h1>
          <span className={classes.saveState} data-testid="save-state-chip" data-status={save.status} data-problem={save.status === 'saved' || save.status === 'saving' ? undefined : 'true'} title={saveText} role="status">
            {save.status === 'saved' ? t('saved') : saveText}
          </span>
          {canRetry && (
            <Button size="compact-xs" fz="xs" variant="default" onClick={onRetry} data-testid="simple-retry">
              {t('retry')}
            </Button>
          )}
          {save.status === 'authExpired' && (
            <Button size="compact-xs" fz="xs" variant="default" onClick={onSignIn} data-testid="simple-sign-in">
              {t('signIn')}
            </Button>
          )}
          <EarlyPreviewLabel />
        </div>
        <div className={classes.headerEnd}>
          {pending}
          {shellSwitch}
          <EditorIcon icon={IconKeyboard} size="lg" label={t('keyboardShortcuts')} tooltip={t('keyboardShortcuts')} onClick={onShortcuts} data-testid="open-keymap" />
          <Button size="xs" fz="xs" ml={4} miw="max-content" style={{ flexShrink: 0 }} leftSection={<IconSend size={14} stroke={1.8} />} onClick={() => onPlace('export')} data-testid="open-deliver">
            {t('export')}
          </Button>
        </div>
      </header>
      {introOpen && (
        <div className={classes.intro} role="note" data-testid="simple-intro">
          <span>{t('intro')}</span>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8, flexShrink: 0 }}>
            <Button size="compact-xs" fz="xs" variant="default" onClick={onOpenFull} data-testid="simple-intro-full">
              {t('openFull')}
            </Button>
            <CloseButton size={coarse ? 44 : 'sm'} aria-label={t('dismissIntro')} onClick={onDismissIntro} data-testid="simple-intro-dismiss" />
          </span>
        </div>
      )}
      {banner}
      <div className={classes.frame} data-layout={layout === 'tablet' ? 'tablet' : undefined}>
        <div className={classes.left} data-testid="simple-left">
          <nav className={classes.places} aria-label={t('places')} data-testid="simple-places">
            {SIMPLE_PLACES.map((entry) => {
              const Icon = PLACE_ICONS[entry];
              return (
                <UnstyledButton key={entry} className={classes.place} data-active={place === entry} aria-current={place === entry ? 'page' : undefined} onClick={() => onPlace(entry)} data-testid={`simple-place-${entry}`}>
                  <Icon size={18} aria-hidden />
                  {t(`place.${entry}`)}
                </UnstyledButton>
              );
            })}
          </nav>
          <div className={classes.leftBody}>{media}</div>
        </div>
        <div className={classes.centre}>
          <div className={classes.viewer} ref={attachViewer} data-fullscreen={fullscreenOn || undefined} data-testid="viewer-zone">
            <MantineThemeProvider theme={viewerTheme}>
              <div style={{ flex: '1 1 0px', minHeight: 0, display: 'flex', flexDirection: 'column' }}>{picture}</div>
              {transport}
            </MantineThemeProvider>
          </div>
          {ask}
          <div className={classes.timeline}>{timeline}</div>
        </div>
        <section className={classes.right} aria-label={panelTitle} data-testid="simple-panel">
          <div className={classes.panelHeader}>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, minWidth: 0 }}>
              {onPanelBack && <EditorIcon icon={IconArrowLeft} label={t('backToTasks')} tooltip={t('backToTasks')} onClick={onPanelBack} data-testid="simple-panel-back" />}
              <h2 className={classes.panelTitle} data-testid="simple-panel-title">
                {panelTitle}
              </h2>
            </span>
            <Button size={coarse ? 'sm' : 'compact-xs'} fz="xs" variant={aiActive ? 'filled' : 'light'} leftSection={<IconWand size={14} stroke={1.8} aria-hidden />} aria-pressed={aiActive} onClick={onOpenAi} data-testid="simple-ai-tools" style={{ flexShrink: 0 }}>
              {t('aiTools')}
            </Button>
          </div>
          <div className={classes.rightBody} hidden={aiActive} data-testid="simple-panel-body">
            {aiActive ? null : panel}
          </div>
          <div className={classes.rightBody} hidden={!aiActive} data-testid="simple-ai-panel">
            {aiPanel}
          </div>
        </section>
      </div>
      {dialogs}
    </div>
  );
}
