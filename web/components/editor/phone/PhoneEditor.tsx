'use client';

import { onProposalReviewRequested } from '@/lib/editing/background/proposal';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import { useForegroundRefresh, useRenderCompletion } from '../deliver/deliver-phone';
import { DeliverPreview } from './DeliverPreview';
import type { WorkspaceId } from '@/lib/editing/store';
import type { SaveState } from '@/lib/editing/sync';
import { BottomSheet, type SheetDetent } from './BottomSheet';
import { LookSheetBody, type PhoneFixEnv } from './LookSheet';
import { PhoneActionBar } from './PhoneActionBar';
import type { PhoneActions } from './phone-actions';
import { PhoneDock, type DockItem } from './PhoneDock';
import { PhoneHeader, type OverflowSheet } from './PhoneHeader';
import { PhoneNote } from './PhoneNote';
import { PhoneShell } from './PhoneShell';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import classes from './phone.module.css';
import { UndoSnackbar } from './UndoSnackbar';
import { setTouchDragEnabled, useTouchDragEnabled } from './touch-drag-flag';
import { useFollowHost } from '@/lib/editing/follow/follow-host';
import { useT } from '@/i18n';

export type PhoneSheetId = DockItem | OverflowSheet | 'deliver';


/** Where each sheet opens: the export and the media pool want room; the rest start at half. */
const SHEET_DETENT: Record<PhoneSheetId, SheetDetent> = {
  media: 'half',
  edit: 'half',
  audio: 'half',
  look: 'half',
  captions: 'half',
  deliver: 'full',
  assist: 'half',
  review: 'half',
  history: 'half',
};

const DOCK_IDS: readonly string[] = ['media', 'edit', 'audio', 'look', 'captions'];

export interface PhoneEditorProps {
  name: string;
  save: SaveState;
  backHref: string;
  reviewHref: string;
  canUndo: boolean;
  canRedo: boolean;
  onUndo: () => void;
  onRedo: () => void;
  onRetry: () => void;
  onSignIn: () => void;
  /** The action bar's handlers (RM.5), built from the editor's own. */
  actions: PhoneActions;
  readOnly: boolean;
  /** The program picture, the transport, the timeline with its toolbar: the same nodes the desktop uses. */
  picture: React.ReactNode;
  transport: React.ReactNode;
  timeline: React.ReactNode;
  /** Sync notices (conflict bar, offline relink): rendered above the dock. */
  banner: React.ReactNode;
  /** The compact status line (dropped-frame counter, preview kind). */
  status?: React.ReactNode;
  dialogs: React.ReactNode;
  /** The workspace the editor is showing, and how to change it (a sheet picks one). */
  workspace: WorkspaceId;
  onWorkspace: (workspace: WorkspaceId) => void;
  /** What the current workspace shows (media pool, mixer, color, export). */
  workspaceTools: React.ReactNode;
  /** RM.8: the Add media sheet. `collapse` shrinks the sheet to a peek (after an Insert, so the strip shows the result). */
  renderMedia?: (ctx: { collapse: () => void }) => React.ReactNode;
  /** RM.11 / RM.12: the Audio and Captions sheets. `close` shuts the sheet (a full-screen dialog opens from it). */
  renderAudio?: (ctx: { close: () => void }) => React.ReactNode;
  renderCaptions?: (ctx: { close: () => void }) => React.ReactNode;
  /** RM.13: the Assist (AI proposal review) sheet. */
  renderAssist?: () => React.ReactNode;
  /** What the Look sheet's Fix exposure button needs to read the viewer; absent, the button is disabled. */
  fixExposure?: PhoneFixEnv;
  /** Opens the platform assistant (a full-height sheet over the app). Absent outside the app shell. */
  onAskAssistant?: () => void;
  /** The AI tools drawer (the "AI tools" entry in the overflow menu opens it). */
  renderAiTools?: (a: { opened: boolean; onClose: () => void; openSheet: (sheet: PhoneSheetId) => void }) => React.ReactNode;
  /** The inspector / assist / revisions panels, and the panels with captions, review and history. */
  rightPanel: React.ReactNode;
  leftPanel: React.ReactNode;
  onRightTab: (tab: string) => void;
  onLeftTab: (tab: string) => void;
  /** The render queue and how to refetch it: a finished render is announced, and the queue is refetched when the page returns to the foreground. */
  renders?: readonly EditTimelineRenderSummary[];
  onRefreshRenders?: () => void;
}

/** The Edit (inspector) sheet's half detent: at least half the screen so the inspector is usable (C7). */
const INSPECTOR_HALF_HEIGHT = '52dvh';

/** Which workspace and panel tab a sheet needs the editor to be showing. */
function prepare(sheet: PhoneSheetId): { workspace: WorkspaceId; right?: string; left?: string } {
  switch (sheet) {
    case 'media':
      return { workspace: 'media' };
    case 'edit':
      return { workspace: 'edit', right: 'inspector' };
    case 'audio':
      return { workspace: 'audio' };
    case 'look':
      return { workspace: 'edit' };
    case 'captions':
      return { workspace: 'edit', left: 'subtitles' };
    case 'deliver':
      return { workspace: 'deliver' };
    case 'assist':
      return { workspace: 'edit', right: 'assist' };
    case 'review':
      return { workspace: 'edit', left: 'review' };
    case 'history':
      return { workspace: 'edit', left: 'history' };
  }
}

/**
 * The phone surface (RM.6): header, viewer, strip, dock and the sheets, wired to the slot nodes the
 * editor already builds. Each dock item opens a sheet that hosts the existing panel, so nothing the
 * desktop can do on a phone is lost; later tickets replace a sheet's content, not this frame.
 */
export function PhoneEditor(props: PhoneEditorProps) {
  const t = useT('editor.phone.phoneEditor');
  const { workspace, onWorkspace, onLeftTab, onRightTab } = props;
  const [sheet, setSheet] = useState<PhoneSheetId | null>(null);
  const [detent, setDetent] = useState<SheetDetent>('half');
  const [aiToolsOpen, setAiToolsOpen] = useState(false);
  const landscape = useMediaQuery('(max-height: 499.98px)', false, { getInitialValueInEffect: false }) ?? false;
  const touchDrag = useTouchDragEnabled();
  // A render finished while the Export sheet was closed: the Export button says so until the sheet opens.
  const [exportBadge, setExportBadge] = useState(false);
  const sheetRef = useRef<PhoneSheetId | null>(null);
  useEffect(() => {
    sheetRef.current = sheet;
  }, [sheet]);
  const { onRefreshRenders } = props;
  useForegroundRefresh(() => onRefreshRenders?.());
  useRenderCompletion(props.renders, (done) => {
    notifications.show({ color: 'green', title: t('yourFilmIsReady'), message: t('hasFinishedRendering', { outputFileName: done[0].outputFileName }) });
    if (sheetRef.current !== 'deliver') setExportBadge(true);
  });

  // Picking an object on the picture: the sheet drops to its peek so the mini viewer is free, then returns with the card.
  const picking = Boolean(useFollowHost().armed?.onSeed);

  const open = useCallback(
    (next: PhoneSheetId) => {
      const need = prepare(next);
      onWorkspace(need.workspace);
      if (need.right) onRightTab(need.right);
      if (need.left) onLeftTab(need.left);
      if (next === 'deliver') setExportBadge(false);
      setDetent(SHEET_DETENT[next]);
      setSheet(next);
    },
    [onWorkspace, onLeftTab, onRightTab],
  );
  const close = useCallback(() => setSheet(null), []);
  // "Review" on a finished AI suggestion opens the Assist sheet on it.
  useEffect(() => onProposalReviewRequested(() => open('assist')), [open]);

  const need = sheet ? prepare(sheet) : null;
  // The workspace flips in the same tick as the sheet opens; render nothing until it has, so a sheet never flashes the previous tool.
  const ready = need !== null && need.workspace === workspace;
  const content = !sheet || !ready ? null : sheet === 'look' ? <LookSheetBody actions={props.actions} fix={props.fixExposure ?? null} /> : sheet === 'media' && props.renderMedia ? props.renderMedia({ collapse: () => setDetent('peek') }) : sheet === 'audio' && props.renderAudio ? props.renderAudio({ close }) : sheet === 'captions' && props.renderCaptions ? props.renderCaptions({ close }) : sheet === 'assist' && props.renderAssist ? props.renderAssist() : sheet === 'edit' || sheet === 'assist' ? props.rightPanel : sheet === 'captions' || sheet === 'review' || sheet === 'history' ? props.leftPanel : props.workspaceTools;

  return (
    <>
      <PhoneShell
        landscape={landscape}
        sheetOpen={sheet !== null}
        header={
          <PhoneHeader
            name={props.name}
            save={props.save}
            backHref={props.backHref}
            reviewHref={props.reviewHref}
            canUndo={props.canUndo && !props.readOnly}
            canRedo={props.canRedo && !props.readOnly}
            onUndo={props.onUndo}
            onRedo={props.onRedo}
            onExport={() => open('deliver')}
            exportBadge={exportBadge}
            onOpenSheet={open}
            touchDrag={touchDrag}
            onToggleTouchDrag={() => setTouchDragEnabled(!touchDrag)}
            onAskAssistant={props.onAskAssistant}
            onOpenAiTools={props.renderAiTools ? () => setAiToolsOpen(true) : undefined}
            onRetry={props.onRetry}
            onSignIn={props.onSignIn}
            landscape={landscape}
          />
        }
        viewer={sheet === 'deliver' ? <DeliverPreview name={props.name} /> : props.picture}
        transport={props.transport}
        timeline={props.timeline}
        banner={props.banner}
        status={
          // D2+: the phone says "Early preview" in words. It lives on the status line, not the header, so the
          // timeline name and save state keep their room there.
          <div className={classes.statusRow} data-testid="phone-status-row">
            <div className={classes.statusRowMain}>{props.status}</div>
            <div className={classes.statusRowLabel}>
              <EarlyPreviewLabel />
            </div>
          </div>
        }
        note={<PhoneNote />}
        actionBar={<PhoneActionBar actions={props.actions} onAddMedia={() => open('media')} />}
        dock={<PhoneDock active={sheet && DOCK_IDS.includes(sheet) ? (sheet as DockItem) : null} onSelect={(item) => (sheet === item ? close() : open(item))} orientation={landscape ? 'rail' : 'bar'} />}
        sheets={
          sheet && (
            <BottomSheet
              key={sheet}
              opened
              onClose={close}
              title={t(`sheetTitles.${sheet}`)}
              detent={picking ? 'peek' : detent}
              onDetentChange={setDetent}
              side={landscape ? 'right' : 'bottom'}
              testId="phone-sheet"
              halfHeight={sheet === 'edit' ? INSPECTOR_HALF_HEIGHT : undefined}
            >
              {content}
            </BottomSheet>
          )
        }
      >
        {props.dialogs}
      </PhoneShell>
      {props.renderAiTools?.({ opened: aiToolsOpen, onClose: () => setAiToolsOpen(false), openSheet: open })}
      <UndoSnackbar />
    </>
  );
}
