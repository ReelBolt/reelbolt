import { useState, useRef, useCallback, useMemo, useEffect } from 'react';
import { useDeviceClass, useRetireFullViewOnPhone } from '@/lib/editing/use-device-class';
import { registerEnhanceHost } from '../enhance/enhance-host';
import { notifications } from '@mantine/notifications';
import { useEditorStore } from '@/lib/editing/store';
import { type EditOp } from '@/lib/editing/engine';
import { type SaveState, isReadOnlySave } from '@/lib/editing/sync';
import { timelineEndFrame } from '@/lib/editing/transport';
import { type PreviewSession, type AudioMeterSnapshot, type PlaybackStats, ZERO_STATS, type PreviewSessionKind, readDeviceFacts } from '@/lib/editing/preview';
import { createCoarseUiSelector } from '@/lib/editing/store/coarse-ui';
import { createValueStore, createStatsThrottle } from '@/lib/editing/preview/throttled-stats';
import { useLandedToast } from '../place/landed-toast';
import { usePipToast } from '../place/pip-toast';
import type { EditTimeline } from '@/lib/editing/types';
import { type GradePreview, withGradePreview } from '@/lib/editing/color/grade-preview';
import type { ProposalAgent } from '@/lib/editing/sync/api';
import { useRefusalToast } from '../shell/refusal-toast';
import { useViewerFullscreen } from '../viewer/useViewerFullscreen';
import { useOptionalAssistant } from '@/components/assistant/AssistantShellProvider';
import { programPictureCanvas, canvasToPngBlob, downloadBlob, stillFileName } from '@/lib/editing/stills/stills';
import { useT } from '@/i18n';
import type { EditorWorkspaceInput } from './types';

/**
 * The workspace's ground state: what it reads from the store, the save state, the playback statistics, the device
 * class, the audition and the grade preview. Everything later in the controller builds on these values.
 */
export function useWorkspaceCore(ctx: EditorWorkspaceInput) {
  const { store, sync } = ctx;
  const tx = useT('editor.editorWorkspace');
  const wb = useT('editor.shell.workspaceBar');
  const { showRefusal } = useRefusalToast();
  const showLandedToast = useLandedToast();
  const showPipToast = usePipToast();
  const doc = useEditorStore(store, (s) => s.doc);
  const revision = useEditorStore(store, (s) => s.revision);
  const dirty = useEditorStore(store, (s) => s.dirty);
  const [silenceOpen, setSilenceOpen] = useState(false);
  const [beatsOpen, setBeatsOpen] = useState(false);
  const [showBeats, setShowBeats] = useState(true);
  const [assistHelper, setAssistHelper] = useState<ProposalAgent | undefined>(undefined);
  // P5: the live preview session, for the mixer's meters (a ref: it changes without re-rendering).
  const sessionRef = useRef<PreviewSession | null>(null);
  const getMeters = useCallback((): AudioMeterSnapshot | null => sessionRef.current?.meters?.() ?? null, []);
  // Coarse while playing (lib/editing/store/coarse-ui.ts): the shell does not re-render on every frame.
  const coarseUiSelector = useMemo(() => createCoarseUiSelector(), []);
  const ui = useEditorStore(store, coarseUiSelector);
  // T-002: the page is a place — its name is the h1 and the document title, and a switch moves focus there.
  const pageHeadingRef = useRef<HTMLHeadingElement | null>(null);
  const lastWorkspace = useRef(ui.workspace);
  const pageName = wb(`names.${ui.workspace}.label`);
  // Capture the title BEFORE the title effect runs, so unmounting restores the page we came from.
  useEffect(() => {
    const previous = document.title;
    return () => {
      document.title = previous;
    };
  }, []);
  useEffect(() => {
    document.title = `${pageName} · ${doc.name}`;
  }, [pageName, doc.name]);
  useEffect(() => {
    if (lastWorkspace.current === ui.workspace) return;
    lastWorkspace.current = ui.workspace;
    // Never fight an open dialog or the palette: only a plain page switch moves focus to the heading.
    if (document.querySelector('[role="dialog"][aria-modal="true"],dialog[open]')) return;
    pageHeadingRef.current?.focus({ preventScroll: true });
  }, [ui.workspace]);
  const history = useEditorStore(store, (s) => s.history);
  const cursor = useEditorStore(store, (s) => s.cursor);
  const [save, setSave] = useState<SaveState>(sync.getState());
  const statsStore = useMemo(() => createValueStore<PlaybackStats>(ZERO_STATS), []);
  const [sessionKind, setSessionKind] = useState<PreviewSessionKind>('unavailable');
  // The session reports every presented frame; the counter is painted a few times a second (throttled-stats.ts).
  const statsThrottle = useMemo(() => createStatsThrottle<PlaybackStats>((next) => statsStore.set(next)), [statsStore]);
  useEffect(() => () => statsThrottle.cancel(), [statsThrottle]);
  const onStats = useCallback(
    (next: PlaybackStats, kind: PreviewSessionKind) => {
      statsThrottle.push(next);
      setSessionKind(kind);
    },
    [statsThrottle],
  );
  // R04.5: null = follow Auto (a low-budget device starts on the simple preview); true/false = the user's own choice.
  const [simpleChoice, setSimpleChoice] = useState<boolean | null>(null);
  // The viewer on the whole screen (view state only).
  const { attach: attachViewerZone, active: fullscreenOn, supported: fullscreenSupported, toggle: toggleFullscreen, exit: exitFullscreen, portalTheme: viewerPortalTheme } = useViewerFullscreen();
  const [deviceFacts] = useState(readDeviceFacts);
  // R05.8: desktop and tablet open on the media pool; a phone keeps Markers (its pool lives in the Media workspace).
  const [leftTab, setLeftTab] = useState<string | null>('media');
  const [rightTab, setRightTab] = useState<string | null>('inspector');
  // RM.2: the phone surface follows the one device-class source (phone under 600 wide, or coarse and under 500 tall),
  // never a width literal. The workspace only mounts after the timeline has loaded in the browser, never from
  // server markup, so there is nothing to hydrate against.
  const deviceClass = useDeviceClass();
  useRetireFullViewOnPhone();
  const isPhone = deviceClass === 'phone';
  const platformAssistant = useOptionalAssistant();
  // RM.15: the tablet runs this layout hardened for touch (rail, drawers, wrapped toolbar). "Works, not certified" (D2).
  const isTablet = deviceClass === 'tablet';
  // P6: the compositor's canvas, for stills.
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  useEffect(() => sync.subscribe(setSave), [sync]);
  // The revision panel refetches whenever a save lands: the confirmed revision is the signal.
  const refreshRevisions = save.revision;

  // Frozen: changed elsewhere, open in another tab, or a view-only member (the store refuses every write too).
  const readOnly = isReadOnlySave(save);
  const readOnlyMessage = save.viewOnly
    ? tx('youCanLookBut')
    : save.status === 'otherTab' || save.status === 'takenOver'
      ? tx('thisTimelineIsOpen')
      : tx('thisTimelineWasChanged');
  // P6: the one program a batch applies as one undo step, and the still saver.
  const applyBatch = useCallback(
    (ops: readonly EditOp[], label: string): boolean => {
      if (readOnly) return false;
      try {
        // false = the edit guard held it (it lands later if the user goes on).
        return store.dispatchBatch(ops, label);
      } catch (error) {
        showRefusal(error, store.getState().doc);
        return false;
      }
    },
    [store, readOnly],
  );
  // The phone clip sheet's Enhance section applies through this one batch (see enhance/enhance-host.ts).
  useEffect(() => {
    registerEnhanceHost({ projectId: store.getState().projectId ?? null, getDoc: () => store.getState().doc, dispatchBatch: applyBatch });
    return () => registerEnhanceHost(null);
  }, [store, applyBatch]);
  const saveStill = useCallback(async () => {
    const canvas = programPictureCanvas(canvasRef.current);
    if (!canvas) {
      notifications.show({ color: 'yellow', title: tx('noPictureToSave'), message: tx('thePreviewHasNo') });
      return;
    }
    const blob = await canvasToPngBlob(canvas);
    if (!blob) {
      notifications.show({ color: 'yellow', title: tx('noPictureToSave2'), message: tx('thePreviewCanvasCould') });
      return;
    }
    const state = store.getState();
    downloadBlob(blob, stillFileName(state.doc.name, state.ui.playheadFrame, state.doc.settings.rate));
  }, [store, tx]);
  // R09.3: audition - the viewer plays the RESULT of proposed cuts while the document stays untouched.
  // Apply writes them as one undo step; Cancel (or any other edit) drops the audition.
  const [auditionState, setAudition] = useState<{ base: EditTimeline; preview: EditTimeline; ops: EditOp[]; label: string } | null>(null);
  // Any change to the document (an edit, undo, a remote change) ends the audition: it previewed the OLD one.
  const audition = auditionState && auditionState.base === doc ? auditionState : null;
  const auditionRef = useRef(audition);
  useEffect(() => {
    auditionRef.current = audition;
  }, [audition]);
  const shownBaseDoc = audition ? audition.preview : doc;
  // T-008: a colour-wheel drag previews its grade on the picture without writing the document. Like the
  // audition, the preview belongs to the base document: any real change drops it.
  const [gradePreviewState, setGradePreview] = useState<{ base: EditTimeline; preview: GradePreview } | null>(null);
  const gradePreview = gradePreviewState && gradePreviewState.base === doc ? gradePreviewState.preview : null;
  const gradePreviewValue = useMemo(() => ({ set: (preview: GradePreview | null) => setGradePreview(preview ? { base: doc, preview } : null) }), [doc]);
  const shownDoc = withGradePreview(shownBaseDoc, gradePreview);
  // The file of the first selected clip, so the Analyse panel can put it first.
  const selectedFileId = useMemo(() => {
    const first = ui.selection[0];
    if (!first) return null;
    for (const track of doc.tracks) for (const item of track.items) if (item.id === first && item.type === 'mediaClip' && item.assetRef.kind === 'projectFile') return item.assetRef.projectFileId ?? null;
    return null;
  }, [ui.selection, doc]);
  // R10.4: the selected clip decides whether "Preview 3 s" is offered in the viewer header.
  const previewItem = useMemo(() => {
    const first = ui.selection[0];
    if (!first) return null;
    for (const track of doc.tracks) for (const item of track.items) if (item.id === first && item.type === 'mediaClip') return item;
    return null;
  }, [ui.selection, doc]);
  const pausePlayback = useCallback(() => store.setUi({ playbackSpeed: 0 }), [store]);
  const endFrame = useMemo(() => timelineEndFrame(shownDoc), [shownDoc]);
  const rate = doc.settings.rate;

  return { showRefusal, showLandedToast, showPipToast, doc, revision, dirty, silenceOpen, setSilenceOpen, beatsOpen, setBeatsOpen, showBeats, setShowBeats, assistHelper, setAssistHelper, sessionRef, getMeters, ui, pageHeadingRef, history, cursor, save, statsStore, sessionKind, onStats, simpleChoice, setSimpleChoice, attachViewerZone, fullscreenOn, fullscreenSupported, toggleFullscreen, exitFullscreen, viewerPortalTheme, deviceFacts, leftTab, setLeftTab, rightTab, setRightTab, isPhone, platformAssistant, isTablet, canvasRef, refreshRevisions, readOnly, readOnlyMessage, applyBatch, saveStill, auditionState, setAudition, audition, auditionRef, gradePreviewValue, shownDoc, selectedFileId, previewItem, pausePlayback, endFrame, rate };
}

export type WorkspaceCoreCtx = EditorWorkspaceInput & ReturnType<typeof useWorkspaceCore>;
