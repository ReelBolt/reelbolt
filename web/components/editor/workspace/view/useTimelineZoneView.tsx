'use client';

import { Tooltip, Stack, Group, Button, Slider, Box } from '@mantine/core';
import { IconPointer, IconScissors, IconHandStop, IconArrowBackUp, IconArrowForwardUp, IconCut, IconMagnet, IconMagnetOff, IconZoomOut, IconZoomIn, IconArrowsHorizontal, IconArrowsMinimize, IconTrash } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { type EditorTool } from '@/lib/editing/store';
import { captionTracks } from '@/lib/editing/subtitles/delivery';
import { linkIdOf, linkedOpsForMove, linkedOpsForBlade } from '@/lib/editing/link';
import { selectMarquee } from '@/lib/editing/selection';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { ClipStrip } from '../../phone/ClipStrip';
import { LivePlayhead, PAINT_MAX_HZ } from '../../shell/LivePlayhead';
import { framesToTimecode } from '@/lib/editing/time';
import { badgeMenuItems } from '../../timeline/badgeMenu';
import classes from '../../shell/workspace.module.css';
import { TimelineCanvas } from '../../timeline/TimelineCanvas';
import { EventsLaneMenu } from '../../timeline/EventsLaneMenu';
import { TrackHeaders } from '../../timeline/TrackHeaders';
import { LinkMigrationBanner } from '../../timeline/LinkMigrationBanner';
import { TrimModeControl, EditSeparatelyToggle, SelectMenu, AddAtPlayheadMenu } from '../../timeline/TimelineMenus';
import { FrameSteppersMenu, FrameSteppers } from '../../timeline/FrameSteppers';
import { type DeliverZoneTargets, DeliverWorkspace } from '../../deliver/DeliverWorkspace';
import { RemoveSilenceButton } from '../../panels/RemoveSilenceDialog';
import { CutToBeatButton } from '../../timeline/CutToBeatButton';
import { gapInfoFor } from '@/lib/editing/gaps';
import { type KeyAction, displayChord, ariaChord } from '@/lib/editing/keymap';
import { showRecordFirst } from '@/lib/editing/commands';
import { EditorIcon } from '../../ui';
import { useT } from '@/i18n';
import { IS_MAC } from '../helpers';
import { LiveClipActionMenu } from '../live-panels';
import type { ViewerZoneViewCtx } from './useViewerZoneView';

/** The timeline pane: toolbar, canvas, track headers and the phone's clip strip. A view hook: it returns elements and keeps no state. */
export function useTimelineZoneView(ctx: ViewerZoneViewCtx) {
  const { store, chords, ui, setTool, doc, isPhone, timelineDoc, paintFlags, parityOf, rationaleFrames, clipArt, artVersion, timelineView, busyClipIds, badgesOf, jobOf, editing, revealAdjustment, onJobClick, readOnly, setSpeed, seek, onSelect, applyBatch, linkOptions, commitTrim, timelineId, pool, toolbarRef, toolbarFold, isTablet, liveChordLabel, undo, liveAria, cursor, redo, history, recordFirst, bladeAtPlayhead, extractSelection, liftSelection, closeGapBefore, closeTrackGaps, nudgeSelection, setUi, setSilenceOpen, setBeatsOpen, beatOverlay, showBeats, setShowBeats, eventsLane, zoomTo, zoomToFit, timelineBodyRef, onToggleTrack, onTarget, onAddTrack, onRemoveTrack, onSetTrackProperty, onMoveTrack, onAddTrackAt, setTimelineView, ghostState, beatGrid, renderRegions, snap, snapSource, badgeTitle, jobsAnimating, rate, dispatch, dropPoolItem, setBadgeMenu, openInspector, planTrim, projectId, revision, dirty, renders, rendersLoading, refreshRenders, selectedClip, setMarks, flushForRender, setWorkspace, simple, setSimplePlace } = ctx;
  const tx = useT('editor.editorWorkspace');
  const tBadges = useT('editor-timeline.badges');
  const tS = useT('editor-simple.timeline');
  // T-007: the tooltip shows the LIVE chord (the tool's keymap action), and the button states it to AT.
  const toolButton = (tool: EditorTool, label: string, action: KeyAction, Icon: typeof IconPointer) => {
    const chord = chords[action][0];
    return (
      <Tooltip label={chord ? `${label} (${displayChord(chord, IS_MAC)})` : label} openDelay={300} position="bottom" events={{ hover: true, focus: true, touch: true }}>
        <EditorIcon
          icon={Icon}
          tooltip={false}
          variant={ui.tool === tool ? 'default' : 'subtle'}
          color={ui.tool === tool ? undefined : 'gray'}
          label={tx('tool', { label })}
          aria-pressed={ui.tool === tool}
          aria-keyshortcuts={chord ? ariaChord(chord, IS_MAC) : undefined}
          onClick={() => setTool(tool)}
          data-testid={`tool-${tool}`}
        />
      </Tooltip>
    );
  };

  const selectedSpans = doc.tracks.flatMap((t) => t.items).filter((i) => ui.selection.includes(i.id));
  const clipStrip = isPhone ? (
    <LivePlayhead store={store} maxHz={PAINT_MAX_HZ}>
      {(livePlayhead) => (
        <ClipStrip
          doc={timelineDoc}
          playheadFrame={livePlayhead}
          selection={ui.selection}
          inFrame={ui.inOut.inFrame}
          outFrame={ui.inOut.outFrame}
          pixelsPerFrame={ui.pixelsPerFrame}
          trackFlags={paintFlags}
          parityOf={parityOf}
          rationaleFrames={rationaleFrames}
          clipArt={clipArt}
          artVersion={artVersion}
          view={timelineView}
          isLinked={(item) => linkIdOf(item) !== null}
          busyClipIds={busyClipIds}
          badgesOf={badgesOf}
          jobOf={jobOf}
          onClipBadge={({ itemId, kind }) => {
            if (kind === 'more') {
              store.setUi({ selection: [itemId] });
              editing.openMenu(window.innerWidth / 2, window.innerHeight / 2);
            } else revealAdjustment(itemId, kind);
          }}
          onClipJob={onJobClick}
          readOnly={readOnly}
          onSeek={(f) => {
            setSpeed(0);
            seek(f);
          }}
          onSelect={onSelect}
          onZoom={(pixelsPerFrame) => store.setUi({ pixelsPerFrame })}
          onClipMenu={({ clientX, clientY }) => editing.openMenu(clientX, clientY)}
          onMoveItem={(itemId, toTrackId, toRecordIn) => applyBatch(linkedOpsForMove(store.getState().doc, [{ itemId, toTrackId, toRecordIn }], linkOptions()), tx('historyMove'))}
          onTrimItem={commitTrim}
        />
      )}
    </LivePlayhead>
  ) : null;

  const timeline = (
    <Stack gap={0} style={{ height: '100%', minHeight: 0 }} data-testid="timeline-pane">
      <LinkMigrationBanner timelineId={timelineId} doc={doc} pool={pool} readOnly={readOnly} onApply={applyBatch} />
      {/* The Simple shell's toolbar: select or split, undo and redo, split here, delete, snapping and zoom. Every other
          timeline tool keeps its chord and its place in the full editor. */}
      {simple && (
      <div className={classes.toolbar} data-testid="timeline-toolbar" data-simple="true">
        <Group gap={6} wrap="nowrap">
          <div className={classes.toolGroup}>
            {toolButton('select', tx('toolSelect'), 'toolSelect', IconPointer) /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
            {toolButton('blade', tx('toolBlade'), 'toolBlade', IconScissors) /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
          </div>
          <div className={classes.toolDivider} />
          <Tooltip label={liveChordLabel(tx('undo2'), 'undo')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={IconArrowBackUp} onClick={undo} label={tx('undo2')} aria-keyshortcuts={liveAria('undo')} data-testid="undo" disabled={readOnly || cursor === 0} />
          </Tooltip>
          <Tooltip label={liveChordLabel(tx('redo'), 'redo')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={IconArrowForwardUp} onClick={redo} label={tx('redo')} aria-keyshortcuts={liveAria('redo')} data-testid="redo" disabled={readOnly || cursor >= history.length} />
          </Tooltip>
          <div className={classes.toolDivider} />
          <Tooltip label={liveChordLabel(tx('splitAtPlayhead'), 'bladeAtPlayhead')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={IconCut} onClick={bladeAtPlayhead} label={tx('splitAtPlayhead')} aria-keyshortcuts={liveAria('bladeAtPlayhead')} data-testid="blade-at-playhead" disabled={readOnly} />
          </Tooltip>
          <Tooltip label={liveChordLabel(tS('deleteSelected'), 'deleteSelection')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={IconTrash} onClick={extractSelection} label={tS('deleteSelected')} aria-keyshortcuts={liveAria('deleteSelection')} data-testid="simple-delete" disabled={readOnly || ui.selection.length === 0} />
          </Tooltip>
          <Tooltip label={liveChordLabel(ui.snapping ? tx('snappingOnN') : tx('snappingOffN'), 'toggleSnapping')} /* eslint-disable-line i18next/no-literal-string -- action id, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={ui.snapping ? IconMagnet : IconMagnetOff}
              variant={ui.snapping ? 'light' : 'subtle'}
              color={ui.snapping ? 'violet' : 'gray'}
              onClick={() => setUi({ snapping: !ui.snapping })}
              label={tx('snapping')}
              aria-pressed={ui.snapping}
              data-testid="snap-toggle" data-snapping={ui.snapping ? 'on' : 'off'} />
          </Tooltip>
        </Group>
        <Group gap={4} wrap="nowrap">
          <EditorIcon icon={IconZoomOut} onClick={() => zoomTo(ui.pixelsPerFrame / 1.5)} label={tx('zoomOut')} data-testid="zoom-out" />
          <EditorIcon icon={IconZoomIn} onClick={() => zoomTo(ui.pixelsPerFrame * 1.5)} label={tx('zoomIn')} data-testid="zoom-in" />
          <Tooltip label={tx('fitTheWholeTimeline')} openDelay={300}>
            <EditorIcon tooltip={false} icon={IconArrowsHorizontal} onClick={zoomToFit} label={tx('zoomToFit')} data-testid="zoom-fit" />
          </Tooltip>
        </Group>
      </div>
      )}
      {/* The phone has no timeline toolbar: the action bar and the clip strip carry its actions (RM.4, RM.5). */}
      {!isPhone && !simple && (
      <div ref={toolbarRef} className={classes.toolbar} data-testid="timeline-toolbar" data-fold={toolbarFold.labels ? 'labels' : toolbarFold.zoomSlider ? 'zoom-slider' : toolbarFold.steppers ? 'steppers' : toolbarFold.trim ? 'trim' : 'none'} data-wrap={isTablet ? 'true' : undefined}>
        <Group gap={6} wrap="nowrap">
          <span className={classes.toolbarBreak} aria-hidden />
          <div className={classes.toolGroup}>
            {toolButton('select', tx('toolSelect'), 'toolSelect', IconPointer) /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
            {toolButton('blade', tx('toolBlade'), 'toolBlade', IconScissors) /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
            {toolButton('hand', tx('toolPan'), 'toolHand', IconHandStop) /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
          </div>
          <div className={classes.toolDivider} />
          <Tooltip label={liveChordLabel(tx('undo2'), 'undo')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={IconArrowBackUp} onClick={undo} label={tx('undo2')} aria-keyshortcuts={liveAria('undo')} data-testid="undo" disabled={readOnly || cursor === 0} />
          </Tooltip>
          <Tooltip label={liveChordLabel(tx('redo'), 'redo')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={IconArrowForwardUp} onClick={redo} label={tx('redo')} aria-keyshortcuts={liveAria('redo')} data-testid="redo" disabled={readOnly || cursor >= history.length} />
          </Tooltip>
          <div className={classes.toolDivider} />
          <Tooltip label={liveChordLabel(tx('splitAtPlayhead'), 'bladeAtPlayhead')} /* eslint-disable-line i18next/no-literal-string -- action ids, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={IconCut} onClick={recordFirst(bladeAtPlayhead)} label={tx('splitAtPlayhead')} aria-keyshortcuts={liveAria('bladeAtPlayhead')} data-testid="blade-at-playhead" disabled={readOnly} />
          </Tooltip>
          <TrimModeControl value={ui.trimMode} onChange={(trimMode) => store.setUi({ trimMode })} folded={isTablet || toolbarFold.trim} />
          <EditSeparatelyToggle value={ui.editSeparately} onChange={(editSeparately) => store.setUi({ editSeparately })} />
          <LiveClipActionMenu store={store}
            selectedItems={selectedSpans}
                        readOnly={readOnly}
            onDelete={recordFirst(extractSelection)}
            onLift={recordFirst(liftSelection)}
            gaps={gapInfoFor(doc, ui.selection)}
            onCloseGap={recordFirst(closeGapBefore)}
            onCloseGaps={recordFirst(closeTrackGaps)}
            onSplit={recordFirst(bladeAtPlayhead)}
            onNudge={recordFirst(nudgeSelection)}
            extraActions={editing.items()}
          />
          {toolbarFold.steppers ? (
            <FrameSteppersMenu clipEnabled={!readOnly && ui.selection.length > 0} onNudge={recordFirst(nudgeSelection)} onPlayhead={recordFirst(editing.stepPlayhead)} />
          ) : (
            <FrameSteppers clipEnabled={!readOnly && ui.selection.length > 0} onNudge={recordFirst(nudgeSelection)} onPlayhead={recordFirst(editing.stepPlayhead)} />
          )}
          <Tooltip label={tx('closeEveryGapBetween')} openDelay={300}>
            <Button
              size="compact-sm"
              fz="xs"
              variant="subtle"
              color="gray"
              disabled={readOnly}
              onClick={() => {
                const onTrack = ui.selection.length > 0 ? doc.tracks.find((t) => t.items.some((i) => i.id === ui.selection[0]))?.id : ui.targetedTrackIds[0];
                showRecordFirst(store);
                if (onTrack) editing.closeAllGaps(onTrack);
                else notifications.show({ color: 'yellow', title: tx('pickATrack'), message: tx('selectAClipOr') });
              }}
              data-testid="close-all-gaps"
              aria-label={tx('closeAllGaps')}
            >
              {toolbarFold.labels ? <IconArrowsMinimize size={15} stroke={1.7} /> : tx('closeAllGaps')}
            </Button>
          </Tooltip>
          {editing.selectedGap && (
            <Button size="compact-sm" fz="xs" variant="light" color="yellow" disabled={readOnly} onClick={recordFirst(editing.deleteSelection)} data-testid="close-selected-gap">
              {tx('closeSelectedGap')}
            </Button>
          )}
          <Tooltip label={liveChordLabel(ui.snapping ? tx('snappingOnN') : tx('snappingOffN'), 'toggleSnapping')} /* eslint-disable-line i18next/no-literal-string -- action id, not copy */ openDelay={300}>
            <EditorIcon tooltip={false} icon={ui.snapping ? IconMagnet : IconMagnetOff}
              variant={ui.snapping ? 'light' : 'subtle'}
              color={ui.snapping ? 'violet' : 'gray'}
              onClick={() => setUi({ snapping: !ui.snapping })}
              label={tx('snapping')}
              aria-pressed={ui.snapping}
              data-testid="snap-toggle" data-snapping={ui.snapping ? 'on' : 'off'} />
          </Tooltip>
          <RemoveSilenceButton iconOnly={toolbarFold.labels} disabled={readOnly} onOpen={recordFirst(() => setSilenceOpen(true))} />
          <CutToBeatButton iconOnly={toolbarFold.labels} disabled={readOnly} onOpen={recordFirst(() => setBeatsOpen(true))} beatsReady={beatOverlay.ready} showBeats={showBeats} onToggleBeats={() => setShowBeats((v) => !v)} />
          <EventsLaneMenu controller={eventsLane} disabled={readOnly} />
          <SelectMenu store={store} />
          <AddAtPlayheadMenu store={store} disabled={readOnly} onApply={recordFirst(applyBatch)} />
        </Group>
        <Group gap={4} wrap="nowrap">
          <EditorIcon icon={IconZoomOut} onClick={() => zoomTo(ui.pixelsPerFrame / 1.5)} label={tx('zoomOut')} data-testid="zoom-out" />
          {!toolbarFold.zoomSlider && <Slider
            w={120}
            visibleFrom="sm"
            size="xs"
            color="gray"
            label={null}
            min={Math.log(0.01)}
            max={Math.log(64)}
            step={0.01}
            value={Math.log(ui.pixelsPerFrame)}
            onChange={(v) => zoomTo(Math.exp(v))}
            aria-label={tx('timelineZoom')}
            thumbLabel="Timeline zoom"
          />}
          <EditorIcon icon={IconZoomIn} onClick={() => zoomTo(ui.pixelsPerFrame * 1.5)} label={tx('zoomIn')} data-testid="zoom-in" />
          <Tooltip label={tx('fitTheWholeTimeline')} openDelay={300}>
            <EditorIcon tooltip={false} icon={IconArrowsHorizontal} onClick={zoomToFit} label={tx('zoomToFit')} data-testid="zoom-fit" />
          </Tooltip>
        </Group>
      </div>
      )}
      {isPhone ? (
        // The phone clip strip replaces the lanes (RM.4) and is the whole timeline pane: it grows with the strip area.
        <div style={{ flex: '1 1 auto', minHeight: 'clamp(200px, 30dvh, 320px)', display: 'flex', flexDirection: 'column' }}>{clipStrip}</div>
      ) : (
      <div className={classes.timelineBody} ref={timelineBodyRef}>
        <TrackHeaders
          doc={timelineDoc}
          width={240}
          ui={{ playheadFrame: ui.playheadFrame, selection: new Set(ui.selection), inFrame: ui.inOut.inFrame, outFrame: ui.inOut.outFrame, trackFlags: paintFlags, parityOf, rationaleFrames }}
          targetedTrackIds={ui.targetedTrackIds}
          onToggle={onToggleTrack}
          onTarget={onTarget}
          // The Simple shell's headers carry the name and the switches; adding, removing, renaming and reordering
          // tracks and the view options are the full editor's.
          onAddTrack={readOnly || simple ? undefined : onAddTrack}
          onRemoveTrack={readOnly || simple ? undefined : onRemoveTrack}
          onSetTrackProperty={readOnly || simple ? undefined : onSetTrackProperty}
          onMoveTrack={readOnly || simple ? undefined : onMoveTrack}
          onAddTrackAt={readOnly || simple ? undefined : onAddTrackAt}
          view={timelineView}
          onViewChange={simple ? undefined : setTimelineView}
          eventsLane={eventsLane.header}
        />
        <Box style={{ flex: 1, minWidth: 0 }}>
          {eventsLane.menu}
          <LivePlayhead store={store} maxHz={PAINT_MAX_HZ}>
            {(livePlayhead) => (
          <TimelineCanvas
            doc={timelineDoc}
            ghosts={ghostState?.ghosts}
            playheadFrame={livePlayhead}
            selection={ui.selection}
            inFrame={ui.inOut.inFrame}
            outFrame={ui.inOut.outFrame}
            pixelsPerFrame={ui.pixelsPerFrame}
            scrollFrame={ui.scrollFrame}
            tool={ui.tool}
            trackFlags={paintFlags}
            parityOf={parityOf}
            rationaleFrames={rationaleFrames}
            beatGrid={beatGrid}
            eventsLane={eventsLane.laneProps}
            renderRegions={renderRegions}
            clipArt={clipArt}
            artVersion={artVersion}
            view={timelineView}
            snap={snap}
            snapSource={snapSource}
            onSeek={(f) => {
              setSpeed(0);
              seek(f);
            }}
            onSelect={onSelect}
            isLinked={(item) => linkIdOf(item) !== null}
            busyClipIds={busyClipIds}
            badgesOf={badgesOf}
            badgeTitle={badgeTitle}
            jobOf={jobOf}
            jobsAnimating={jobsAnimating}
            onClipJob={onJobClick}
            onClipBadge={({ itemId, kind }) => {
              if (kind === 'more') {
                store.setUi({ selection: [itemId] });
                const r = document.querySelector('[data-testid="timeline-canvas"]')?.getBoundingClientRect();
                editing.openMenu((r?.left ?? 0) + 40, (r?.top ?? 0) + 40);
              } else revealAdjustment(itemId, kind);
            }}
            onMarquee={(rect) => {
              editing.setSelectedGap(null);
              store.setUi({ selection: selectMarquee(doc, rect) });
            }}
            onBlade={(itemId, atFrame) => {
              const label = tx('bladeAt2', { v1: framesToTimecode(atFrame, rate) });
              if (store.getState().ui.editSeparately) dispatch({ type: 'blade', itemId, atFrame, newItemId: newMarkerId() }, label);
              else applyBatch(linkedOpsForBlade(store.getState().doc, itemId, atFrame, newMarkerId(), linkOptions()).ops, label);
            }}
            onMoveItem={(itemId, toTrackId, toRecordIn, separately) => {
              if (separately || store.getState().ui.editSeparately) dispatch({ type: 'move', itemId, toTrackId, toRecordIn }, tx('historyMove'));
              else applyBatch(linkedOpsForMove(store.getState().doc, [{ itemId, toTrackId, toRecordIn }], linkOptions()), 'Move');
            }}
            onPoolDrop={readOnly ? undefined : dropPoolItem}
            trimMode={ui.trimMode}
            seamControls={{ rate, newId: newMarkerId, readOnly, onApply: applyBatch }}
            selectedGap={editing.selectedGap}
            onSelectGap={editing.setSelectedGap}
            onClipContextMenu={({ clientX, clientY, itemId, badge, area, frame }) => {
              // Menu items are built here, in the event handler, not during render (they close over refs).
              const item = badge && badge !== 'more' && itemId ? doc.tracks.flatMap((tr) => tr.items).find((i) => i.id === itemId) : undefined;
              if (item && badge && badge !== 'more') {
                setBadgeMenu({ x: clientX, y: clientY, items: badgeMenuItems(item, doc, badge, { t: tBadges, readOnly, open: revealAdjustment, apply: (ops, label) => void editing.apply(ops, label) }) });
              } else editing.openMenu(clientX, clientY, area && frame !== undefined ? { area, frame } : undefined);
            }}
            onItemDoubleClick={(itemId) => {
              store.setUi({ selection: [itemId] });
              openInspector(false);
            }}
            planTrim={planTrim}
            onTrimItem={commitTrim}
            onZoom={(pixelsPerFrame, anchorFrame) => {
              // Keep the anchor frame under the pointer.
              const anchorX = (anchorFrame - ui.scrollFrame) * ui.pixelsPerFrame;
              const scrollFrame = Math.max(0, anchorFrame - anchorX / pixelsPerFrame);
              store.setUi({ pixelsPerFrame, scrollFrame });
            }}
            onScroll={(scrollFrame) => store.setUi({ scrollFrame })}
          />
            )}
          </LivePlayhead>
        </Box>
      </div>
      )}
    </Stack>
  );

  // `simpleExport`: the Simple shell's Export place asks for the one-column form of the same workspace.
  const deliverWorkspaceElement = (zoneTargets: DeliverZoneTargets | null, simpleExport?: { onMoreOptions: () => void }) => (
    <DeliverWorkspace
      projectId={projectId}
      timelineId={timelineId}
      revision={revision}
      dirty={dirty}
      readOnly={readOnly}
      inOut={ui.inOut}
      renders={renders}
      rendersLoading={rendersLoading}
      refreshRenders={refreshRenders}
      selectedClip={selectedClip}
      onSetInOut={(range) => setMarks({ inFrame: range.inFrame, outFrame: range.outFrame })}
      timelineSize={doc.settings}
      doc={doc}
      flushSave={flushForRender}
      captionDelivery={{ tracks: captionTracks(doc) }}
      zoneTargets={zoneTargets}
      onOpenEdit={() => (simple ? setSimplePlace('edit') : setWorkspace('edit'))}
      simple={simpleExport}
    />
  );

  return { timeline, deliverWorkspaceElement };
}

export type TimelineZoneViewCtx = ViewerZoneViewCtx & ReturnType<typeof useTimelineZoneView>;
