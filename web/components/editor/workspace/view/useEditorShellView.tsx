'use client';

import { Box } from '@mantine/core';
import { PhoneEditor } from '../../phone/PhoneEditor';
import { AddMediaSheet } from '../../phone/AddMediaSheet';
import { showUndoSnackbar } from '../../phone/UndoSnackbar';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { newGuid } from '@/lib/editing/ids';
import { GradePreviewContext } from '@/lib/editing/color/grade-preview';
import { PanelLayout, WorkspaceBar } from '../../shell';
import { PendingResults } from '../../background/PendingResults';
import { AiToolsLauncher } from '../../shell/AiToolsLauncher';
import classes from '../../shell/workspace.module.css';
import { editorHubHref, timelineReviewHref } from '@/lib/editing/open-timeline';
import { AudioSheet } from '../../phone/AudioSheet';
import { ColorNodesPanel, ColorGradeToolsPanel } from '../../color/ColorWorkspace';
import { ProposalSheet } from '../../phone/ProposalSheet';
import { LiveChordsProvider } from '../../prefs/live-chords';
import { useT } from '@/i18n';
import { LiveVoRecorderPanel, LiveCaptionsSheet, LiveColorPage } from '../live-panels';
import type { SimpleViewCtx } from './useSimpleView';

/** Puts the pieces in the frame: the phone surface, the Simple shell, or the full editor's pages. A view hook: it returns elements and keeps no state. */
export function useEditorShellView(ctx: SimpleViewCtx) {
  const { store, sync, seedStepResultId, isPhone, doc, save, projectId, timelineId, undo, redo, phoneActions, seek, getCanvas, applyBatch, readOnly, viewerPicture, viewerTransport, timeline, banner, statusBar, dialogs, busyGuard, ui, setWorkspace, workspaceTools, placeChosen, flags, onToggleTrack, dispatch, onSetProperty, phoneMoreTools, setSilenceOpen, setBeatsOpen, platformAssistant, aiToolsCtx, assistHelper, setGhostState, rightPanel, leftPanel, setRightTab, setLeftTab, renders, refreshRenders, editorPage, colorViewer, viewer, audioRightPanel, composeRightPanel, cutOverview, appliedSizes, appliedFractions, layoutReset, onPanelSizes, runningChords, gradePreviewValue, isTablet, header, pageChords, mediaPage, deliverPage, colorPageProps, shell, simpleShell } = ctx;
  const tx = useT('editor.editorWorkspace');
  if (isPhone) {
    // The phone surface: header, viewer, strip, dock and sheets. Every slot is the same component the desktop
    // shows (or hosts it in a sheet), so nothing is a reduced copy that could drift.
    return (
      <PhoneEditor
        name={doc.name}
        save={save}
        backHref={editorHubHref(projectId)}
        reviewHref={timelineReviewHref(projectId, timelineId)}
        canUndo={store.canUndo()}
        canRedo={store.canRedo()}
        onUndo={undo}
        onRedo={redo}
        onRetry={() => void sync.retry()}
        onSignIn={() => sync.requestSignIn()}
        actions={phoneActions}
        fixExposure={{ getDoc: () => store.getState().doc, getPlayhead: () => store.getState().ui.playheadFrame, seek, getCanvas, apply: applyBatch, newId: newGuid }}
        readOnly={readOnly}
        picture={viewerPicture}
        transport={viewerTransport}
        timeline={timeline}
        banner={
          <>
            {banner}
            {/* The phone has no header: the "needs your OK" chip lives here, and a "Ready to review" tap on a clip opens it. */}
            <PendingResults store={store} projectId={projectId} compact />
          </>
        }
        status={statusBar}
        dialogs={
          <>
            {dialogs}
            {busyGuard}
          </>
        }
        workspace={ui.workspace}
        onWorkspace={setWorkspace}
        workspaceTools={workspaceTools}
        renderMedia={({ collapse }) => (
          <AddMediaSheet
            projectId={projectId}
            readOnly={readOnly}
            onPlace={(item, mode, marks) => placeChosen(item, mode, marks)}
            onPlaced={(item) => {
              collapse();
              showUndoSnackbar({ message: tx('addedToTheTimeline', { fileName: item.fileName }), onUndo: undo });
            }}
          />
        )}
        renderAudio={() => (
          <AudioSheet
            doc={doc}
            readOnly={readOnly}
            actions={phoneActions}
            flags={flags}
            onToggle={onToggleTrack}
            onApply={applyBatch}
            projectId={projectId}
            selection={ui.selection}
            onGain={(trackId, db) => {
              const target = store.getState().doc.tracks.find((t) => t.id === trackId);
              dispatch({ type: 'setTrackProperty', trackId, key: 'gainDb', value: { value: db, keys: target?.gainDb?.keys ?? null } }, `Volume ${target?.name ?? 'track'}`);
            }}
            voiceover={<LiveVoRecorderPanel store={store} doc={doc} projectId={projectId} readOnly={readOnly} newId={newMarkerId} onApply={applyBatch} phone />}
          />
        )}
        renderCaptions={({ close }) => (
          <LiveCaptionsSheet
            store={store}
            doc={doc}
            projectId={projectId}
            seedStepResultId={seedStepResultId}
            readOnly={readOnly}
            newItemId={newMarkerId}
            newTrackId={newMarkerId}
            onApply={applyBatch}
            onSetProperty={onSetProperty}
            onSeek={seek}
            onUndo={undo}
            moreTools={phoneMoreTools}
            onRemoveSilence={() => {
              close();
              setSilenceOpen(true);
            }}
            onCutToBeat={() => {
              close();
              setBeatsOpen(true);
            }}
          />
        )}
        onAskAssistant={platformAssistant ? platformAssistant.open : undefined}
        renderAiTools={({ opened, onClose, openSheet }) => <AiToolsLauncher phone opened={opened} onClose={onClose} context={{ ...aiToolsCtx, openSheet }} projectId={projectId} />}
        renderAssist={() => (
          <ProposalSheet key={assistHelper ?? 'any'} initialAgent={assistHelper} doc={doc} projectId={projectId} timelineId={timelineId} readOnly={readOnly} inOut={ui.inOut} onGhosts={setGhostState} onApply={applyBatch} onSeek={seek} onUndo={undo} />
        )}
        rightPanel={rightPanel}
        leftPanel={leftPanel}
        onRightTab={setRightTab}
        onLeftTab={setLeftTab}
        renders={renders}
        onRefreshRenders={refreshRenders}
      />
    );
  }

  // Which shell a desktop shows is not known until the saved preferences answered (a stored choice answers at once):
  // the frame waits rather than show one shell and swap it for the other.
  if (shell === null) return <div className={classes.root} data-testid="editor-workspace" data-shell="pending" aria-busy="true" />;
  if (simpleShell) return simpleShell;

  // §5.6/§8.2: Colour fills the frame's three zones — nodes on the left, the viewer with the scopes
  // docked under it in the centre, the grade-tool tabs on the right — under one colour-page provider.
  const desktopPanel = (
    <PanelLayout
      page={editorPage}
      left={ui.workspace === 'color' ? <ColorNodesPanel /> : workspaceTools}
      viewer={ui.workspace === 'color' ? colorViewer : viewer}
      right={ui.workspace === 'color' ? <ColorGradeToolsPanel /> : ui.workspace === 'audio' ? audioRightPanel : ui.workspace === 'compose' ? composeRightPanel : rightPanel}
      timeline={timeline}
      overview={cutOverview}
      dockRightOnTablet
      appliedSizes={appliedSizes}
      appliedFractions={appliedFractions}
      resetToken={layoutReset}
      onSizesChange={onPanelSizes}
    />
  );

  return (
    <LiveChordsProvider chords={runningChords}>
    <GradePreviewContext.Provider value={gradePreviewValue}>
    <div className={classes.root} data-testid="editor-workspace" data-shell="full" data-workspace={ui.workspace} data-layout={isTablet ? 'tablet' : undefined}>
      {header}
      {banner}
      <Box style={{ flex: 1, minHeight: 0, display: isTablet ? 'flex' : undefined }}>
        {isTablet && <WorkspaceBar active={ui.workspace} onChange={setWorkspace} variant="rail" chords={pageChords} />}
        <Box style={isTablet ? { flex: 1, minWidth: 0, minHeight: 0 } : { height: '100%', minHeight: 0 }}>
        {ui.workspace === 'media' ? (
          mediaPage
        ) : ui.workspace === 'deliver' ? (
          deliverPage
        ) : ui.workspace === 'color' ? (
          <LiveColorPage store={store} {...colorPageProps}>{desktopPanel}</LiveColorPage>
        ) : (
          desktopPanel
        )}
        </Box>
      </Box>
      {statusBar}
      {dialogs}
    </div>
    </GradePreviewContext.Provider>
    </LiveChordsProvider>
  );
}
