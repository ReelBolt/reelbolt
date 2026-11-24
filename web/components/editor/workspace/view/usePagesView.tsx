'use client';

import { Button, Box, Stack, MantineThemeProvider } from '@mantine/core';
import { selectItem } from '@/lib/editing/selection';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { PanelLayout } from '../../shell';
import classes from '../../shell/workspace.module.css';
import { enterViewerTool } from '@/lib/editing/viewer-transform';
import { type DeliverZoneTargets } from '../../deliver/DeliverWorkspace';
import { CutOverview } from '../../cut/CutWorkspace';
import { ColorSoloBanner } from '../../color/ColorWorkspace';
import { ScopesPanel } from '../../color/ScopesPanel';
import { useT } from '@/i18n';
import { LiveComposeProperties, LiveBusesPanel, LiveVoRecorderPanel, LiveMixerPanel, PlayheadComposeList, LiveCutWorkspace } from '../live-panels';
import type { TimelineZoneViewCtx } from './useTimelineZoneView';

/** What each page puts in the frame: Media, Deliver, Cut's overview, Compose, Audio and Colour. A view hook: it returns elements and keeps no state. */
export function usePagesView(ctx: TimelineZoneViewCtx) {
  const { store, seedStepResultId, setMediaBinsTarget, mediaZoneTargets, mediaView, setMediaDetailTarget, timeline, mediaTimelineOpen, setMediaTimelineOpen, appliedSizes, appliedFractions, layoutReset, onPanelSizes, deliverControlsTarget, deliverResultTarget, setDeliverControlsTarget, setDeliverResultTarget, deliverWorkspaceElement, ui, doc, paintFlags, parityOf, seek, projectId, timelineId, pausePlayback, readOnly, onSetProperty, activateViewerTool, drawMask, dispatch, applyBatch, setUi, onWindowTarget, getCanvas, gallery, captureStill, setGallery, referenceId, setReferenceId, wipe, setWipe, proxyUrlFor, probeFor, scopeStatsRef, attachViewerZone, fullscreenOn, viewerPortalTheme, viewerPicture, viewerTransport, flags, getMeters, latestRender, revision, flushForRender, onToggleTrack, composeViewerTool, targetedVideoTrackId, setSpeed, viewerSwitchOn, openSource, sourceShowing, mediaWorkspace, leftPanel } = ctx;
  const tx = useT('editor.editorWorkspace');
  // T-003: Media in the shared frame — bins | pool | viewer+metadata, timeline hidden until asked.
  const mediaPage = (
    <PanelLayout
      page="media"
      left={<div ref={setMediaBinsTarget} className={classes.mediaZone} data-testid="media-bins-zone" />}
      viewer={<div className={classes.mediaPoolZone} data-testid="media-pool-zone">{mediaZoneTargets ? mediaView(mediaZoneTargets) : null}</div>}
      right={<div ref={setMediaDetailTarget} className={classes.mediaZone} data-testid="media-detail-zone" />}
      timeline={timeline}
      timelineVisible={mediaTimelineOpen}
      timelineToggle={
        <Button size="compact-xs" fz="xs" variant="default" onClick={() => setMediaTimelineOpen((open) => !open)} data-testid="media-timeline-toggle">
          {mediaTimelineOpen ? tx('hideTimeline') : tx('showTimeline')}
        </Button>
      }
      appliedSizes={appliedSizes}
      appliedFractions={appliedFractions}
      resetToken={layoutReset}
      onSizesChange={onPanelSizes}
    />
  );
  const deliverZoneTargets: DeliverZoneTargets | null =
    deliverControlsTarget && deliverResultTarget ? { controls: deliverControlsTarget, result: deliverResultTarget } : null;
  const deliverPage = (
    <Box style={{ height: '100%', minHeight: 0 }} data-testid="deliver-fullpane">
      <Box style={{ height: '100%', minHeight: 0 }} data-testid="deliver-workspace">
        <PanelLayout
          page="deliver"
          left={<div ref={setDeliverControlsTarget} className={classes.deliverZone} data-testid="deliver-controls-zone" />}
          right={<div ref={setDeliverResultTarget} className={`${classes.deliverZone} ${classes.deliverZoneResult}`} data-testid="deliver-result-zone" />}
          appliedSizes={appliedSizes}
          appliedFractions={appliedFractions}
          resetToken={layoutReset}
          onSizesChange={onPanelSizes}
        />
      </Box>
      {deliverZoneTargets ? deliverWorkspaceElement(deliverZoneTargets) : null}
    </Box>
  );

  // T-003: Cut's overview strip is the page's own band, not the top of the left column.
  const cutOverview = ui.workspace === 'cut' ? (
    <CutOverview
      doc={doc}
      playheadFrame={ui.playheadFrame}
      selection={ui.selection}
      trackFlags={paintFlags}
      parityOf={parityOf}
      onSeek={seek}
      onSelect={(id) => store.setUi({ selection: id ? selectItem(doc, id) : [] })}
    />
  ) : undefined;

  // §5.5: Compose's right zone is the properties of the one selected list entry.
  const composeRightPanel = (
    <Box style={{ height: '100%', overflow: 'auto', background: 'var(--rb-ed-inspector-bg)' }}>
      <LiveComposeProperties store={store}
        doc={doc}
        projectId={projectId}
        timelineId={timelineId}
        onPreview3sStart={pausePlayback}
        readOnly={readOnly}
        selection={ui.selection}
        newId={newMarkerId}
        onSetProperty={onSetProperty}
        onSeek={seek}
        onActivateViewerTool={activateViewerTool}
        onDrawMask={drawMask}
        composeEntry={ui.composeEntry}
      />
    </Box>
  );

  // T-003: Audio's routing half lives in the right zone; the mixer keeps the left zone.
  const audioRightPanel = (
    <Stack gap={0} style={{ height: '100%', overflow: 'auto', background: 'var(--rb-ed-inspector-bg)' }}>
      <LiveBusesPanel store={store} doc={doc} readOnly={readOnly} playbackSpeed={ui.playbackSpeed} newId={newMarkerId} onOp={dispatch} onApply={applyBatch} />
      <LiveVoRecorderPanel store={store} doc={doc} projectId={projectId} readOnly={readOnly} newId={newMarkerId} onApply={applyBatch} />
    </Stack>
  );

  // §5.6/§8.2: Colour fills the frame's three zones. Nodes on the left, the viewer with the scopes
  // docked under it in the centre, the grade-tool tabs on the right.
  const colorPageProps = {
    doc,
    projectId,
    seedStepResultId,
    readOnly,
    selection: ui.selection,
    timelineId,
    onPreview3sStart: pausePlayback,
    newId: newMarkerId,
    onSetProperty,
    onOp: dispatch,
    onApply: applyBatch,
    onSelect: (id: string) => store.setUi({ selection: selectItem(store.getState().doc, id) }),
    onSeek: seek,
    gradeBypass: ui.gradeBypass,
    onGradeBypass: (bypass: boolean) => setUi({ gradeBypass: bypass }),
    onWindowTarget,
    onActivateWindowTool: () => enterViewerTool(store, 'window'),
    getCanvas,
    gallery,
    onCaptureStill: captureStill,
    onRemoveStill: (id: string) => {
      setGallery((g) => g.filter((s) => s.id !== id));
      if (referenceId === id) setReferenceId(null);
    },
    referenceId,
    onReference: setReferenceId,
    wipe,
    onWipe: setWipe,
    mediaUrlFor: proxyUrlFor,
    probeFor,
    getCurrentStats: () => scopeStatsRef.current,
  };
  // The Colour page's viewer zone is the full-screen target there too (picture, scopes and transport).
  const colorViewer = (
    <div className={classes.viewer} ref={attachViewerZone} data-fullscreen={fullscreenOn || undefined} data-testid="viewer-zone">
      <MantineThemeProvider theme={viewerPortalTheme}>
        {viewerPicture}
        <ColorSoloBanner />
        <ScopesPanel getCanvas={getCanvas} onStats={(data) => { scopeStatsRef.current = data.stats; }} />
        {viewerTransport}
      </MantineThemeProvider>
    </div>
  );

  const workspaceTools =
    ui.workspace === 'audio' ? (
      <Stack gap={0} style={{ height: '100%', overflow: 'auto', background: 'var(--rb-ed-inspector-bg)' }}>
        <LiveMixerPanel store={store}
          doc={doc}
          readOnly={readOnly}
                    flags={flags}
          getMeters={getMeters}
          lastRenderLoudness={latestRender?.loudness ?? null}
          onOp={dispatch}
          onApply={applyBatch}
          projectId={projectId}
          revision={revision}
          flushSave={flushForRender}
          onToggle={onToggleTrack}
          selection={ui.selection}
        />
      </Stack>
    ) : ui.workspace === 'deliver' ? (
      <Box style={{ height: '100%', overflow: 'auto', background: 'var(--rb-ed-inspector-bg)' }}>
        {deliverWorkspaceElement(null)}
      </Box>
    ) : ui.workspace === 'compose' ? (
      <Box style={{ height: '100%', overflow: 'auto', background: 'var(--rb-ed-inspector-bg)' }}>
        <PlayheadComposeList store={store}
          doc={doc}
          readOnly={readOnly}
          selection={ui.selection}
          inOut={ui.inOut}
          newId={newMarkerId}
          onApply={applyBatch}
          // The fresh store document, not the render's: "Add adjustment clip" selects the clip it has just added.
          onSelect={(id) => store.setUi({ selection: selectItem(store.getState().doc, id) })}
          composeEntry={ui.composeEntry}
          onComposeEntry={(entry) => store.setUi({ composeEntry: entry })}
          viewerTool={ui.viewerTool}
          onViewerTool={composeViewerTool}
        />
      </Box>
    ) : ui.workspace === 'cut' ? (
      <Box style={{ height: '100%', overflow: 'auto', background: 'var(--rb-ed-inspector-bg)' }}>
        <LiveCutWorkspace store={store}
          doc={doc}
          projectId={projectId}
          seedStepResultId={seedStepResultId}
          readOnly={readOnly}
                    playbackSpeed={ui.playbackSpeed}
          selection={ui.selection}
          trackId={targetedVideoTrackId}
          trackFlags={paintFlags}
          parityOf={parityOf}
          newItemId={newMarkerId}
          onSeek={seek}
          onSelect={(id) => store.setUi({ selection: id ? selectItem(doc, id) : [] })}
          onSetSpeed={setSpeed}
          onOp={dispatch}
          onApply={applyBatch}
          onOpenSource={viewerSwitchOn ? openSource : undefined}
          openSourceRef={sourceShowing ? ui.viewerSource : null}
        />
      </Box>
    ) : ui.workspace === 'media' ? (
      mediaWorkspace
    ) : (
      leftPanel
    );

  return { mediaPage, deliverPage, cutOverview, composeRightPanel, audioRightPanel, colorPageProps, colorViewer, workspaceTools };
}

export type PagesViewCtx = TimelineZoneViewCtx & ReturnType<typeof usePagesView>;
