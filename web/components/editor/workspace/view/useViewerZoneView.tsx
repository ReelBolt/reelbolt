'use client';

import { Group, Tooltip, Menu, Text, Button, Box, MantineThemeProvider } from '@mantine/core';
import { IconCamera, IconColorFilter, IconMinimize, IconMaximize, IconAdjustmentsHorizontal } from '@tabler/icons-react';
import { previousEdit, nextEdit, previousBefore, nextAfter } from '@/lib/editing/transport';
import { allMarkerFrames } from '@/lib/editing/markers/markers';
import { type PreviewQuality, storeQuality } from '@/lib/editing/preview';
import { LivePlayhead, PAINT_MAX_HZ } from '../../shell/LivePlayhead';
import { ViewerModeSwitch, SourceMonitorPicture, SourceMonitorTransport } from '../../viewer/SourceMonitor';
import classes from '../../shell/workspace.module.css';
import { PreviewToggles, ProgramViewer, TransportBar } from '../../viewer';
import { ViewingLutMenu } from '../../viewer/ViewingLutMenu';
import { TransformOverlay, TransformToolToggle, TransformTools } from '../../viewer/TransformOverlay';
import { FrameHandlesOverlay, FrameTools } from '../../viewer/FrameHandlesOverlay';
import { PageEmptyState } from '../../ui/PageEmptyState';
import { timelineHasClips } from '../../panels/TimelineSettingsPanel';
import { Preview3sOverlay, Preview3sButton } from '../../viewer/Preview3s';
import { classifyItem } from '@/lib/editing/preview/parity';
import { offersPreview3s } from '@/lib/editing/preview/preview3s';
import { EditorIcon } from '../../ui';
import { useT } from '@/i18n';
import type { SidePanelsViewCtx } from './useSidePanelsView';

/** The one viewer: the Source | Record switch, the viewer options, the picture, the transport and the zone that holds them. A view hook: it returns elements and keeps no state. */
export function useViewerZoneView(ctx: SidePanelsViewCtx) {
  const { store, viewerSwitchOn, sourceShowing, liveChordLabel, liveAria, sourceItem, saveStill, ui, setUi, isPhone, fullscreenSupported, fullscreenOn, toggleFullscreen, setSimpleChoice, forceFallback, simpleChoice, autoReasonText, audition, cancelAudition, readOnly, applyAudition, previewDoc, getPreviewDoc, flags, proxyUrlFor, pictureUrlFor, inputStageFor, viewingLut, gradeLutFor, gradeLutStatus, hdrKindFor, stabilizeFor, sourceWidthFor, proxyStatusFor, proxyVersion, getPlayRange, playbackPlan, readiness, retryPreview, prepareAllPreview, jumpToClip, onStats, onEnded, sessionRef, canvasRef, doc, setWorkspace, sourceSizeFor, projectId, previewItem, timelineId, pausePlayback, reference, wipe, firstChords, rate, endFrame, setSpeed, seek, setMarks, playInToOut, isTablet, attachViewerZone, viewerPortalTheme, monitor, placeChosen, simple } = ctx;
  const tx = useT('editor.editorWorkspace');
  // The one viewer. On Cut and Edit a Source | Record switch sits in its header; Source lays the clip over the SAME box
  // (the program viewer stays mounted underneath, inert, so its session and size are untouched) and swaps the transport.
  const viewerModeSwitch = viewerSwitchOn ? (
    <ViewerModeSwitch
      mode={sourceShowing ? 'source' : 'record'}
      onChange={(mode) => {
        if (mode === 'source') store.setUi({ viewerMode: 'source', playbackSpeed: 0 });
        else store.setUi({ viewerMode: 'record' });
      }}
      hint={liveChordLabel(tx('viewerSwitchHint'), 'toggleViewerMode')}
      keyShortcuts={liveAria('toggleViewerMode')}
      sourceName={sourceItem?.fileName ?? null}
    />
  ) : null;
  // The Simple shell keeps the whole-screen button and the preview menu; stills and the grade switch stay in the full editor
  // (Look has its own "show the original").
  const viewerOptions = (
    <Group gap={2} wrap="nowrap">
      {!simple && (
      <Tooltip label={tx('saveThisFrameAs')} openDelay={300}>
        <EditorIcon tooltip={false} icon={IconCamera} label={tx('saveStill')} onClick={() => void saveStill()} data-testid="save-still" />
      </Tooltip>
      )}
      {!simple && (
      <Tooltip label={ui.gradeBypass ? tx('gradesAreBypassedIn') : tx('beforeAfterBypassEvery')} openDelay={300}>
        <EditorIcon tooltip={false} icon={IconColorFilter}
          variant={ui.gradeBypass ? 'light' : 'subtle'}
          color={ui.gradeBypass ? 'orange' : 'gray'}
          label={ui.gradeBypass ? tx('gradeOff') : tx('gradeOn')}
          aria-pressed={ui.gradeBypass}
          onClick={() => setUi({ gradeBypass: !ui.gradeBypass })}
          data-testid="grade-bypass" />
      </Tooltip>
      )}
      {!isPhone && fullscreenSupported && (
        <Tooltip label={liveChordLabel(fullscreenOn ? tx('viewerFullscreenExit') : tx('viewerFullscreen'), 'viewerFullscreen')} /* eslint-disable-line i18next/no-literal-string -- action id, not copy */ openDelay={300}>
          <EditorIcon tooltip={false} icon={fullscreenOn ? IconMinimize : IconMaximize}
            variant={fullscreenOn ? 'light' : 'subtle'}
            label={fullscreenOn ? tx('viewerFullscreenExit') : tx('viewerFullscreen')}
            aria-pressed={fullscreenOn}
            aria-keyshortcuts={liveAria('viewerFullscreen')}
            onClick={toggleFullscreen}
            data-testid="viewer-fullscreen" />
        </Tooltip>
      )}
      <Menu position="top-end" withinPortal closeOnItemClick={false} width={240}>
        <Menu.Target>
          <EditorIcon icon={IconAdjustmentsHorizontal} label={tx('previewOptions')} />
        </Menu.Target>
        <Menu.Dropdown p="sm">
          <PreviewToggles
            playbackResolution={ui.playbackResolution}
            onPlaybackResolution={(v: PreviewQuality) => {
              setUi({ playbackResolution: v });
              storeQuality(v);
              setSimpleChoice(null);
            }}
            degraded={forceFallback}
            onDegraded={setSimpleChoice}
            degradedAvailable
            autoReason={simpleChoice === null ? autoReasonText : null}
          />
        </Menu.Dropdown>
      </Menu>
    </Group>
  );

  const viewerPicture = (
    <>
      {audition && (
        <Group gap="xs" p="xs" justify="space-between" wrap="wrap" role="status" data-testid="audition-banner" style={{ background: 'var(--mantine-color-dark-7)', color: 'var(--mantine-color-gray-0)', borderBottom: '1px solid var(--mantine-color-dark-4)' }}>
          <Text size="xs">{tx('playingTheResultOf', { label: audition.label })}</Text>
          <Group gap="xs">
            <Button size={isPhone ? 'md' : 'compact-xs'} fz="xs" variant="default" onClick={cancelAudition} data-testid="audition-cancel" style={isPhone ? { minHeight: 44 } : undefined}>
              {tx('cancel')}
            </Button>
            <Button size={isPhone ? 'md' : 'compact-xs'} fz="xs" disabled={readOnly} onClick={applyAudition} data-testid="audition-apply" style={isPhone ? { minHeight: 44 } : undefined}>
              {tx('apply')}
            </Button>
          </Group>
        </Group>
      )}
      <LivePlayhead store={store}>
        {(livePlayhead) => (
      <ProgramViewer
        doc={previewDoc}
        getDoc={getPreviewDoc}
        flags={flags}
        proxyUrlFor={proxyUrlFor}
        pictureUrlFor={pictureUrlFor}
        inputStageFor={inputStageFor}
        getViewingLut={viewingLut.getViewingLut}
        gradeLutFor={gradeLutFor}
        gradeLutStatus={gradeLutStatus}
        viewingLutVersion={viewingLut.spec?.key ?? null}
        hdrKindFor={hdrKindFor}
        stabilizeFor={stabilizeFor}
        sourceWidthFor={sourceWidthFor}
        proxyStatusFor={proxyStatusFor}
        proxyVersion={proxyVersion}
        getPlayRange={getPlayRange}
        suspended={sourceShowing}
        playheadFrame={livePlayhead}
        speed={ui.playbackSpeed}
        playbackResolution={playbackPlan.resolution}
        fitMaxLongSide={playbackPlan.fitMaxLongSide}
        forceFallback={forceFallback}
        simpleReason={simpleChoice === null ? autoReasonText : null}
        readiness={readiness}
        onRetryPreview={retryPreview}
        onPrepareAll={prepareAllPreview}
        onJumpToClip={jumpToClip}
        onSwitchToSimple={() => setSimpleChoice(true)}
        onFrame={(frame) => store.setUi({ playheadFrame: frame })}
        onStats={onStats}
        onEnded={onEnded}
        onSession={(session) => {
          sessionRef.current = session;
        }}
        onCanvas={(canvas) => {
          canvasRef.current = canvas;
        }}
        overlay={
          <>
            {ui.workspace === 'edit' && !timelineHasClips(doc) && (
              <Box style={{ position: 'absolute', inset: 0, zIndex: 2, display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--rb-ed-studio-bg)' }}>
                <PageEmptyState
                  page="edit"
                  // In the Simple shell the media list is already beside the viewer: there is no page to open.
                  action={simple ? undefined : { label: tx('openMedia'), onClick: () => setWorkspace('media'), testId: 'edit-empty-open-media' }}
                  testId="edit-empty"
                />
              </Box>
            )}
            <TransformOverlay store={store} sourceSizeFor={sourceSizeFor} readOnly={readOnly} />
            <FrameHandlesOverlay store={store} sourceSizeFor={sourceSizeFor} readOnly={readOnly} />
            <Preview3sOverlay />
          </>
        }
        lead={viewerModeSwitch}
        quiet={simple}
        tools={simple ? (
          <TransformTools store={store} sourceSizeFor={sourceSizeFor} readOnly={readOnly} />
        ) : (
          <>
            <FrameTools store={store} readOnly={readOnly} />
            {!isPhone && <ViewingLutMenu projectId={projectId} choice={viewingLut.choice} onChoose={viewingLut.setChoice} problem={viewingLut.problem} />}
            {!isPhone && <TransformToolToggle store={store} />}
            <TransformTools store={store} sourceSizeFor={sourceSizeFor} readOnly={readOnly} />
            {!isPhone && previewItem && offersPreview3s(classifyItem(previewItem, doc, { hdrKindFor, gradeLutStatus, stabilizeFor }).parity) && (
              <Preview3sButton projectId={projectId} timelineId={timelineId} atFrame={livePlayhead} readOnly={readOnly} onBeforeStart={pausePlayback} />
            )}
          </>
        )}
        gradeBypass={ui.gradeBypass}
        referenceOverlay={reference ? { dataUrl: reference.dataUrl, wipe } : null}
      />
        )}
      </LivePlayhead>
    </>
  );

  const viewerTransport = (
      <div className={classes.transport}>
        <LivePlayhead store={store} maxHz={PAINT_MAX_HZ}>
          {(livePlayhead) => (
        <TransportBar
          compact={isPhone}
          simple={simple}
          chords={firstChords}
          playheadFrame={livePlayhead}
          rate={rate}
          endFrame={endFrame}
          speed={ui.playbackSpeed}
          loop={ui.loop}
          inFrame={ui.inOut.inFrame}
          outFrame={ui.inOut.outFrame}
          onSeek={(f) => {
            setSpeed(0);
            seek(f);
          }}
          onSpeed={setSpeed}
          onStep={(d) => {
            setSpeed(0);
            seek(store.getState().ui.playheadFrame + d);
          }}
          onPrevEdit={() => {
            const f = previousEdit(doc, store.getState().ui.playheadFrame);
            if (f !== null) seek(f);
          }}
          onNextEdit={() => {
            const f = nextEdit(doc, store.getState().ui.playheadFrame);
            if (f !== null) seek(f);
          }}
          onPrevMarker={() => {
            const f = previousBefore(allMarkerFrames(doc), store.getState().ui.playheadFrame);
            if (f !== null) seek(f);
          }}
          onNextMarker={() => {
            const f = nextAfter(allMarkerFrames(doc), store.getState().ui.playheadFrame);
            if (f !== null) seek(f);
          }}
          onMarkIn={() => setMarks({ ...ui.inOut, inFrame: store.getState().ui.playheadFrame })}
          onMarkOut={() => setMarks({ ...ui.inOut, outFrame: store.getState().ui.playheadFrame + 1 })}
          onToggleLoop={() => setUi({ loop: !ui.loop })}
          onPlayInToOut={() => {
            playInToOut();
          }}
          trailing={isPhone || isTablet || simple ? viewerOptions : undefined}
        />
          )}
        </LivePlayhead>
        {!isPhone && !isTablet && !simple && viewerOptions}
      </div>
  );

  const viewer = (
    <div className={classes.viewer} ref={attachViewerZone} data-fullscreen={fullscreenOn || undefined} data-testid="viewer-zone">
      <MantineThemeProvider theme={viewerPortalTheme}>
      <div style={{ position: 'relative', flex: '1 1 0px', minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          {viewerPicture}
        </div>
        {sourceShowing && <SourceMonitorPicture monitor={monitor} />}
      </div>
      {/* Record's transport alone sets the height; Source's lies over it and fits it, so the picture box never changes on the switch and Record pays nothing for Source. */}
      <div style={{ position: 'relative', flex: '0 0 auto' }} data-testid="viewer-transport-slot">
        <div style={{ visibility: sourceShowing ? 'hidden' : 'visible' }} {...(sourceShowing ? { inert: true } : {})}>
          {viewerTransport}
        </div>
        {viewerSwitchOn && (
          <div style={{ position: 'absolute', inset: 0, visibility: sourceShowing ? 'visible' : 'hidden' }} {...(sourceShowing ? {} : { inert: true })}>
            <SourceMonitorTransport monitor={monitor} readOnly={readOnly} onPlace={placeChosen} chordLabel={liveChordLabel} chordAria={liveAria} />
          </div>
        )}
      </div>
      </MantineThemeProvider>
    </div>
  );

  return { viewerPicture, viewerTransport, viewer };
}

export type ViewerZoneViewCtx = SidePanelsViewCtx & ReturnType<typeof useViewerZoneView>;
