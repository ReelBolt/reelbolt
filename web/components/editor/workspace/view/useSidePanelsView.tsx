'use client';

import { Stack, Text, Box } from '@mantine/core';
import { IconPhotoVideo, IconBookmark, IconListDetails, IconWaveSine, IconTextCaption, IconBadgeCc, IconStack2, IconHierarchy2, IconMessageCircle, IconHistory, IconAdjustmentsHorizontal, IconWand, IconSettings, IconVersions, IconArrowsDiff } from '@tabler/icons-react';
import { linkedOpsForRetime } from '@/lib/editing/link';
import { selectItem } from '@/lib/editing/selection';
import { isRationaleMarkerId, newMarkerId } from '@/lib/editing/markers/markers';
import { mediaRefOf } from '@/lib/editing/preview';
import { framesToTimecode } from '@/lib/editing/time';
import { PlaceMenu } from '../../place/PlaceMenu';
import { originalContentUrl } from '@/lib/editing/api/media';
import { type PanelTab, PanelTabs } from '../../shell';
import { MediaView } from '../../hub/MediaView';
import type { MediaZoneTargets } from '../../media/MediaPool';
import { poolClipSpan, hasAudioStream, PAIRED_AUDIO_TRACK_NAME } from '@/lib/editing/media/pool-insert';
import type { Marks } from '@/lib/editing/media/in-out';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { HistoryPanel } from '../../panels/HistoryPanel';
import { EditIndexPanel } from '../../panels/EditIndexPanel';
import { TimelineSettingsPanel, timelineHasClips } from '../../panels/TimelineSettingsPanel';
import { RevisionHistoryPanel } from '../../panels/RevisionHistoryPanel';
import { TranscriptPanel } from '../../panels/TranscriptPanel';
import { AnalysePanel } from '../../panels/AnalysePanel';
import { TakeStackPanel } from '../../panels/TakeStackPanel';
import { ComparePanel } from '../../panels/ComparePanel';
import { ProposalPanel } from '../../panels/ProposalPanel';
import { phoneMoreToolTabs } from '../../phone/captions-logic';
import { decodeWhenReady } from '@/lib/editing/audio/decode-when-ready';
import { useT } from '@/i18n';
import { LiveMarkersPanel, LiveSubtitlesPanel, LiveStructurePanel, LiveReviewPanel, LiveInspectorPanel, ExtractAudioButton } from '../live-panels';
import type { ShellStateCtx } from '../useShellState';

/** The Media pool views and the Edit page's left and right tab groups. A view hook: it returns elements and keeps no state. */
export function useSidePanelsView(ctx: ShellStateCtx) {
  const { store, seedStepResultId, offeredSourceRef, rate, readOnly, placeChosen, projectId, placePoolSource, poolMarksRef, openSource, placePoolItem, mediaBinsTarget, mediaDetailTarget, markerRows, seek, addMarker, dispatch, doc, ui, selectedFileId, applyBatch, startAudition, timelineId, onSetProperty, proxyUrlFor, history, cursor, undo, redo, linkOptions, relinkOptions, editing, hdrKindFor, stabilizeFor, gradeLutStatus, probeFor, activateViewerTool, pool, extractAudio, assistHelper, setGhostState, revision, onRestore, refreshRevisions, leftTab, setLeftTab, rightTab, setRightTab } = ctx;
  const tx = useT('editor.editorWorkspace');
  // The Media workspace's clip actions: put the selected source (or its marked range) on the timeline.
  const poolClipActions = (item: MediaPoolItem, marks: Marks) => {
    offeredSourceRef.current = { item, marks };
    const span = poolClipSpan(item, rate, marks);
    const withSound = item.kind === 'video' && hasAudioStream(item) === true;
    return (
      <Stack gap={4} data-testid="media-clip-actions">
        <PlaceMenu disabled={readOnly} onPlace={(mode) => placeChosen(item, mode, marks)} />
        <Text size="xs" c="dimmed" data-testid="media-insert-length">
          {span.fromMarks ? tx('goesInAsThe') : tx('goesInAtFull')}: {framesToTimecode(span.durationFrames, rate)}
          {withSound ? tx('withItsSoundOn', { PAIRED_AUDIO_TRACK_NAME }) : '.'}
        </Text>
        {!span.lengthKnown ? (
          <Text size="xs" c="yellow.8" data-testid="media-length-unknown">
            {tx('lengthUnknownNote')}
          </Text>
        ) : null}
      </Stack>
    );
  };
  const mediaView = (zoneTargets: MediaZoneTargets | null) => (
    <MediaView
      projectId={projectId}
      clipActions={poolClipActions}
      onPlace={placePoolSource}
      placeDisabled={readOnly}
      readOnly={readOnly}
      onMarksChange={(mediaRef, marks) => {
        poolMarksRef.current.clear();
        poolMarksRef.current.set(mediaRef, marks);
      }}
      onSelectClip={zoneTargets === null ? (item) => { if (item) openSource(item.mediaRef); } : undefined}
      onCreateSubclip={(item, descriptor, marks) => placePoolItem(item, 'append', marks, descriptor.name)}
      createSubclipHint={tx('createSubclipHint')}
      zoneTargets={zoneTargets}
    />
  );
  // The Edit page's Media tab: the same pool in a narrower container (it reflows by container width).
  const mediaWorkspace = (
    <Box p="md" style={{ height: '100%', overflow: 'auto', background: 'var(--rb-ed-inspector-bg)' }}>
      {mediaView(null)}
    </Box>
  );
  const mediaZoneTargets: MediaZoneTargets | null =
    mediaBinsTarget ? { bins: mediaBinsTarget, detail: mediaDetailTarget } : null;

  const leftTabs: PanelTab[] = [
    {
      // R05.8: the pool is the first thing on the left of the Edit workspace, so a tile can be dragged straight onto the timeline.
      value: 'media',
      label: tx('media'),
      icon: IconPhotoVideo,
      testId: 'tab-media',
      content: mediaWorkspace,
    },
    {
      value: 'markers',
      label: tx('markers'),
      icon: IconBookmark,
      content: (
        <LiveMarkersPanel store={store}
          rows={markerRows}
          rate={rate}
                    onSeek={seek}
          onAdd={addMarker}
          onRename={(id, name) => !isRationaleMarkerId(id) && dispatch({ type: 'setMarker', markerId: id, patch: { name } }, tx('renameMarker'))}
          onRemove={(id) => !isRationaleMarkerId(id) && dispatch({ type: 'removeMarker', markerId: id }, tx('removeMarker'))}
          onSelectItem={(itemId) => store.setUi({ selection: selectItem(doc, itemId) })}
        />
      ),
    },
    {
      value: 'index',
      label: tx('edits'),
      icon: IconListDetails,
      content: <EditIndexPanel doc={doc} rate={rate} selection={ui.selection} onSeek={seek} onSelect={(id) => store.setUi({ selection: selectItem(doc, id) })} />,
    },
    {
      value: 'analyse',
      label: tx('analyse'),
      icon: IconWaveSine,
      testId: 'tab-analyse',
      content: <AnalysePanel doc={doc} projectId={projectId} readOnly={readOnly} focusFileId={selectedFileId} />,
    },
    {
      value: 'transcript',
      label: tx('transcript'),
      icon: IconTextCaption,
      testId: 'tab-transcript',
      content: <TranscriptPanel doc={doc} projectId={projectId} seedStepResultId={seedStepResultId} readOnly={readOnly} newItemId={newMarkerId} onApply={applyBatch} onAudition={startAudition} onSeek={seek} />,
    },
    {
      value: 'subtitles',
      label: tx('subtitles'),
      icon: IconBadgeCc,
      testId: 'tab-subtitles',
      content: (
        <LiveSubtitlesPanel store={store}
          doc={doc}
          projectId={projectId}
          timelineId={timelineId}
          seedStepResultId={seedStepResultId}
          readOnly={readOnly}
                    newItemId={newMarkerId}
          newTrackId={newMarkerId}
          onApply={applyBatch}
          onSetProperty={onSetProperty}
          onSeek={seek}
        />
      ),
    },
    {
      value: 'takes',
      label: tx('takes'),
      icon: IconStack2,
      testId: 'tab-takes',
      content: <TakeStackPanel doc={doc} projectId={projectId} seedStepResultId={seedStepResultId} readOnly={readOnly} onOp={dispatch} onApply={applyBatch} onAudition={startAudition} newItemId={newMarkerId} onSeek={seek} onSelect={(id) => store.setUi({ selection: selectItem(doc, id) })} />,
    },
    {
      value: 'structure',
      label: tx('structure'),
      icon: IconHierarchy2,
      testId: 'tab-structure',
      content: (
        <LiveStructurePanel store={store}
          doc={doc}
          readOnly={readOnly}
          selection={ui.selection}
                    playbackSpeed={ui.playbackSpeed}
          newId={newMarkerId}
          onOp={dispatch}
          onApply={applyBatch}
          onSelect={(id) => store.setUi({ selection: selectItem(doc, id) })}
          decodeEnvelope={(clip) => {
            const url = proxyUrlFor(clip) ?? (mediaRefOf(clip.assetRef) ? originalContentUrl(projectId, mediaRefOf(clip.assetRef) as string) : null);
            if (!url) return Promise.reject(new Error(tx('noMediaUrlFor', { name: clip.name })));
            return decodeWhenReady(url);
          }}
        />
      ),
    },
    {
      value: 'review',
      label: tx('comments'),
      icon: IconMessageCircle,
      testId: 'tab-review',
      content: <LiveReviewPanel store={store} projectId={projectId} timelineId={timelineId} rate={rate} inOut={ui.inOut} onSeek={seek} />,
    },
    {
      value: 'history',
      label: tx('history'),
      icon: IconHistory,
      content: <HistoryPanel history={history} cursor={cursor} onUndo={undo} onRedo={redo} onJumpTo={(c) => store.jumpTo(c)} />,
    },
  ];

  const rightTabs: PanelTab[] = [
    {
      value: 'inspector',
      flush: true,
      label: tx('inspector'),
      icon: IconAdjustmentsHorizontal,
      content: (
        <LiveInspectorPanel store={store}
          doc={doc}
          selection={ui.selection}
          rate={rate}
          readOnly={readOnly}
          onSetProperty={onSetProperty}
                    onOp={(op, label) => {
            // R08.1: a speed change keeps a linked picture and its sound the same length and in step.
            if (op.type === 'retime' && !store.getState().ui.editSeparately) return applyBatch(linkedOpsForRetime(store.getState().doc, op.itemId, op.playbackRate, op.recordOut, linkOptions()), label ?? 'Retime');
            return dispatch(op, label);
          }}
          onBatch={applyBatch}
          newId={newMarkerId}
          onSeek={seek}
          relinkOptions={relinkOptions}
          onNumericTrim={editing.applyNumericTrim}
          hdrKindFor={hdrKindFor}
          stabilizeFor={stabilizeFor}
          gradeLutStatus={gradeLutStatus}
          probeFor={probeFor}
          projectId={projectId}
          onActivateViewerTool={activateViewerTool}
          mediaClipActions={(item, trackKind) =>
            trackKind === 'video' ? (
              <ExtractAudioButton
                silent={(() => {
                  const ref = mediaRefOf(item.assetRef);
                  const source = ref ? pool?.find((p) => p.mediaRef === ref) : undefined;
                  return source ? hasAudioStream(source) === false : false;
                })()}
                disabled={readOnly || Boolean(item.hold)}
                onExtract={() => extractAudio(item.id)}
              />
            ) : null
          }
        />
      ),
    },
    {
      value: 'assist',
      label: tx('assist'),
      icon: IconWand,
      testId: 'tab-assist',
      content: <ProposalPanel key={assistHelper ?? 'any'} initialAgent={assistHelper} doc={doc} projectId={projectId} timelineId={timelineId} readOnly={readOnly} inOut={ui.inOut} onGhosts={setGhostState} onApply={applyBatch} onSeek={seek} />,
    },
    {
      value: 'settings',
      flush: true,
      label: tx('settings'),
      icon: IconSettings,
      content: <TimelineSettingsPanel settings={doc.settings} hasClips={timelineHasClips(doc)} readOnly={readOnly} onChange={(patch) => dispatch({ type: 'setTimelineSettings', patch }, tx('changeTimelineSettings'))} />,
    },
    {
      value: 'revisions',
      flush: true,
      label: tx('versions'),
      icon: IconVersions,
      content: <RevisionHistoryPanel projectId={projectId} timelineId={timelineId} currentRevision={revision} readOnly={readOnly} onRestore={onRestore} refreshKey={refreshRevisions} />,
    },
    {
      value: 'compare',
      flush: true,
      label: tx('compare'),
      icon: IconArrowsDiff,
      testId: 'tab-compare',
      content: <ComparePanel doc={doc} projectId={projectId} timelineId={timelineId} onSeek={seek} />,
    },
  ];

  const leftPanel = <PanelTabs label={tx('timelinePanels')} value={leftTab} onChange={setLeftTab} tabs={leftTabs} />;
  // The Captions sheet's "More tools" on a phone: the same panels minus the desktop-only ones (x1).
  const phoneMoreTabs = phoneMoreToolTabs(leftTabs);
  const phoneMoreTools = <PanelTabs label={tx('moreTools')} value={phoneMoreTabs.some((t) => t.value === leftTab) ? leftTab : (phoneMoreTabs[0]?.value ?? null)} onChange={setLeftTab} tabs={phoneMoreTabs} />;
  const rightPanel = <PanelTabs label={tx('clipPanels')} value={rightTab} onChange={setRightTab} tabs={rightTabs} />;

  return { mediaView, mediaWorkspace, mediaZoneTargets, leftPanel, phoneMoreTools, rightPanel };
}

export type SidePanelsViewCtx = ShellStateCtx & ReturnType<typeof useSidePanelsView>;
