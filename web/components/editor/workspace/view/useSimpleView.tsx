'use client';

import React from 'react';
import { Box } from '@mantine/core';
import { newGuid } from '@/lib/editing/ids';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { GradePreviewContext } from '@/lib/editing/color/grade-preview';
import { editorHubHref } from '@/lib/editing/open-timeline';
import { PendingResults } from '../../background/PendingResults';
import { TimelineSettingsPanel, timelineHasClips } from '../../panels/TimelineSettingsPanel';
import { ProposalPanel } from '../../panels/ProposalPanel';
import { AudioSheet } from '../../phone/AudioSheet';
import { LookSheetBody, TitlesSection } from '../../phone/LookSheet';
import phone from '../../phone/phone.module.css';
import { LiveChordsProvider } from '../../prefs/live-chords';
import { AskBox } from '../../simple/AskBox';
import { ShellSwitch } from '../../simple/ShellSwitch';
import { SimpleSelection } from '../../simple/SimpleSelection';
import { SimpleTasks, type SimpleTaskId } from '../../simple/SimpleTasks';
import { SimpleWorkspace } from '../../simple/SimpleWorkspace';
import { useT } from '@/i18n';
import { LiveSubtitlesPanel, LiveVoRecorderPanel } from '../live-panels';
import type { ChromeViewCtx } from './useChromeView';

/**
 * The Simple shell: one page (Edit) under three places. It builds no tool of its own: the viewer, the timeline, the
 * media list, the notices and the dialogs are the full editor's nodes, and each panel body is one the editor already
 * has (the phone's clip, look and sound bodies, the captions and suggestions panels, the timeline's settings, the
 * Deliver workspace in its one-column form). A view hook: it returns elements and keeps no state.
 */
export function useSimpleView(ctx: ChromeViewCtx) {
  const { store, sync, seedStepResultId, simple, shell, setShell, simplePlace, setSimplePlace, simpleView, setSimpleView, askSeed, setAskSeed, openInFull, simpleIntroOpen, dismissIntro, isTablet, doc, save, projectId, timelineId, readOnly, ui, phoneActions, seek, getCanvas, applyBatch, dispatch, onSetProperty, flags, onToggleTrack, viewerPicture, viewerTransport, timeline, banner, dialogs, busyGuard, mediaWorkspace, deliverWorkspaceElement, attachViewerZone, fullscreenOn, viewerPortalTheme, runningChords, gradePreviewValue, setSilenceOpen, setBeatsOpen, setCheatsheetOpen, setGhostState } = ctx;
  const tS = useT('editor-simple.panel');
  const tx = useT('editor.editorWorkspace');
  // Once the suggestions panel has been opened it stays mounted (hidden under another place), so its draft survives.
  const [aiOpened, setAiOpened] = React.useState(false);
  const aiActive = simple && simplePlace === 'edit' && simpleView === 'ask';
  if (aiActive && !aiOpened) setAiOpened(true);
  if (!simple || shell !== 'simple') return { simpleShell: null };

  const selected = doc.tracks.flatMap((track) => track.items).filter((item) => ui.selection.includes(item.id));
  const runTask = (task: SimpleTaskId) => {
    if (task === 'silence') return setSilenceOpen(true);
    if (task === 'beats') return setBeatsOpen(true);
    if (task === 'tighten') {
      // Date.now() only makes the panel's key new, so the same words asked twice start from a fresh panel.
      setAskSeed({ agent: 'recut', brief: '', nonce: Date.now() });
      return setSimpleView('ask');
    }
    setSimpleView(task);
  };

  // The side panel: which body, under which heading. Look and Export are places; the rest are views of Edit.
  let panelTitle: string;
  let panel: React.ReactNode;
  let back = false;
  if (simplePlace === 'look') {
    panelTitle = tS('look');
    panel = (
      <LookSheetBody
        panel
        actions={phoneActions}
        fix={{ getDoc: () => store.getState().doc, getPlayhead: () => store.getState().ui.playheadFrame, seek, getCanvas, apply: applyBatch, newId: newGuid }}
      />
    );
  } else if (simplePlace === 'export') {
    panelTitle = tS('export');
    panel = deliverWorkspaceElement(null, { onMoreOptions: () => openInFull({ workspace: 'deliver' }) });
  } else if (simpleView === 'captions') {
    panelTitle = tS('captions');
    back = true;
    panel = (
      <Box p="sm">
      <LiveSubtitlesPanel store={store} doc={doc} projectId={projectId} timelineId={timelineId} seedStepResultId={seedStepResultId} readOnly={readOnly} newItemId={newMarkerId} newTrackId={newMarkerId} onApply={applyBatch} onSetProperty={onSetProperty} onSeek={seek} />
      </Box>
    );
  } else if (simpleView === 'title') {
    panelTitle = tS('title');
    back = true;
    panel = (
      <div className={phone.sheetStack} data-testid="simple-title-panel">
        <TitlesSection actions={phoneActions} />
      </div>
    );
  } else if (simpleView === 'sound') {
    panelTitle = tS('sound');
    back = true;
    panel = (
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
          const target = store.getState().doc.tracks.find((track) => track.id === trackId);
          dispatch({ type: 'setTrackProperty', trackId, key: 'gainDb', value: { value: db, keys: target?.gainDb?.keys ?? null } }, tS('trackVolume', { name: target?.name ?? '' }));
        }}
        voiceover={<LiveVoRecorderPanel store={store} doc={doc} projectId={projectId} readOnly={readOnly} newId={newMarkerId} onApply={applyBatch} />}
      />
    );
  } else if (simpleView === 'format') {
    panelTitle = tS('format');
    back = true;
    panel = <TimelineSettingsPanel settings={doc.settings} hasClips={timelineHasClips(doc)} readOnly={readOnly} onChange={(patch) => dispatch({ type: 'setTimelineSettings', patch }, tx('changeTimelineSettings'))} />;
  } else if (selected.length > 0) {
    panelTitle = selected.length === 1 ? selected[0].name || tS('selection') : tS('selectionMany', { count: selected.length });
    panel = <SimpleSelection items={selected} actions={phoneActions} onOpenInFull={(workspace) => openInFull({ workspace, rightTab: workspace === 'edit' ? 'inspector' : undefined })} onCaptions={() => setSimpleView('captions')} />;
  } else {
    panelTitle = tS('tasks');
    panel = <SimpleTasks hasClips={timelineHasClips(doc)} readOnly={readOnly} onTask={runTask} />;
  }

  const askPanel =
    aiActive || aiOpened ? (
      <Box p="sm">
        <ProposalPanel
          key={askSeed ? `${askSeed.agent}:${askSeed.nonce}` : 'review'}
          initialAgent={askSeed?.agent}
          initialBrief={askSeed?.brief}
          doc={doc}
          projectId={projectId}
          timelineId={timelineId}
          readOnly={readOnly}
          inOut={ui.inOut}
          onGhosts={setGhostState}
          onApply={applyBatch}
          onSeek={seek}
        />
      </Box>
    ) : null;
  if (aiActive) {
    panelTitle = tS('ask');
    back = true;
    panel = null;
  }

  const simpleShell = (
    <LiveChordsProvider chords={runningChords}>
      <GradePreviewContext.Provider value={gradePreviewValue}>
        <SimpleWorkspace
          name={doc.name}
          save={save}
          backHref={editorHubHref(projectId)}
          onRetry={() => void sync.retry()}
          onSignIn={() => sync.requestSignIn()}
          layout={isTablet ? 'tablet' : 'desktop'}
          place={simplePlace}
          onPlace={setSimplePlace}
          shellSwitch={<ShellSwitch shell="simple" onChange={setShell} />}
          pending={
            <>
              <PendingResults store={store} projectId={projectId} />
              {busyGuard}
            </>
          }
          onShortcuts={() => setCheatsheetOpen(true)}
          introOpen={simpleIntroOpen}
          onDismissIntro={dismissIntro}
          onOpenFull={() => {
            dismissIntro();
            openInFull();
          }}
          banner={banner}
          dialogs={dialogs}
          media={<Box style={{ height: '100%' }}>{mediaWorkspace}</Box>}
          picture={viewerPicture}
          transport={viewerTransport}
          attachViewer={attachViewerZone}
          fullscreenOn={fullscreenOn}
          viewerTheme={viewerPortalTheme}
          ask={
            <AskBox
              readOnly={readOnly}
              onAsk={(agent, brief) => {
                setSimplePlace('edit');
                setAskSeed({ agent, brief, nonce: Date.now() });
                setSimpleView('ask');
              }}
            />
          }
          timeline={timeline}
          panelTitle={panelTitle}
          onPanelBack={back ? () => setSimpleView('home') : undefined}
          panel={panel}
          aiPanel={askPanel}
          aiActive={aiActive}
          onOpenAi={() => {
            setSimplePlace('edit');
            setSimpleView('ask');
          }}
        />
      </GradePreviewContext.Provider>
    </LiveChordsProvider>
  );

  return { simpleShell };
}

export type SimpleViewCtx = ChromeViewCtx & ReturnType<typeof useSimpleView>;
