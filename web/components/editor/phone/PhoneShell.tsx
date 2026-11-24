'use client';

import React, { useState } from 'react';
import { IconArrowsMaximize, IconArrowsMinimize, IconEye, IconEyeOff } from '@tabler/icons-react';
import { UnstyledButton } from '@mantine/core';
import classes from './phone.module.css';
import { useT } from '@/i18n';

export type ViewerMode = 'full' | 'mini' | 'hidden';

export interface PhoneShellProps {
  header: React.ReactNode;
  /** The program picture (no transport). */
  viewer: React.ReactNode;
  /** The transport controls under the picture; shown with the full viewer only. */
  transport: React.ReactNode;
  /** The strip: the timeline with its toolbar. Never shorter than 160 px. */
  timeline: React.ReactNode;
  /** A compact bar above the dock: a sync conflict, the read-only note. */
  banner?: React.ReactNode;
  /** The page-level note (the escape hatch's "Phone view" note). */
  note?: React.ReactNode;
  /** Row above the dock for actions on the selection (Add marker and similar). */
  actionBar?: React.ReactNode;
  /** The status line (save, dropped frames, preview kind), compact, under the strip. */
  status?: React.ReactNode;
  dock: React.ReactNode;
  /** The sheets (each renders itself in a portal; kept here so the slot is explicit). */
  sheets?: React.ReactNode;
  /** True while any sheet is open: the viewer shrinks to the mini size so the strip stays visible. */
  sheetOpen?: boolean;
  /** Landscape phone: viewer left, strip right, dock as a left rail. */
  landscape?: boolean;
  /** Everything that must render outside the layout (dialogs). */
  children?: React.ReactNode;
}

/**
 * The phone layout. It owns only placement and the viewer's size (full, mini, hidden); every slot is
 * filled by the editor with the components the desktop also uses, so nothing is a reduced copy.
 */
export function PhoneShell({ header, viewer, transport, timeline, banner, note, actionBar, status, dock, sheets, sheetOpen = false, landscape = false, children }: PhoneShellProps) {
  const t = useT('editor.phone.phoneShell');
  const [mode, setMode] = useState<ViewerMode>('full');
  const effective: ViewerMode = mode === 'full' && sheetOpen ? 'mini' : mode;
  const next: ViewerMode = mode === 'full' ? 'mini' : mode === 'mini' ? 'hidden' : 'full';
  const nextLabel = mode === 'full' ? t('makeTheViewerSmaller') : mode === 'mini' ? t('hideTheViewer') : t('showTheViewer');
  return (
    <div className={classes.shell} data-layout="phone" data-landscape={landscape ? 'true' : undefined} data-viewer-mode={effective} data-sheet-open={sheetOpen ? 'true' : undefined} data-testid="editor-workspace">
      <div className={classes.shellHeader}>{header}</div>
      <div className={classes.shellDock}>{dock}</div>
      {effective !== 'hidden' ? (
        <div className={classes.shellViewer} data-testid="phone-viewer">
          <div className={classes.shellPicture}>
            {viewer}
            {mode !== 'full' && (
              <UnstyledButton className={classes.viewerGrow} onClick={() => setMode('full')} aria-label={t('makeTheViewerBigger')} data-testid="phone-viewer-grow">
                <IconArrowsMaximize size={18} aria-hidden />
              </UnstyledButton>
            )}
            <UnstyledButton className={classes.viewerSize} aria-label={nextLabel} onClick={() => setMode(next)} data-testid="phone-viewer-size">
              {mode === 'full' ? <IconArrowsMinimize size={18} aria-hidden /> : <IconEyeOff size={18} aria-hidden />}
            </UnstyledButton>
          </div>
          {effective === 'full' && <div className={classes.shellTransport}>{transport}</div>}
        </div>
      ) : (
        <UnstyledButton className={classes.showViewer} onClick={() => setMode('full')} data-testid="phone-viewer-show" aria-label={t('showTheViewer2')}>
          <IconEye size={18} aria-hidden /> {t('showTheViewer3')}
        </UnstyledButton>
      )}
      <div className={classes.shellStrip} data-testid="phone-strip">
        {note}
        <div className={classes.stripBody}>{timeline}</div>
        {banner}
        {actionBar}
        {status}
      </div>
      {sheets}
      {children}
    </div>
  );
}
