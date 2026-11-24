'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import { Alert, Badge, Button, Group, Loader, Select, Stack, Tabs, Text, Tooltip, UnstyledButton } from '@mantine/core';
import {
  IconArrowBackUp,
  IconArrowForwardUp,
  IconBrush,
  IconFileExport,
  IconHandStop,
  IconPointer,
  IconScissors,
  IconTypography,
} from '@tabler/icons-react';
import { notifyInsufficientCredits } from '@/components/billing/insufficientCreditsNotification';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { uploadFile, uploadFileWithProgress } from '@/lib/api/files';
import {
  createImageExport,
  createImageGraphic,
  createLayerFromFile,
  canConfigureOf,
  isNoProviderRefusal,
  createTextLayer,
  getImageGraphics,
  getOperationCapabilities,
  isOperationUnavailable,
  runImageOperation,
  segmentSubjects,
  sourceTooLarge,
  uploadEditorAsset,
} from '@/lib/api/image-documents';
import {
  OPERATION_INFO,
  buildOperationRequest,
  runsOnSubjectRoute,
  subjectsChangeRoute,
  type OperationInput,
} from '@/lib/editor/ai-operations';
import { needsAiLabel } from '@/lib/editor/ai-label';
import { describeSubjectRefusal, type SubjectRefusal } from '@/lib/editor/subject-refusal';
import { avatarMediaConsentPath, grantAvatarMediaConsent } from '@/lib/api/avatars';
import { useAvatarRequirements, useProjectAvatars } from '@/lib/hooks/use-avatars';
import { MediaConsentDialog } from '@/components/avatars/MediaConsentDialog';
import { mutate as globalMutate } from 'swr';
import type { AiEffect, EffectTarget } from '@/lib/editor/ai-effect';
import { aiToolInfo, availabilityOperation, type AiToolId } from '@/lib/editor/ai-tools';
import { onImageDocumentChange, type ImageDocumentChange } from '@/lib/editor/assistant-events';
import { activeGraphicJob, graphicTargetRegion, jobsToAdopt, revisableGraphic } from '@/lib/editor/graphics';
import {
  downloadBlob,
  ensureFontsFor,
  domMaskCanvas,
  loadBitmap,
  renderExport,
  renderScaledCopy,
} from '@/lib/editor/canvas-dom';
import { MAX_EXPORT_BYTES, exportFormat, formatBytes, type ExportFormatId } from '@/lib/editor/export-formats';
import { findSourceLayer, type EditorLayer } from '@/lib/editor/model';
import { originalCanvasSize } from '@/lib/editor/canvas-size';
import {
  layersRemoved,
  originalSize,
  patchCanvas,
  patchLayer,
  patchLayers,
  reorderLayer,
  sequence,
  type CanvasPatch,
  type LayerPatch,
} from '@/lib/editor/history';
import { MaskEmptyError, MaskPainter } from '@/lib/editor/mask';
import type { Point } from '@/lib/editor/compose';
import { defaultTextContent } from '@/lib/editor/text-content';
import { containFitTransform } from '@/lib/editor/transform-tools';
import { isEditableImageType } from '@/lib/editor/open-thumbnail-editor';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useEditorDefaults } from '@/lib/hooks/use-editor-defaults';
import { useImageGraphics } from '@/lib/hooks/use-image-graphics';
import type {
  CreateImageGraphicRequest,
  EditImageOperation,
  ExportContentType,
  ImageGraphicJob,
  ImageOperationCapabilities,
  ImageOperationRequest,
  ImageOperationResult,
  SubjectBoxPrompt,
  SubjectPointPrompt,
  TextContent,
} from '@/lib/types/image-document';
import { EDIT_IMAGE_OPERATIONS } from '@/lib/types/image-document';
import { isReconcilable } from '@/lib/editor/reconcile';
import { useT, type T } from '@/i18n';
import { describeError, readableServerMessage } from '@/lib/api/describe-error';
import { useActivity } from '@/lib/activity/ActivityProvider';
import { useRunLabelA } from './run-label-a';
import type { ResultInfo } from '@/lib/editor/result-bar';
import { AiPanel, type SubjectRouteAnswer } from './AiPanel';
import { RunFooterContext } from './PinnedRun';
import { ArrangePanel } from './ArrangePanel';
import { ResultBar } from './ResultBar';
import { BusyGuardModal, ReviewBar } from './BusyNotices';
import { loadPictureReview, savePictureReview } from './picture-review-store';
import { stopExecution } from '@/lib/api/executions';
import { cropToRect, naturalSize } from '@/lib/editor/crop';
import { CropDialog } from './CropDialog';
import { useArrange } from './useArrange';
import { BrushPanel } from './BrushPanel';
import { CanvasPanel } from './CanvasPanel';
import { DesignGraphicPanel } from './DesignGraphicPanel';
import { EditorCanvas, type BrushState, type SubjectPrompts, type ToolId } from './EditorCanvas';
import { nudgeTransform } from './layer-actions';
import type { NudgeKey } from '@/lib/editor/transform-tools';
import { ExportDialog, type ExportRequest } from './ExportDialog';
import { refitCanvas } from '@/lib/editor/refit';
import { LayersPanel, UploadImageButton } from './LayersPanel';
import { LayerMaskBar } from './LayerMaskBar';
import { useLayerMaskEditing } from './useLayerMaskEditing';
import { MAX_SUBJECT_MASKS, SubjectPanel } from './SubjectPanel';
import { TextPanel } from './TextPanel';
import { useCoarsePointer } from './useCoarsePointer';
import { useEditorDocument } from './useEditorDocument';
import { useLayerBitmaps } from './useLayerBitmaps';
import { useStageFit } from './useStageFit';
import classes from './ThumbnailEditor.module.css';
import phoneClasses from '../phone/phone.module.css';
import { BottomSheet, SHEET_HEIGHTS, type SheetDetent } from '../phone/BottomSheet';
import { UndoSnackbar, useUndoSnackbar } from '../phone/UndoSnackbar';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { phoneStageHeight, PICTURE_DOCK, type PicturePanelId } from './phone-shell';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { EditorIcon, type EditorIconComponent } from '../ui';

const NO_LAYERS: EditorLayer[] = [];

/** A background job working on a layer: `job` is set for a designed graphic (it can be stopped). */
interface BusyJob {
  key: string;
  layerId: string;
  job: ImageGraphicJob | null;
}

type PanelId = PicturePanelId;

export interface ThumbnailEditorProps {
  projectId: string;
  documentId: string;
}

interface Message {
  /** `warning`: it worked, but not exactly as asked (a smaller copy of the picture was used). */
  tone: 'error' | 'info' | 'warning';
  text: string;
}

const errorText = (t: Pick<T, 'msg'>, cause: unknown, fallback: string) => humanizeErrorT(t, cause, fallback);

const isTypingTarget = (target: EventTarget | null) => {
  const el = target as HTMLElement | null;
  if (!el) return false;
  return el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable;
};

const clock = () => (typeof performance !== 'undefined' ? performance.now() : Date.now());

/**
 * The thumbnail editor: a layered picture on an infinite canvas, with every expensive pixel operation
 * a call to the server and everything else done here in the browser.
 *
 * This component owns only what is not the document: which layer is selected, which tool and AI tool
 * are armed, the brush mask in progress, the prompts staged for a subject cut-out, the AI effect on
 * the canvas, and the messages. The document itself — loading, local edits, undo, the debounced write
 * to the server — is {@link useEditorDocument}; pixels are decoded by {@link useLayerBitmaps}; the
 * drawing is `lib/editor/compose.ts` and `lib/editor/workspace.ts`.
 */
export function ThumbnailEditor({ projectId, documentId }: ThumbnailEditorProps) {
  const t = useT('editor-thumbnail-a.editor');
  const tc = useT('editor-thumbnail-a.common');
  const tAll = useT();
  const { estimateLine } = useRunLabelA();
  const editor = useEditorDocument(projectId, documentId);
  const doc = editor.doc;
  const layers = doc?.layers ?? NO_LAYERS;
  const { bitmaps, missing, unavailable: unloadableIds, retry: retryBitmaps, seed: seedBitmap } = useLayerBitmaps(layers);
  const coarse = useCoarsePointer();
  const layoutRef = useRef<HTMLDivElement>(null);
  useStageFit(layoutRef, doc !== null && doc !== undefined);
  const { data: projectFiles, mutate: refreshProjectFiles } = useProjectFiles(projectId);
  // The project's avatars and objects, for the two AI tools that can show them. A 404 on the requirements means
  // the feature is off here: nothing about it is then shown.
  const { data: avatarRequirements, error: avatarRequirementsError } = useAvatarRequirements(projectId);
  const avatarsOffered = Boolean(avatarRequirements && !avatarRequirementsError);
  const { data: projectAvatars } = useProjectAvatars(projectId, avatarsOffered);
  const tAv = useT('avatars');
  const graphics = useImageGraphics(projectId, documentId);
  const { trackJob, markNeedsReview, resolveTrackedJob, allJobs } = useActivity();
  // The tray job that mirrors each kept-hidden layer (layer id -> job id), so the bar and the tray resolve each other.
  const reviewJobs = useRef(new Map<string, string>());
  const { data: editorDefaults } = useEditorDefaults(projectId);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [tool, setTool] = useState<ToolId>('select');
  const [panel, setPanelState] = useState<PanelId>('layers');
  // The phone shell (R13.6): Layers / Text / AI / Canvas open as a bottom sheet over a stage that stays visible.
  const phone = useDeviceClass() === 'phone';
  const landscape = useMediaQuery('(orientation: landscape)') ?? false;
  const undoable = useUndoSnackbar();
  const [sheetOpen, setSheetOpen] = useState(false);
  const [detent, setDetent] = useState<SheetDetent>('half');
  const [runFooter, setRunFooter] = useState<HTMLElement | null>(null);
  const [stageH, setStageH] = useState<number | null>(null);
  // Choosing a panel (the dock, or a tool that needs one) shows it: on a phone that opens the sheet.
  const setPanel = (next: PanelId) => {
    setPanelState(next);
    if (phone) setSheetOpen(true);
  };
  const stageReady = doc !== null && doc !== undefined;
  const stageSheet = sheetOpen ? detent : null;
  // Portrait phone: the stage is 45 dvh, and ends above an open sheet so the picture stays visible and reactive.
  useEffect(() => {
    const stage = layoutRef.current?.querySelector<HTMLElement>(':scope > [data-stage]');
    if (!phone || landscape || !stageReady || !stage) {
      setStageH(null);
      return;
    }
    const measure = () => {
      const dock = document.querySelector<HTMLElement>('[data-testid="picture-dock"]')?.offsetHeight ?? 0;
      setStageH(phoneStageHeight(window.innerHeight, stage.getBoundingClientRect().top + dock, stageSheet));
    };
    // The page scrolls: bring the stage to the top first so it sits above the open sheet, not behind it.
    if (stageSheet !== null) stage.scrollIntoView({ block: 'start' });
    measure();
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, [phone, landscape, stageReady, stageSheet]);
  const [aiTool, setAiTool] = useState<AiToolId | null>(null);
  const [fontVersion, setFontVersion] = useState(0);
  const [message, setMessage] = useState<Message | null>(null);

  const [painter, setPainter] = useState<MaskPainter | null>(null);
  const [maskLayerId, setMaskLayerId] = useState<string | null>(null);
  const [maskTick, setMaskTick] = useState(0);
  const [brush, setBrush] = useState<BrushState>({ size: 60, erase: false });

  // Layer masks: painting one is a session with its own tool; leaving the tool ends the session.
  const maskEdit = useLayerMaskEditing({
    projectId,
    documentId,
    layers,
    bitmaps,
    writeMatte: editor.writeMatte,
    seed: seedBitmap,
    onMessage: (tone, text) => setMessage({ tone, text }),
    onActive: (active) => {
      if (active) {
        setPanel('layers');
        setTool('layermask');
      } else {
        setTool((cur) => (cur === 'layermask' ? 'select' : cur));
      }
    },
  });
  const maskPainting = maskEdit.session !== null;

  const [prompts, setPrompts] = useState<SubjectPrompts>({ points: [], boxes: [] });
  const [subjectBusy, setSubjectBusy] = useState(false);
  const [markMode, setMarkMode] = useState<'include' | 'leaveOut'>('include');

  const [runningOp, setRunningOp] = useState<EditImageOperation | null>(null);
  // The one-time question before avatar or object files are sent to an AI service, and the run it interrupted.
  const [mediaConsent, setMediaConsent] = useState<{ op: EditImageOperation; input: OperationInput; busy: boolean; failed: boolean } | null>(null);
  const [unavailable, setUnavailable] = useState<Partial<Record<EditImageOperation, string>>>({});
  const [capabilities, setCapabilities] = useState<Partial<Record<EditImageOperation, ImageOperationCapabilities>>>({});
  // "Create a picture" WITH avatars or objects runs on the picture-edit service, so once the person has chosen some
  // the server is asked about that run (`subjects=true`) and its availability and price are what the tool shows.
  const [subjectRouteWanted, setSubjectRouteWanted] = useState<Partial<Record<EditImageOperation, boolean>>>({});
  const [subjectRoute, setSubjectRoute] = useState<Partial<Record<EditImageOperation, SubjectRouteAnswer>>>({});
  const [lastRun, setLastRun] = useState<
    Partial<Record<EditImageOperation, Pick<ImageOperationResult, 'provider' | 'usage' | 'chargedCredits'>>>
  >({});
  // Whether the caller may set up the workspace's AI services (a member is told to ask an admin), and
  // what the last cut-out cost: one availability model for every paid tool.
  const [canConfigure, setCanConfigure] = useState(false);
  const [subjectCharged, setSubjectCharged] = useState<number | null>(null);
  const [graphicAvailability, setGraphicAvailability] = useState<{ reason: string; canConfigure: boolean } | null>(null);
  const [effect, setEffect] = useState<AiEffect | null>(null);
  // The bar after a paid run (what changed, the cost, compare / undo / try again, and the options of a cut-out).
  const [result, setResult] = useState<{ info: ResultInfo; options: boolean; op: EditImageOperation | null; retry?: () => void } | null>(null);
  const [comparing, setComparing] = useState(false);
  const [activeOption, setActiveOption] = useState(0);
  // Results the server kept hidden because their layer was deleted meanwhile (waiting for the user's OK).
  const [reviewIds, setReviewIds] = useState<string[]>(() => loadPictureReview(documentId));

  // When each design job this editor started began, so its working effect fades in from then.
  const [graphicStarts, setGraphicStarts] = useState<Record<string, number>>({});
  // Design jobs whose finished layer this editor has already brought in (or is bringing in).
  const settledGraphics = useRef(new Set<string>());

  const [pendingDelete, setPendingDelete] = useState<string | null>(null);

  // Background work on a layer: an AI operation running here, or a designed graphic revising it. An edit that
  // would change such a layer asks first; "Go on" is remembered per job for the session.
  const [busyOp, setBusyOp] = useState<{ key: string; layerId: string } | null>(null);
  const opSeq = useRef(0);
  const busyRef = useRef<BusyJob[]>([]);
  const ackedRef = useRef(new Set<string>());
  const [ask, setAsk] = useState<{ keys: string[]; run: () => void; job: BusyJob['job'] } | null>(null);
  const guard = useCallback((layerId: string, run: () => void) => {
    const open = busyRef.current.filter((b) => b.layerId === layerId && !ackedRef.current.has(b.key));
    if (open.length === 0) {
      run();
      return;
    }
    setAsk((cur) => cur ?? { keys: open.map((b) => b.key), run, job: open.find((b) => b.job)?.job ?? null });
  }, []);
  const goOn = () => {
    if (!ask) return;
    ask.keys.forEach((k) => ackedRef.current.add(k));
    const run = ask.run;
    setAsk(null);
    run();
  };
  const [deleting, setDeleting] = useState(false);
  const [exportOpen, setExportOpen] = useState(false);
  const [addImageId, setAddImageId] = useState<string | null>(null);

  const arrange = useArrange({
    projectId,
    documentId,
    editor,
    layers,
    bitmaps,
    doc: doc ?? null,
    fontVersion,
    selectedId,
    onSelect: setSelectedId,
    say: setMessage,
    onFileAdded: () => void refreshProjectFiles(),
    guard,
  });
  const canvasDoc = useMemo(() => {
    if (!doc || !result) return doc;
    const ids = result.info.layerIds;
    if (comparing) {
      const hide = new Set(ids);
      return { ...doc, layers: doc.layers.map((l) => (hide.has(l.id) ? { ...l, visible: false } : l)) };
    }
    if (result.options) {
      const shown = ids[activeOption];
      const group = new Set(ids);
      return { ...doc, layers: doc.layers.map((l) => (group.has(l.id) ? { ...l, visible: l.id === shown } : l)) };
    }
    return doc;
  }, [activeOption, comparing, doc, result]);
  const selectedLayer = layers.find((l) => l.id === selectedId) ?? null;
  const sourceLayer = findSourceLayer(layers);
  // The layer a subject is cut from: the selected picture layer (an AI result, an upload, a cut-out),
  // else the original. Any layer with pixels can be cut from, not only the original.
  const subjectLayer = selectedLayer && selectedLayer.kind !== 'text' ? selectedLayer : sourceLayer;
  const subjectLayerId = subjectLayer?.id ?? null;
  const hasMask = !!painter && !painter.isEmpty && maskLayerId !== null && maskLayerId === selectedLayer?.id;

  // The selected layer's own pixel size: the server's, or the decoded picture's (an uploaded photo's row has none).
  const selectedImage = selectedLayer ? bitmaps.get(selectedLayer.id)?.image : undefined;
  const selectedLayerSize = useMemo(() => {
    if (!selectedLayer || selectedLayer.kind === 'text') return null;
    if (selectedImage) return { width: selectedImage.width, height: selectedImage.height };
    return selectedLayer.width && selectedLayer.height ? { width: selectedLayer.width, height: selectedLayer.height } : null;
  }, [selectedLayer, selectedImage]);

  // What the AI service behind the chosen tool can take (a prompt or not; how far expand can grow a picture),
  // asked once per tool. It costs nothing: the server reads its catalogue. A tool with no service set up is
  // marked unavailable with the server's own reason, exactly as a failed run would.
  // "Design a graphic" is an agent run, not a provider operation, so it has no capabilities to ask for;
  // "Cut out a subject" runs on the segmentation service, so it asks as "Remove background" does.
  const capabilityTool = aiTool ? availabilityOperation(aiTool) : null;
  // The size a run would be priced at: the selected layer's pixels, else the canvas. A change of
  // either asks again, so the price on the button is never stale.
  const priceWidth = selectedLayerSize?.width ?? doc?.canvasWidth ?? 0;
  const priceHeight = selectedLayerSize?.height ?? doc?.canvasHeight ?? 0;
  const unavailableText = (cause: unknown) =>
    isNoProviderRefusal(cause)
      ? tc(canConfigureOf(cause) ? 'noProvider.admin' : 'noProvider.member')
      : errorText(t, cause, t('noProviderFallback'));
  const noProvider = (cause: unknown, op: EditImageOperation) => {
    setUnavailable((prev) => ({ ...prev, [op]: unavailableText(cause) }));
    setCanConfigure(canConfigureOf(cause));
  };
  const learned = (op: EditImageOperation, caps: ImageOperationCapabilities) => {
    setCapabilities((prev) => ({ ...prev, [op]: caps }));
    setUnavailable((prev) => {
      if (!(op in prev)) return prev;
      const { [op]: _gone, ...rest } = prev;
      return rest;
    });
    if (caps.canConfigure !== undefined) setCanConfigure(caps.canConfigure);
  };
  useEffect(() => {
    if (!capabilityTool || !(priceWidth > 0)) return;
    let alive = true;
    getOperationCapabilities(projectId, capabilityTool, { documentId, width: priceWidth, height: priceHeight }).then(
      (caps) => {
        if (alive) learned(capabilityTool, caps);
      },
      (cause: unknown) => {
        if (alive && isOperationUnavailable(cause)) noProvider(cause, capabilityTool);
      },
    );
    return () => {
      alive = false;
    };
    // `learned` and `noProvider` only set state.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [capabilityTool, projectId, documentId, priceWidth, priceHeight]);

  // The run with subjects, asked once the person has chosen some for a tool whose run then moves to another AI
  // service, at the same size as the plain ask so the two prices are comparable. Its refusal (no picture-edit
  // service, a model that cannot take them) is kept apart from the plain tool's, which stays as it was.
  const subjectRouteOps = useMemo(
    () => (avatarsOffered ? EDIT_IMAGE_OPERATIONS.filter((op) => subjectsChangeRoute(op) && subjectRouteWanted[op]) : []),
    [avatarsOffered, subjectRouteWanted],
  );
  const subjectRouteKey = subjectRouteOps.join(',');
  useEffect(() => {
    if (subjectRouteOps.length === 0 || !(priceWidth > 0)) return;
    let alive = true;
    for (const op of subjectRouteOps) {
      getOperationCapabilities(projectId, op, { documentId, width: priceWidth, height: priceHeight }, { subjects: true }).then(
        (caps) => {
          if (!alive) return;
          setSubjectRoute((prev) => ({ ...prev, [op]: { capabilities: caps } }));
          if (caps.canConfigure !== undefined) setCanConfigure(caps.canConfigure);
        },
        (cause: unknown) => {
          if (alive && isOperationUnavailable(cause)) setSubjectRoute((prev) => ({ ...prev, [op]: { unavailable: unavailableText(cause) } }));
        },
      );
    }
    return () => {
      alive = false;
    };
    // `subjectRouteKey` stands for the list; `unavailableText` only reads the translators.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subjectRouteKey, projectId, documentId, priceWidth, priceHeight]);
  const onSubjectsChosen = useCallback((op: EditImageOperation, chosen: boolean) => {
    setSubjectRouteWanted((prev) => (prev[op] === chosen ? prev : { ...prev, [op]: chosen }));
  }, []);

  // Opening the AI tab asks about every tool once, so the list says which are not set up and what each
  // costs before one is picked. "Design a graphic" answers through its own list with the same fields.
  const probedAll = useRef(false);
  useEffect(() => {
    if (panel !== 'ai' || probedAll.current || !(priceWidth > 0)) return;
    probedAll.current = true;
    for (const op of EDIT_IMAGE_OPERATIONS) {
      getOperationCapabilities(projectId, op, { documentId, width: priceWidth, height: priceHeight }).then(
        (caps) => learned(op, caps),
        (cause: unknown) => {
          if (isOperationUnavailable(cause)) noProvider(cause, op);
        },
      );
    }
    getImageGraphics(projectId, documentId).then(
      (g) => {
        if (!g.available) {
          setGraphicAvailability({
            reason: tc(g.canConfigure === true ? 'noProvider.admin' : 'noProvider.member'),
            canConfigure: g.canConfigure === true,
          });
        }
      },
      () => undefined,
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [panel, priceWidth, priceHeight, projectId, documentId]);

  // Marks are in the pixels of the layer they were made on, so they cannot carry over to another layer.
  useEffect(() => {
    setPrompts((cur) => (cur.points.length === 0 && cur.boxes.length === 0 ? cur : { points: [], boxes: [] }));
  }, [subjectLayerId]);

  // ── fonts ───────────────────────────────────────────────────────────────────────────────────
  // The text layers' fonts, as a stable string so the effect runs when a face is first needed, not
  // on every edit. Loading is best-effort: until a face arrives the canvas draws the fallback.
  const fontKey = useMemo(
    () =>
      layers
        .filter((l) => l.kind === 'text' && l.content)
        .map((l) => `${l.content?.fontFamily}|${l.content?.fontWeight}`)
        .filter((v, i, all) => all.indexOf(v) === i)
        .sort()
        .join(','),
    [layers],
  );
  useEffect(() => {
    if (!fontKey) return;
    let alive = true;
    const wanted = fontKey.split(',').map((entry) => {
      const [fontFamily, weight] = entry.split('|');
      return { fontFamily, fontWeight: Number(weight) };
    });
    void ensureFontsFor(wanted).then(() => {
      if (alive) setFontVersion((v) => v + 1);
    });
    return () => {
      alive = false;
    };
  }, [fontKey]);

  // ── keyboard and page guards ────────────────────────────────────────────────────────────────
  const { undo, redo } = editor;
  // Inside mask painting, Ctrl+Z walks the strokes, not the document.
  const maskUndoRef = useRef({ painting: false, undo: maskEdit.undo, redo: maskEdit.redo });
  useEffect(() => {
    maskUndoRef.current = { painting: maskPainting, undo: maskEdit.undo, redo: maskEdit.redo };
  }, [maskPainting, maskEdit.undo, maskEdit.redo]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || isTypingTarget(e.target)) return;
      const key = e.key.toLowerCase();
      if (key === 'z' && !e.shiftKey) {
        e.preventDefault();
        if (maskUndoRef.current.painting) maskUndoRef.current.undo();
        else undo();
      } else if ((key === 'z' && e.shiftKey) || key === 'y') {
        e.preventDefault();
        if (maskUndoRef.current.painting) maskUndoRef.current.redo();
        else redo();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [undo, redo]);

  const unsaved = editor.saveStatus !== 'saved' || maskEdit.unsaved;
  useEffect(() => {
    if (!unsaved) return;
    const onBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [unsaved]);

  // ── mask ────────────────────────────────────────────────────────────────────────────────────
  const startMask = useCallback(
    (layer: EditorLayer | null) => {
      if (!layer || layer.kind === 'text') {
        setPainter(null);
        setMaskLayerId(null);
        return;
      }
      if (layer.crop) {
        // Paint and marks are made in the whole stored picture; with a crop on, they would land in the wrong place.
        setPainter(null);
        setMaskLayerId(null);
        setMessage({ tone: 'warning', text: t('croppedTool') });
        return;
      }
      const image = bitmaps.get(layer.id)?.image;
      const width = image?.width ?? layer.width;
      const height = image?.height ?? layer.height;
      if (!width || !height) {
        setPainter(null);
        setMaskLayerId(null);
        return;
      }
      setPainter(new MaskPainter(domMaskCanvas, width, height));
      setMaskLayerId(layer.id);
      setMaskTick((n) => n + 1);
    },
    [bitmaps, t],
  );

  const select = useCallback(
    (id: string | null) => {
      if (maskPainting && id !== maskEdit.session?.layerId) {
        setMessage({ tone: 'info', text: t('finishMaskFirst') });
        return;
      }
      setSelectedId(id);
      if (tool === 'brush' && id !== maskLayerId) startMask(layers.find((l) => l.id === id) ?? null);
    },
    [layers, maskEdit.session?.layerId, maskLayerId, maskPainting, startMask, tool, t],
  );

  /**
   * Gets the brush ready on a layer that can take paint: the selected picture layer, else the original (which is
   * then selected, so the user is never left holding a brush with nothing under it).
   */
  const armMask = () => {
    const target = selectedLayer && selectedLayer.kind !== 'text' ? selectedLayer : sourceLayer;
    if (target && target.id !== selectedId) setSelectedId(target.id);
    if (maskLayerId !== (target?.id ?? null)) startMask(target);
  };

  /** Arms a canvas tool. The brush and the subject clicker belong to AI tools, so they open them. */
  const chooseTool = (next: ToolId) => {
    if (maskPainting) {
      setMessage({ tone: 'info', text: t('finishMaskFirst') });
      return;
    }
    setTool(next);
    if (next === 'text') setPanel('text');
    if (next === 'subject') {
      setPanel('ai');
      setAiTool('subject');
    }
    if (next === 'brush') {
      setPanel('ai');
      if (!aiTool || aiToolInfo(aiTool).canvasTool !== 'brush') setAiTool('erase');
      armMask();
    }
  };

  /** Picks an AI tool, and arms the canvas tool it works with. */
  const pickAiTool = (id: AiToolId) => {
    if (maskPainting) {
      setMessage({ tone: 'info', text: t('finishMaskFirst') });
      return;
    }
    setAiTool(id);
    const canvasTool = aiToolInfo(id).canvasTool;
    if (canvasTool === 'brush') {
      setTool('brush');
      armMask();
    } else if (canvasTool === 'subject') {
      setTool('subject');
    } else if (tool === 'brush' || tool === 'subject') {
      setTool('select');
    }
  };

  // ── edits ───────────────────────────────────────────────────────────────────────────────────
  const { commit } = editor;
  const onPatch = useCallback(
    (layerId: string, patch: LayerPatch, label: string, mergeKey?: string) =>
      commit((d) => patchLayer(d, layerId, patch, label, mergeKey)),
    [commit],
  );
  const onTransform = useCallback(
    (layerId: string, transform: EditorLayer['transform'], label: string, mergeKey?: string) =>
      guard(layerId, () => commit((d) => patchLayer(d, layerId, { transform }, label, mergeKey))),
    [commit, guard],
  );
  const onNudgeSelected = useCallback(
    (key: NudgeKey, big: boolean) => {
      if (!selectedLayer || selectedLayer.locked) return;
      onTransform(selectedLayer.id, nudgeTransform(selectedLayer.transform, key, big), t('history.nudge'), `nudge:${selectedLayer.id}`);
    },
    [onTransform, selectedLayer, t],
  );
  const onReorder = useCallback(
    (layerId: string, position: number) => commit((d) => reorderLayer(d, layerId, position)),
    [commit],
  );
  const onCanvas = useCallback(
    (patch: CanvasPatch, label: string, mergeKey?: string) => commit((d) => patchCanvas(d, patch, label, mergeKey)),
    [commit],
  );

  const onRefit = useCallback(
    (size: { width: number; height: number }, label: string) => commit((d) => refitCanvas(d, size, label)),
    [commit],
  );

  // The photo the picture was opened on, at its own pixel size: the decoded image, else the server's
  // record of it (an older picture's row may state none).
  const sourceImage = sourceLayer ? bitmaps.get(sourceLayer.id)?.image : undefined;
  const sourceLayerId = sourceLayer?.id ?? null;
  const photoWidth = sourceImage?.width || sourceLayer?.width || 0;
  const photoHeight = sourceImage?.height || sourceLayer?.height || 0;
  const photoOriginal = originalCanvasSize({ width: photoWidth, height: photoHeight });
  const onOriginalSize = useCallback(() => {
    if (!sourceLayerId || !(photoWidth > 0) || !(photoHeight > 0)) return;
    commit((d) => originalSize(d, sourceLayerId, { width: photoWidth, height: photoHeight }));
  }, [commit, photoHeight, photoWidth, sourceLayerId]);

  // ── creating layers (server calls) ──────────────────────────────────────────────────────────
  const fail = (cause: unknown, fallback: string) => setMessage({ tone: 'error', text: errorText(t, cause, fallback) });

  /**
   * After a failed or dropped paid run the server may still have finished it, so the document is read
   * again and any layer that landed late is brought in (and selected). Nothing is run twice. Says
   * plainly which happened.
   */
  const refetchAfter = (cause: unknown, label: string, known: readonly string[]) => {
    if (!isReconcilable(cause)) return;
    void editor
      .recoverLayers(label, known)
      .then((found) => {
        if (found.length > 0) {
          setSelectedId(found[found.length - 1]);
          setResult(null);
        }
        // Nothing new: the server's own plain failure message (already shown) stands.
        if (found.length > 0) setMessage({ tone: 'info', text: t('notes.reconciled', { title: label }) });
      })
      .catch(() => undefined);
  };

  const addText = async (at?: Point) => {
    if (!doc) return;
    setMessage(null);
    const x = at ? Math.round(at.x) : Math.round(doc.canvasWidth * 0.06);
    const y = at ? Math.round(at.y) : Math.round(doc.canvasHeight * 0.38);
    try {
      const created = await editor.addLayers(t('history.addText'), async () => [
        await createTextLayer(projectId, documentId, {
          content: defaultTextContent(editorDefaults),
          transform: { x, y, scale: 1, rotation: 0 },
          role: 'overlay',
          // Stored in the picture document: the language-neutral English default (LayersPanel shows it in the interface language).
          label: 'Text',
        }),
      ]);
      setSelectedId(created[0]?.id ?? null);
      setTool('select');
      setPanel('text');
    } catch (cause: unknown) {
      fail(cause, t('errors.textAdd'));
    }
  };

  /**
   * Places a project image as a layer. A picture larger than the canvas is fitted inside it and
   * centred (in the same undo step); the user can scale it up again with the corner handles.
   */
  const addProjectImage = async (fileId: string | null = addImageId) => {
    if (!fileId) return;
    setMessage(null);
    const measured: { size?: { width: number; height: number } } = {};
    try {
      const created = await editor.addLayers(
        t('history.addImage'),
        async () => {
          const layer = await createLayerFromFile(projectId, documentId, { projectFileId: fileId, role: 'overlay' });
          if (layer.width && layer.height) {
            measured.size = { width: layer.width, height: layer.height };
          } else {
            try {
              const pixels = await loadBitmap(layer.imageUrl);
              measured.size = { width: pixels.width, height: pixels.height };
            } catch {
              // It is placed where the server put it, and simply loads later.
            }
          }
          return [layer];
        },
        (next, made) => {
          const id = made[0]?.id;
          if (!measured.size || !id) return next;
          const transform = containFitTransform(measured.size, { width: next.canvasWidth, height: next.canvasHeight });
          return { ...next, layers: next.layers.map((l) => (l.id === id ? { ...l, transform } : l)) };
        },
      );
      setSelectedId(created[0]?.id ?? null);
      setAddImageId(null);
    } catch (cause: unknown) {
      fail(cause, t('errors.imageAdd'));
    }
  };

  const [uploadPercent, setUploadPercent] = useState<number | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  /** A picture from this device: into the project's files first (as anywhere else), then onto the canvas. */
  const uploadImageLayer = async (file: File) => {
    setUploadPercent(0);
    setUploadError(null);
    try {
      const uploaded = await uploadFileWithProgress(projectId, file, setUploadPercent);
      void refreshProjectFiles();
      setUploadPercent(null);
      await addProjectImage(uploaded.id);
    } catch (cause: unknown) {
      setUploadPercent(null);
      setUploadError(
        readableServerMessage(cause) !== null
          ? t('errors.uploadWith', { reason: describeError(tAll, cause) })
          : t('errors.upload'),
      );
    }
  };

  const runSubjects = async (input: { prompt: string; returnMultipleMasks: boolean }) => {
    if (!subjectLayer) return;
    const knownBefore = layers.map((l) => l.id);
    setSubjectBusy(true);
    setMessage(null);
    const working: EffectTarget = { kind: 'layers', ids: [subjectLayer.id] };
    setEffect({ phase: 'working', target: working, startedAt: clock() });
    try {
      setSubjectCharged(null);
      setResult(null);
      let charged: number | null = null;
      const created = await editor.addLayers(t('history.cutOut'), () =>
        segmentSubjects(
          projectId,
          documentId,
          {
            layerId: subjectLayer.id,
            ...(input.prompt ? { prompt: input.prompt } : {}),
            ...(prompts.points.length ? { pointPrompts: prompts.points } : {}),
            ...(prompts.boxes.length ? { boxPrompts: prompts.boxes } : {}),
            ...(input.returnMultipleMasks ? { returnMultipleMasks: true, maxMasks: MAX_SUBJECT_MASKS } : {}),
          },
          (credits) => {
            charged = credits;
            setSubjectCharged(credits);
          },
        ),
      (next, made) =>
        // Several options come back as a group: the first shows, the others wait hidden until one is kept.
        made.length > 1
          ? { ...next, layers: next.layers.map((l) => (made.slice(1).some((m) => m.id === l.id) ? { ...l, visible: false } : l)) }
          : next,
      );
      const ids = created.map((l) => l.id);
      setActiveOption(0);
      setComparing(false);
      setResult({
        info: { title: t('history.cutOut'), layerIds: ids, layerNames: created.map((l) => l.label), charged, notice: null, step: t('history.cutOut') },
        options: created.length > 1,
        op: null,
      });
      setEffect({ phase: 'revealing', target: { kind: 'layers', ids }, startedAt: clock(), waitFor: ids });
      setPrompts({ points: [], boxes: [] });
      setSelectedId(created.length > 1 ? (ids[0] ?? null) : (created[created.length - 1]?.id ?? null));
      setTool('select');
      setMessage({
        tone: 'info',
        text: t('notes.subjectDone', { count: created.length }),
      });
    } catch (cause: unknown) {
      setEffect(null);
      if (notifyInsufficientCredits(cause)) {
        // Refused for lack of credits: the notification explains it and links to billing.
      } else if (isOperationUnavailable(cause)) {
        setUnavailable((prev) => ({ ...prev, removeBackground: errorText(t, cause, t('noProviderFallback')) }));
      } else {
        fail(cause, t('errors.subjectCut'));
        refetchAfter(cause, t('history.cutOut'), knownBefore);
      }
    } finally {
      setSubjectBusy(false);
    }
  };

  /** A refusal about the avatars or objects of a run, in plain words. */
  const subjectRefusalText = (refused: SubjectRefusal): string => {
    switch (refused.kind) {
      case 'noPictures': return t('subjects.noPictures');
      case 'notUsable': {
        const why = refused.reason && tAv.has(`reasons.${refused.reason}`) ? tAv(`reasons.${refused.reason}`) : null;
        return why ? t('subjects.notUsableReason', { reason: why }) : t('subjects.notUsable');
      }
      case 'unsupported': return refused.models ? t('subjects.unsupportedModels', { models: refused.models.join(', ') }) : t('subjects.unsupported');
      case 'plan': return t(`subjects.plan.${refused.feature ?? 'other'}`);
      default: return t('subjects.consent');
    }
  };

  /**
   * Runs an operation; when the server answers that the layer's picture is larger than the AI service takes
   * (422 `SOURCE_TOO_LARGE`, nothing spent), draws a copy at exactly the size it names - the size the layer
   * appears in the design - uploads it and runs again with it. The server rasterises nothing, so the copy is
   * made here, from the picture the canvas already decoded.
   */
  const runWithSourceCopy = async (request: ImageOperationRequest, layerId: string | null): Promise<ImageOperationResult> => {
    try {
      return await runImageOperation(projectId, documentId, request);
    } catch (cause: unknown) {
      const wanted = sourceTooLarge(cause);
      if (!wanted || !layerId) throw cause;
      const image = bitmaps.get(layerId)?.image;
      if (!image) throw new Error(t('errors.pictureLoading'));
      const copy = await renderScaledCopy(image, wanted.width, wanted.height);
      const sourceKey = await uploadEditorAsset(projectId, documentId, 'source', copy, 'image/png');
      return runImageOperation(projectId, documentId, { ...request, sourceKey });
    }
  };

  const runOperation = async (op: EditImageOperation, input: OperationInput) => {
    const info = OPERATION_INFO[op];
    if (input.layerId && layers.find((l) => l.id === input.layerId)?.crop) {
      setMessage({ tone: 'warning', text: t('croppedTool') });
      return;
    }
    const knownBefore = layers.map((l) => l.id);
    if (input.layerId) setBusyOp({ key: `op:${++opSeq.current}`, layerId: input.layerId });
    setRunningOp(op);
    setMessage(null);
    setResult(null);
    setComparing(false);
    // Where the work is: the painted area for erase and fill, the whole picture for a new one, the
    // selected layer otherwise. That part of the canvas goes soft while the AI works.
    const painted = info.needsMask && painter && maskLayerId ? painter.paintedBounds : null;
    const working: EffectTarget =
      painted && maskLayerId
        ? { kind: 'mask', layerId: maskLayerId, bounds: painted }
        : op === 'generate' || !input.layerId
          ? { kind: 'frame' }
          : { kind: 'layers', ids: [input.layerId] };
    try {
      let maskKey: string | undefined;
      if (info.needsMask) {
        if (!painter) throw new MaskEmptyError();
        maskKey = await uploadEditorAsset(projectId, documentId, 'mask', await painter.toPng());
      }
      // With avatars or objects "create a picture" runs on the picture-edit service: its answer is the one that counts.
      const runCaps = runsOnSubjectRoute(op, input) ? subjectRoute[op]?.capabilities : capabilities[op];
      const request = buildOperationRequest(op, input, maskKey, runCaps ?? null);
      setEffect({ phase: 'working', target: working, startedAt: clock() });
      let result: ImageOperationResult | null = null;
      const estimate = runCaps?.estimate;
      const created = await trackJob(
        {
          type: 'pictureOperation',
          title: info.title,
          projectId,
          href: `/editor/thumbnails/${documentId}`,
          creditsEstimate: estimate?.billingEnabled && estimate.credits !== undefined ? estimate.credits : null,
        },
        async (_signal, jobId) => {
          const made = await editor.addLayers(info.title, async () => {
            result = await runWithSourceCopy(request, input.layerId);
            return result.layers;
          });
          // The tray's "Needs your OK" must exist before the tracked work ends, or the job would end as done.
          if ((result as ImageOperationResult | null)?.needsReview) mirrorReview(jobId, info.title, made.map((l) => l.id));
          return made;
        },
      );
      const served = result as ImageOperationResult | null;
      if (served) setLastRun((prev) => ({
          ...prev,
          [op]: { provider: served.provider, usage: served.usage, chargedCredits: served.chargedCredits },
        }));
      const ids = created.map((l) => l.id);
      if (served?.needsReview) {
        // The layer this was made for is gone: the output is kept hidden, not shown as applied.
        setReviewIds((prev) => [...prev, ...ids]);
        // Adding a layer shows it; this one stays hidden until the user decides (not an undo step).
        editor.holdHidden(ids);
        setEffect(null);
        return;
      }
      // The changed pixels come into focus in place: the painted area for a mask tool (the rest of
      // the new layer is the same picture), the new layer's own bounds otherwise.
      setEffect({
        phase: 'revealing',
        target: working.kind === 'mask' ? working : { kind: 'layers', ids },
        startedAt: clock(),
        waitFor: ids,
      });
      setSelectedId(created[created.length - 1]?.id ?? null);
      if (info.needsMask) {
        painter?.clear();
        setMaskTick((n) => n + 1);
      }
      if (served?.notice) setMessage({ tone: 'warning', text: t('notes.opDone', { title: info.title, notice: served.notice }) });
      setResult({
        info: {
          title: info.title,
          layerIds: ids,
          layerNames: created.map((l) => l.label),
          charged: served?.chargedCredits ?? null,
          notice: null,
          step: info.title,
        },
        options: false,
        op,
        // A painted area is gone once the run is done, so only the tools that need none can repeat exactly.
        retry: info.needsMask ? undefined : () => void runOperation(op, input),
      });
    } catch (cause: unknown) {
      setEffect(null);
      const refused = describeSubjectRefusal(cause, (input.subjectIds ?? []).length > 0);
      if (notifyInsufficientCredits(cause)) {
        // Refused for lack of credits: the notification explains it and links to billing.
      } else if (refused?.kind === 'consent') {
        // Nothing was sent or spent: ask once, record the answer, then run the same thing again.
        setMediaConsent({ op, input, busy: false, failed: false });
      } else if (refused) {
        setMessage({ tone: 'error', text: subjectRefusalText(refused) });
      } else if (isOperationUnavailable(cause) && runsOnSubjectRoute(op, input)) {
        // The run with subjects cannot happen here; the tool without them may still work, so only that route is marked.
        setSubjectRoute((prev) => ({ ...prev, [op]: { unavailable: errorText(t, cause, t('noProviderFallback')) } }));
      } else if (isOperationUnavailable(cause)) {
        setUnavailable((prev) => ({ ...prev, [op]: errorText(t, cause, t('noProviderFallback')) }));
      } else {
        fail(cause, t('errors.opFailed', { title: info.title }));
        refetchAfter(cause, info.title, knownBefore);
      }
    } finally {
      setRunningOp(null);
      setBusyOp(null);
    }
  };

  /** The explicit yes: record the project's consent, then run what was waiting for it. */
  const grantMediaConsent = async () => {
    const waiting = mediaConsent;
    if (!waiting) return;
    setMediaConsent({ ...waiting, busy: true, failed: false });
    try {
      await grantAvatarMediaConsent(projectId);
      void globalMutate(avatarMediaConsentPath(projectId));
    } catch {
      setMediaConsent({ ...waiting, busy: false, failed: true });
      return;
    }
    setMediaConsent(null);
    await runOperation(waiting.op, waiting.input);
  };

  // ── the bar after a paid run ────────────────────────────────────────────────────────────────
  const resultCanUndo = !!result && editor.canUndo && editor.undoLabel === result.info.step;
  const chooseOption = (index: number) => {
    if (!result) return;
    setActiveOption(index);
    setSelectedId(result.info.layerIds[index] ?? null);
  };
  const keepOneOption = () => {
    if (!result) return;
    const ids = result.info.layerIds;
    const keep = ids[activeOption];
    const others = ids.filter((id) => id !== keep);
    if (!keep) return;
    commit((d) => sequence(t('history.keepOne'), [patchLayer(d, keep, { visible: true }, t('history.keepOne')), layersRemoved(others, t('history.keepOne'))].filter((c): c is NonNullable<typeof c> => c !== null)));
    setSelectedId(keep);
    setResult(null);
    setMessage({ tone: 'info', text: t('notes.keptOption') });
  };
  const keepAllOptions = () => {
    if (!result) return;
    commit((d) => patchLayers(d, result.info.layerIds.map((id) => ({ id, patch: { visible: true } })), t('history.keepAll')));
    setResult(null);
  };
  const tryAgain = result?.retry
    ? () => {
        const again = result.retry;
        editor.undo();
        setResult(null);
        again?.();
      }
    : undefined;

  const confirmDelete = async () => {
    if (!pendingDelete) return;
    setDeleting(true);
    try {
      await editor.deleteLayer(pendingDelete);
      if (selectedId === pendingDelete) setSelectedId(null);
      if (maskLayerId === pendingDelete) startMask(null);
      setPendingDelete(null);
    } catch (cause: unknown) {
      fail(cause, t('errors.layerRemove'));
      setPendingDelete(null);
    } finally {
      setDeleting(false);
    }
  };

  // ── designed graphics ───────────────────────────────────────────────────────────────────────
  const designGraphic = async (request: CreateImageGraphicRequest) => {
    setMessage(null);
    // Pending edits go first, so the designer sees (and the revision is checked against) the picture
    // as it is on screen.
    if (!(await editor.flush())) {
      throw new Error(t('errors.designFlush'));
    }
    const job = await createImageGraphic(projectId, documentId, request);
    setGraphicStarts((prev) => ({ ...prev, [job.id]: clock() }));
    await graphics.mutate(
      (cur) => (cur ? { ...cur, graphics: [job, ...cur.graphics.filter((g) => g.id !== job.id)] } : cur),
      { revalidate: true },
    );
  };

  /** Shows layers someone else just added or changed: selects the first and brings them into focus. */
  const revealLayers = useCallback((ids: readonly string[], text: string) => {
    if (ids.length > 0) {
      setEffect((cur) => cur ?? { phase: 'revealing', target: { kind: 'layers', ids }, startedAt: clock(), waitFor: ids });
      setSelectedId(ids[0]);
    }
    setMessage({ tone: 'info', text });
  }, []);

  // A design that finished is inserted on the server when the list is read; bring its layer in as
  // one undoable step, with the reveal, once per job.
  const { syncFromServer } = editor;
  const graphicJobs = graphics.data?.graphics;
  const busyJobs = useMemo<BusyJob[]>(() => {
    const jobs: BusyJob[] = busyOp ? [{ key: busyOp.key, layerId: busyOp.layerId, job: null }] : [];
    for (const job of graphicJobs ?? []) {
      if ((job.status === 'Queued' || job.status === 'Running') && job.revisesLayerId) {
        jobs.push({ key: `graphic:${job.id}`, layerId: job.revisesLayerId, job });
      }
    }
    return jobs;
  }, [busyOp, graphicJobs]);
  const busyIds = useMemo(() => new Set(busyJobs.map((b) => b.layerId)), [busyJobs]);
  useEffect(() => {
    busyRef.current = busyJobs;
  }, [busyJobs]);
  // Hidden outputs waiting for an OK: from an operation answer, or from a graphic read that says so (this
  // survives a reload). An operation has no read route, so after a reload its hidden output is just a hidden layer.
  // Remembered across a reload; an id goes once its layer is gone or shown.
  const loaded = doc !== null && doc !== undefined;
  useEffect(() => {
    if (!loaded) return;
    const hidden = new Set(layers.filter((l) => !l.visible).map((l) => l.id));
    const kept = reviewIds.filter((id) => hidden.has(id));
    if (kept.length !== reviewIds.length) setReviewIds(kept);
    else savePictureReview(documentId, kept);
  }, [loaded, layers, reviewIds, documentId]);
  const reviewLayerIds = useMemo(() => {
    const hidden = new Set(layers.filter((l) => !l.visible).map((l) => l.id));
    const ids = [...reviewIds, ...(graphicJobs ?? []).flatMap((j) => (j.needsReview && j.layerId ? [j.layerId] : []))];
    return [...new Set(ids)].filter((id) => hidden.has(id));
  }, [layers, reviewIds, graphicJobs]);
  const applyShow = (ids: readonly string[]) => {
    commit((d) => patchLayers(d, ids.map((id) => ({ id, patch: { visible: true } })), t('history.reviewShow')));
    setSelectedId(ids[0] ?? null);
    setReviewIds((cur) => cur.filter((id) => !ids.includes(id)));
  };
  const applyDiscard = (ids: readonly string[]) => {
    commit(() => layersRemoved(ids, t('history.reviewDiscard')));
    setReviewIds((cur) => cur.filter((id) => !ids.includes(id)));
  };
  /** Shows a kept result in the tray's "Needs your OK" too; its Apply / Discard do what the bar's buttons do. */
  const mirrorReview = (jobId: string, tool: string, ids: readonly string[]) => {
    ids.forEach((id) => reviewJobs.current.set(id, jobId));
    markNeedsReview(jobId, {
      summaryKey: 'editor.pictureNeedsReview',
      summaryValues: { tool },
      onApply: () => applyShow(ids),
      onDiscard: () => applyDiscard(ids),
    });
  };
  // Every kept-hidden result is offered in the tray too: a finished design whose layer was deleted, and a layer
  // remembered across a reload. The job id is derived from the graphic / layer, so reopening the editor re-finds
  // the same tray row instead of adding another.
  const mirroredLayers = useRef(new Set<string>());
  useEffect(() => {
    for (const layerId of reviewLayerIds) {
      if (reviewJobs.current.has(layerId) || mirroredLayers.current.has(layerId)) continue;
      mirroredLayers.current.add(layerId);
      const graphic = (graphicJobs ?? []).find((j) => j.needsReview && j.layerId === layerId);
      const title = graphic?.description ?? layers.find((l) => l.id === layerId)?.label ?? '';
      void trackJob(
        {
          id: graphic ? `picreview:graphic:${graphic.id}` : `picreview:layer:${documentId}:${layerId}`,
          type: graphic ? 'graphic' : 'pictureOperation',
          title,
          quietTitle: true,
          projectId,
          executionId: graphic?.executionId,
          href: `/editor/thumbnails/${documentId}`,
        },
        (_signal, jobId) => {
          mirrorReview(jobId, title, [layerId]);
          return Promise.resolve();
        },
      );
    }
    // mirrorReview only reads refs and stable callbacks
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graphicJobs, reviewLayerIds]);
  /** The bar and the tray are one decision: resolving through the tray job runs the same callbacks. */
  const settleReview = (outcome: 'applied' | 'discarded') => {
    const waiting = new Set(allJobs.filter((j) => j.state === 'needsReview').map((j) => j.id));
    const viaTray = new Set<string>();
    const direct: string[] = [];
    for (const id of reviewLayerIds) {
      const jobId = reviewJobs.current.get(id);
      if (jobId && waiting.has(jobId)) viaTray.add(jobId);
      else direct.push(id);
    }
    viaTray.forEach((jobId) => void resolveTrackedJob(jobId, outcome));
    if (direct.length > 0) (outcome === 'applied' ? applyShow : applyDiscard)(direct);
  };
  // A tray decision removes or shows the layers, so the bar goes by itself (it follows the hidden layers).
  const showReviewed = () => settleReview('applied');
  const discardReviewed = () => settleReview('discarded');
  const stopBusy = async () => {
    const job = ask?.job;
    if (!job) return;
    try {
      await stopExecution(projectId, job.workflowId, job.executionId);
      setAsk(null);
      await graphics.mutate();
    } catch (cause: unknown) {
      setAsk(null);
      fail(cause, t('errors.opFailed', { title: job.description }));
    }
  };
  useEffect(() => {
    if (!graphicJobs || !doc) return;
    const due = jobsToAdopt(graphicJobs, new Set(layers.map((l) => l.id)), settledGraphics.current);
    if (due.length === 0) return;
    due.forEach((job) => settledGraphics.current.add(job.id));
    const ids = due.flatMap((job) => (job.layer ? [job.layer.id] : []));
    void (async () => {
      try {
        const result = await syncFromServer({ kind: 'layersAdded', layerIds: ids, label: t('history.designGraphic') });
        if (!result.synced) {
          // Nothing was brought in (an unsaved edit could not be sent); try again on the next read.
          due.forEach((job) => settledGraphics.current.delete(job.id));
          return;
        }
        if (result.present.length === 0) return;
        const last = due[due.length - 1];
        const summary = last.summary ? ` ${last.summary}` : '';
        revealLayers(
          result.present,
          t('notes.graphicReady', { count: due.length, summary }),
        );
      } catch (cause: unknown) {
        due.forEach((job) => settledGraphics.current.delete(job.id));
        setMessage({ tone: 'error', text: errorText(t, cause, t('errors.graphicAdd')) });
      }
    })();
  }, [graphicJobs, doc, layers, syncFromServer, revealLayers, t]);

  // While a design is working, the place it will land is shown working too — unless another AI tool's
  // effect is on screen, which always wins.
  const activeJob = activeGraphicJob(graphicJobs);
  const activeRegion = activeJob ? graphicTargetRegion(activeJob) : null;
  const activeStart = activeJob ? (graphicStarts[activeJob.id] ?? 0) : 0;
  const rx = activeRegion?.x;
  const ry = activeRegion?.y;
  const rw = activeRegion?.w;
  const rh = activeRegion?.h;
  const graphicEffect = useMemo<AiEffect | null>(
    () =>
      rx !== undefined && ry !== undefined && rw !== undefined && rh !== undefined
        ? { phase: 'working', target: { kind: 'region', bounds: { x: rx, y: ry, w: rw, h: rh } }, startedAt: activeStart }
        : null,
    [rx, ry, rw, rh, activeStart],
  );
  const shownEffect = effect ?? graphicEffect;

  // ── the assistant's changes ─────────────────────────────────────────────────────────────────
  const { mutate: refreshGraphics } = graphics;
  const applyAssistantChange = useCallback(
    async (change: ImageDocumentChange) => {
      const said = change.summary ? t('notes.assistantSaid', { summary: change.summary }) : t('notes.assistantChanged');
      if (change.kind === 'graphicQueued') {
        await refreshGraphics();
        setMessage({ tone: 'info', text: said });
        return;
      }
      try {
        const result = await syncFromServer({
          kind: change.kind,
          layerIds: change.layerIds,
          label: change.summary ? t('history.assistant', { summary: change.summary }) : t('history.assistantChange'),
        });
        if (!result.synced) return;
        revealLayers(result.present, said);
      } catch (cause: unknown) {
        setMessage({ tone: 'error', text: errorText(t, cause, t('errors.assistantReload')) });
      }
    },
    [refreshGraphics, revealLayers, syncFromServer, t],
  );
  useEffect(
    () =>
      onImageDocumentChange((change) => {
        if (change.documentId === documentId) void applyAssistantChange(change);
      }),
    [applyAssistantChange, documentId],
  );

  // ── export ──────────────────────────────────────────────────────────────────────────────────
  // A layer whose image is gone (the server's 404 for a missing image) is left out of the export, and
  // the dialog says so. A layer that could not be loaded for any other reason blocks the export: it
  // may well be there, and leaving it out would make a silently wrong file.
  const visibleBitmapLayers = layers.filter((l) => l.visible && l.kind !== 'text');
  const unloadable = visibleBitmapLayers.filter((l) => unloadableIds.has(l.id));
  const waitingOn = visibleBitmapLayers.filter((l) => !bitmaps.has(l.id) && !missing.has(l.id) && !unloadableIds.has(l.id));
  const leftOut = visibleBitmapLayers.filter((l) => missing.has(l.id));
  const quoted = (l: { label: string }) => t('export.quoted', { name: l.label });
  const exportBlocker =
    unloadable.length > 0
      ? unloadable.length === 1
        ? t('export.unloadableOne', { name: quoted(unloadable[0]) })
        : t('export.unloadableMany', { count: unloadable.length, names: unloadable.map(quoted).join(', ') })
      : waitingOn.length > 0
        ? t('export.waiting', { what: waitingOn.length === 1 ? quoted(waitingOn[0]) : t('export.layersCount', { count: waitingOn.length }) })
        : null;
  const exportNotice =
    leftOut.length === 0
      ? null
      : leftOut.length === 1
        ? t('export.leftOutOne', { name: quoted(leftOut[0]) })
        : t('export.leftOutMany', { names: leftOut.map(quoted).join(', ') });

  // A visible layer made with an avatar that asks for a visible label: the exported picture says "AI-generated".
  const aiLabelNeeded = needsAiLabel(layers);

  const runExport = async ({ format, quality, fileName, destination }: ExportRequest): Promise<string> => {
    if (!doc) throw new Error(t('errors.pictureOpening'));
    if (!(await editor.flush())) throw new Error(t('errors.exportFlush'));
    await ensureFontsFor(layers.flatMap((l) => (l.kind === 'text' && l.content ? [l.content] : [])));
    const info = exportFormat(format);
    const blob = await (aiLabelNeeded ? renderExport(format, doc, bitmaps, quality, { aiLabel: t('export.aiLabel') }) : renderExport(format, doc, bitmaps, quality));
    const size = `${doc.canvasWidth} × ${doc.canvasHeight}`;
    if (destination === 'download') {
      downloadBlob(blob, fileName);
      return t('export.downloaded', { fileName, size, bytes: formatBytes(blob.size) });
    }
    if (blob.size > MAX_EXPORT_BYTES) {
      throw new Error(
        t('errors.exportTooBig', { size: formatBytes(blob.size), max: formatBytes(MAX_EXPORT_BYTES) }),
      );
    }
    const key = await uploadEditorAsset(projectId, documentId, 'export', blob, info.mime as ExportContentType);
    const result = await createImageExport(projectId, documentId, {
      key,
      width: doc.canvasWidth,
      height: doc.canvasHeight,
      fileName,
    });
    return t('export.saved', { fileName: result.projectFile?.originalFileName ?? fileName, size });
  };

  /** The size a file would have, found by really encoding it; nothing is saved. */
  const estimateExport = useCallback(
    async (format: ExportFormatId, quality: number): Promise<number> => {
      if (!doc) throw new Error(t('errors.pictureOpening'));
      await ensureFontsFor(layers.flatMap((l) => (l.kind === 'text' && l.content ? [l.content] : [])));
      return (await (aiLabelNeeded ? renderExport(format, doc, bitmaps, quality, { aiLabel: t('export.aiLabel') }) : renderExport(format, doc, bitmaps, quality))).size;
    },
    [bitmaps, doc, layers, t, aiLabelNeeded],
  );

  const imageOptions = useMemo(
    () =>
      (projectFiles ?? [])
        .filter((f) => isEditableImageType(f.mimeType))
        .map((f) => ({ value: f.id, label: f.originalFileName })),
    [projectFiles],
  );

  /**
   * A reference picture picked from the device becomes an ordinary project file through the same upload
   * the rest of the app uses — never a second way into storage — and the panel then selects it. The
   * project's file list is refreshed so it is also offered by name next time.
   */
  const uploadReferencePicture = useCallback(
    async (file: File) => {
      const uploaded = await uploadFile(projectId, file);
      void refreshProjectFiles();
      return { value: uploaded.id, label: uploaded.originalFileName };
    },
    [projectId, refreshProjectFiles],
  );

  // ── render ──────────────────────────────────────────────────────────────────────────────────
  if (editor.loadError) {
    return (
      <Alert color="red" variant="light" title={t('loadFailed')}>
        <Text size="sm">{editor.loadError}</Text>
        <Button mt="sm" size="xs" fz="xs" h={coarse ? 44 : undefined} variant="default" onClick={() => void editor.reload()}>
          {t('tryAgain')}
        </Button>
      </Alert>
    );
  }
  if (editor.loading || !doc) {
    return (
      <Group gap="xs" py="xl" justify="center">
        <Loader size="sm" />
        <Text size="sm" c="dimmed">
          {t('opening')}
        </Text>
      </Group>
    );
  }

  const selectedBitmapLayer = selectedLayer && selectedLayer.kind !== 'text' ? selectedLayer : null;
  const subjectBlocker = !subjectLayer
    ? t('subjectBlocker.noLayer')
    : subjectLayer.crop
      ? t('croppedTool')
      : subjectBusy
      ? t('subjectBlocker.working')
      : null;

  const saveLabel =
    editor.saveStatus === 'saving'
      ? t('save.saving')
      : editor.saveStatus === 'unsaved'
        ? t('save.unsaved')
        : editor.saveStatus === 'error'
          ? t('save.error')
          : t('save.saved');

  const tools: { id: ToolId; label: string; icon: EditorIconComponent }[] = [
    { id: 'select', label: t('tools.select'), icon: IconPointer },
    { id: 'hand', label: t('tools.hand'), icon: IconHandStop },
    { id: 'text', label: t('tools.text'), icon: IconTypography },
    { id: 'subject', label: t('tools.subject'), icon: IconScissors },
    { id: 'brush', label: t('tools.brush'), icon: IconBrush },
  ];

  const panelTabs = (
    <Tabs value={panel} onChange={(v) => v && setPanelState(v as PanelId)} keepMounted={false}>
      {!phone && (
        <Tabs.List mb="sm" grow>
          <Tabs.Tab value="layers">{t('dock.layers')}</Tabs.Tab>
          <Tabs.Tab value="text">{t('dock.text')}</Tabs.Tab>
          <Tabs.Tab value="ai">{t('dock.ai')}</Tabs.Tab>
          <Tabs.Tab value="canvas">{t('dock.canvas')}</Tabs.Tab>
        </Tabs.List>
      )}

            <Tabs.Panel value="layers">
              <Stack gap="sm">
                {maskEdit.session && (
                  <LayerMaskBar
                    layerLabel={layers.find((l) => l.id === maskEdit.session?.layerId)?.label ?? t('thisLayer')}
                    brush={maskEdit.brush}
                    onBrush={maskEdit.setBrush}
                    canUndo={maskEdit.canUndo}
                    canRedo={maskEdit.canRedo}
                    onUndo={maskEdit.undo}
                    onRedo={maskEdit.redo}
                    changed={maskEdit.session.buffer.changed}
                    saveState={maskEdit.saveState}
                    onDone={() => void maskEdit.done()}
                    onDiscard={maskEdit.discard}
                    showMaskOnly={maskEdit.maskOnlyId === maskEdit.session.layerId}
                    onToggleMaskOnly={() => maskEdit.toggleMaskOnly(maskEdit.session?.layerId ?? '')}
                    coarse={coarse}
                  />
                )}
                <LayersPanel
                  layers={layers}
                  selectedId={selectedId}
                  onSelect={select}
                  onPatch={onPatch}
                  onReorder={
                    phone
                      ? (id, position) => {
                          onReorder(id, position);
                          undoable.show({ message: t('layerMoved'), onUndo: () => editor.undo() });
                        }
                      : onReorder
                  }
                  menuRows={phone}
                  masks={{
                    busyId: maskEdit.busyId,
                    paintingId: maskEdit.session?.layerId ?? null,
                    maskOnlyId: maskEdit.maskOnlyId,
                    onAction: (action, layerId) => {
                      const target = layers.find((l) => l.id === layerId);
                      if (!target) return;
                      if (action === 'only') maskEdit.toggleMaskOnly(layerId);
                      else if (action === 'add') void maskEdit.addMask(target);
                      else if (action === 'edit') {
                        setSelectedId(layerId);
                        maskEdit.editMask(target);
                      } else if (action === 'invert') void maskEdit.invertMask(target);
                      else void maskEdit.deleteMask(target);
                    },
                  }}
                  onDelete={(id) => guard(id, () => setPendingDelete(id))}
                  busy={busyIds}
                  missing={missing}
                  coarse={coarse}
                  onUploadImage={(f) => void uploadImageLayer(f)}
                  uploadPercent={uploadPercent}
                  uploadError={uploadError}
                  canRevise={(layer) => revisableGraphic(layer) !== null}
                  onRevise={(id) => {
                    select(id);
                    setPanel('ai');
                    pickAiTool('designGraphic');
                  }}
                  picking={arrange.picking}
                  checked={arrange.checked}
                  onToggleChecked={arrange.toggleChecked}
                />
                {layers.length > 0 && (
                  <ArrangePanel
                    layer={arrange.selectedPlaced}
                    count={arrange.count}
                    picking={arrange.picking}
                    onPicking={arrange.setPicking}
                    onField={arrange.onField}
                    onAlign={arrange.onAlign}
                    onNudge={onNudgeSelected}
                    onDistribute={arrange.onDistribute}
                    onCrop={() => {
                      const target = arrange.selectedPlaced;
                      if (target) guard(target.id, arrange.openCrop);
                    }}
                    canCrop={!!arrange.selectedPlaced && arrange.selectedPlaced.kind !== 'text' && !arrange.selectedPlaced.locked}
                    onMerge={() => void arrange.merge()}
                    merging={arrange.busy === 'merge'}
                    coarse={coarse}
                  />
                )}
                {arrange.cropping && arrange.selectedPlaced && arrange.selectedPlaced.kind !== 'text' && (
                  <CropDialog
                    opened
                    onClose={arrange.closeCrop}
                    imageUrl={arrange.selectedPlaced.imageUrl}
                    width={Math.round(naturalSize(arrange.selectedPlaced).w)}
                    height={Math.round(naturalSize(arrange.selectedPlaced).h)}
                    initial={cropToRect(arrange.selectedPlaced.crop, naturalSize(arrange.selectedPlaced))}
                    label={arrange.selectedPlaced.label}
                    busy={false}
                    coarse={coarse}
                    onApply={(rect) => arrange.crop(rect)}
                  />
                )}
                <Stack gap={6}>
                  {layers.length > 0 && (
                    <UploadImageButton
                      onUpload={(f) => void uploadImageLayer(f)}
                      percent={uploadPercent}
                      error={uploadError}
                      coarse={coarse}
                    />
                  )}
                  {coarse ? (
                    // A dropdown does not open by touch on Android Chrome inside this scrolling panel
                    // (m4 N1), so a touch screen gets the images as a list of buttons to tap.
                    <ImageTapList options={imageOptions} value={addImageId} onChange={setAddImageId} />
                  ) : (
                    <Select
                      size="xs"
                      mih={coarse ? 44 : undefined}
                      searchable
                      label={t('addImage.label')}
                      placeholder={imageOptions.length ? t('addImage.pick') : t('addImage.none')}
                      data={imageOptions}
                      value={addImageId}
                      onChange={setAddImageId}
                      disabled={imageOptions.length === 0}
                    />
                  )}
                  <Button size="xs" fz="xs" variant="light" fullWidth h={coarse ? 44 : undefined} disabled={!addImageId} onClick={() => void addProjectImage(addImageId)}>
                    {t('addImage.add')}
                  </Button>
                  <Text size="12px" c="dimmed">
                    {t('addImage.hint')}
                  </Text>
                </Stack>
              </Stack>
            </Tabs.Panel>

            <Tabs.Panel value="text">
              <TextPanel
                layerId={selectedLayer?.kind === 'text' ? selectedLayer.id : null}
                content={selectedLayer?.kind === 'text' ? selectedLayer.content : null}
                onAdd={() => void addText()}
                phone={phone}
                onChange={(content: TextContent, mergeKey: string) => {
                  if (selectedLayer) onPatch(selectedLayer.id, { content }, t('history.editText'), mergeKey);
                }}
              />
            </Tabs.Panel>

            {/* Kept mounted (hidden) so what was typed into a tool survives a look at another tab. */}
            <Tabs.Panel value="ai" keepMounted>
              <AiPanel
                tool={aiTool}
                onPickTool={pickAiTool}
                layer={selectedLayer}
                layerSize={selectedLayerSize}
                hasMask={hasMask}
                canvas={{ width: doc.canvasWidth, height: doc.canvasHeight }}
                capabilities={capabilities}
                unavailable={unavailable}
                canConfigure={canConfigure}
                graphicUnavailable={graphicAvailability}
                lastRun={lastRun}
                running={runningOp}
                referencePictures={imageOptions}
                onUploadReference={uploadReferencePicture}
                subjects={avatarsOffered ? { projectId, avatars: projectAvatars ?? [] } : null}
                subjectRoute={subjectRoute}
                onSubjectsChosen={onSubjectsChosen}
                onRun={(op, input) => void runOperation(op, input)}
                designControls={
                  <DesignGraphicPanel
                    data={graphics.data}
                    loadError={graphics.error ? errorText(t, graphics.error, t('errors.designsLoad')) : null}
                    canvas={{ width: doc.canvasWidth, height: doc.canvasHeight }}
                    revisable={revisableGraphic(selectedLayer)}
                    revisableLabel={selectedLayer?.label ?? null}
                    onDesign={designGraphic}
                  />
                }
                subjectControls={
                  <SubjectPanel
                    points={prompts.points}
                    boxes={prompts.boxes}
                    blocker={subjectBlocker}
                    targetLabel={subjectLayer?.label ?? null}
                    busy={subjectBusy}
                    onRemovePoint={(i) => setPrompts((cur) => ({ ...cur, points: cur.points.filter((_, j) => j !== i) }))}
                    onRemoveBox={(i) => setPrompts((cur) => ({ ...cur, boxes: cur.boxes.filter((_, j) => j !== i) }))}
                    onClear={() => setPrompts({ points: [], boxes: [] })}
                    onRun={(input) => void runSubjects(input)}
                    markMode={markMode}
                    onMarkMode={setMarkMode}
                    unavailable={unavailable.removeBackground}
                    canConfigure={canConfigure}
                    estimate={capabilities.removeBackground?.estimate ?? null}
                    chargedCredits={subjectCharged}
                  />
                }
                brushControls={
                  <BrushPanel
                    brush={brush}
                    onBrush={setBrush}
                    targetLabel={selectedBitmapLayer?.label ?? null}
                    hasMask={hasMask}
                    onClear={() => {
                      painter?.clear();
                      setMaskTick((n) => n + 1);
                    }}
                  />
                }
              />
            </Tabs.Panel>

            <Tabs.Panel value="canvas">
              <CanvasPanel
                canvasWidth={doc.canvasWidth}
                canvasHeight={doc.canvasHeight}
                backgroundColor={doc.backgroundColor}
                onCanvas={onCanvas}
                originalSize={photoOriginal}
                onOriginalSize={onOriginalSize}
                onRefit={layers.length > 0 ? onRefit : undefined}
              />
            </Tabs.Panel>
    </Tabs>
  );
  const dockPick = (id: PanelId) => {
    if (sheetOpen && panel === id) {
      setSheetOpen(false);
      return;
    }
    setPanel(id);
  };
  const phoneShell = phone ? (
    <div className={phoneClasses.pictureShell} data-landscape={landscape ? '' : undefined}>
      <div aria-hidden style={{ height: sheetOpen && !landscape ? `calc(var(--rb-dock-h) + ${SHEET_HEIGHTS[detent]})` : 'var(--rb-dock-h)' }} />
      <nav className={phoneClasses.dock} aria-label={t('dockLabel')} data-testid="picture-dock">
        {PICTURE_DOCK.map(({ id }) => (
          <UnstyledButton key={id} className={phoneClasses.dockItem} aria-pressed={sheetOpen && panel === id} onClick={() => dockPick(id)} data-testid={`picture-dock-${id}`}>
            <span>{t(`dock.${id}`)}</span>
          </UnstyledButton>
        ))}
      </nav>
      <BottomSheet
        opened={sheetOpen}
        onClose={() => setSheetOpen(false)}
        title={t(`panelTitle.${panel}`)}
        detent={detent}
        onDetentChange={setDetent}
        side={landscape ? 'right' : 'bottom'}
        testId="picture-sheet"
        keepMounted
        footerRef={setRunFooter}
      >
        <RunFooterContext.Provider value={panel === 'ai' ? runFooter : null}>
          <div className={phoneClasses.sheetStack}>{panelTabs}</div>
        </RunFooterContext.Provider>
      </BottomSheet>
      <UndoSnackbar />
    </div>
  ) : null;

  return (
    <Stack gap="sm">
      <Group justify="space-between" wrap="wrap" gap="xs">
        <Group gap="xs">
          <Tooltip label={editor.undoLabel ? t('undoTip', { label: editor.undoLabel }) : t('undoNone')}>
            <EditorIcon tooltip={false} icon={IconArrowBackUp} size="lg" variant="default" label={t('undo')} disabled={!editor.canUndo} onClick={editor.undo} />
          </Tooltip>
          <Tooltip label={editor.redoLabel ? t('redoTip', { label: editor.redoLabel }) : t('redoNone')}>
            <EditorIcon tooltip={false} icon={IconArrowForwardUp} size="lg" variant="default" label={t('redo')} disabled={!editor.canRedo} onClick={editor.redo} />
          </Tooltip>
          <Badge
            variant="light"
            color={editor.saveStatus === 'error' ? 'red' : editor.saveStatus === 'saved' ? 'green' : 'yellow'}
            role="status"
          >
            {saveLabel}
          </Badge>
        </Group>
        <Group gap="xs">
          {!phone && (
            <Text size="xs" c="dimmed">
              {doc.canvasWidth} × {doc.canvasHeight}
            </Text>
          )}
          <Button
            size="xs" fz="xs"
            h={coarse ? 44 : undefined}
            data-testid="picture-export"
            leftSection={<IconFileExport size={16} />}
            onClick={() => {
              retryBitmaps();
              setExportOpen(true);
            }}
          >
            {t('export.button')}
          </Button>
        </Group>
      </Group>

      {editor.notice && (
        <Alert color="yellow" variant="light" withCloseButton onClose={editor.dismissNotice}>
          {editor.notice}
        </Alert>
      )}
      {editor.saveError && (
        <Alert color="red" variant="light" title={t('saveFailed')}>
          <Text size="sm">{editor.saveError}</Text>
          <Button mt="xs" size="compact-xs" fz="xs" h={coarse ? 44 : undefined} variant="default" onClick={() => void editor.flush()}>
            {t('tryAgain')}
          </Button>
        </Alert>
      )}
      {message && (
        <Alert
          color={message.tone === 'error' ? 'red' : message.tone === 'warning' ? 'yellow' : 'blue'}
          variant="light"
          withCloseButton
          onClose={() => setMessage(null)}
        >
          {message.text}
        </Alert>
      )}

      {reviewLayerIds.length > 0 && <ReviewBar onShow={showReviewed} onDiscard={discardReviewed} />}

      {result && (
        <ResultBar
          info={result.info}
          options={result.options}
          activeOption={activeOption}
          onOption={chooseOption}
          comparing={comparing}
          onCompare={setComparing}
          canUndo={resultCanUndo}
          onUndo={() => {
            editor.undo();
            setResult(null);
            setComparing(false);
          }}
          onTryAgain={tryAgain}
          tryAgainCost={result.op ? estimateLine(capabilities[result.op]?.estimate) : null}
          tryAgainDisabled={runningOp !== null}
          onKeepOne={keepOneOption}
          onKeepAll={keepAllOptions}
          onDismiss={() => {
            setResult(null);
            setComparing(false);
          }}
          coarse={coarse}
        />
      )}

      <div ref={layoutRef} className={phone ? classes.layoutPhone : classes.layout} data-landscape={phone && landscape ? '' : undefined} data-sheet={phone && sheetOpen ? '' : undefined} data-testid="picture-layout">
        <div className={classes.tools} role="toolbar" aria-label={t('toolsLabel')} aria-orientation="vertical">
          {tools.map((entry, i) => (
            <React.Fragment key={entry.id}>
              {i === 3 && <div className={classes.toolSeparator} aria-hidden />}
              <Tooltip label={entry.label} position="right">
                <EditorIcon
                  icon={entry.icon}
                  size="lg"
                  tooltip={false}
                  variant={tool === entry.id ? 'filled' : 'default'}
                  label={entry.label}
                  aria-pressed={tool === entry.id}
                  onClick={() => chooseTool(entry.id)}
                />
              </Tooltip>
            </React.Fragment>
          ))}
        </div>

        <div className={phone ? classes.stagePhone : classes.stage} data-stage data-testid="picture-stage" style={stageH !== null ? { height: stageH } : undefined}>
          <EditorCanvas
            doc={canvasDoc ?? doc}
            bitmaps={maskEdit.liveBitmaps}
            fontVersion={fontVersion}
            selectedId={selectedId}
            busyIds={busyIds}
            tool={tool}
            onSelect={select}
            onTransform={onTransform}
            onPlaceText={(p) => void addText(p)}
            painter={painter}
            maskLayerId={maskLayerId}
            maskTick={maskTick + maskEdit.tick}
            brush={maskPainting ? { size: maskEdit.brush.size, erase: maskEdit.brush.hide } : brush}
            onMaskChange={() => (maskPainting ? maskEdit.changed() : setMaskTick((n) => n + 1))}
            layerMask={maskEdit.session ? { layerId: maskEdit.session.layerId, buffer: maskEdit.session.buffer, hardness: maskEdit.brush.hardness } : null}
            subjectLayerId={subjectLayerId}
            prompts={prompts}
            onAddPoint={(p: SubjectPointPrompt) => setPrompts((cur) => ({ ...cur, points: [...cur.points, p] }))}
            onAddBox={(b: SubjectBoxPrompt) => setPrompts((cur) => ({ ...cur, boxes: [...cur.boxes, b] }))}
            subjectLeaveOut={markMode === 'leaveOut'}
            effect={shownEffect}
            onEffectDone={() => setEffect((cur) => (cur?.phase === 'revealing' ? null : cur))}
            missing={missing}
          />
        </div>

        {!phone && <div className={classes.panel}>{panelTabs}</div>}
      </div>
      {phoneShell}

      <ExportDialog
        key={editor.name ?? 'picture'}
        opened={exportOpen}
        onClose={() => setExportOpen(false)}
        width={doc.canvasWidth}
        height={doc.canvasHeight}
        blocker={exportBlocker}
        notice={exportNotice}
        aiLabel={aiLabelNeeded}
        onRetry={unloadable.length > 0 ? retryBitmaps : undefined}
        defaultName={editor.name}
        onExport={runExport}
        onEstimate={estimateExport}
      />

      <MediaConsentDialog
        opened={mediaConsent !== null}
        busy={mediaConsent?.busy ?? false}
        failed={mediaConsent?.failed ?? false}
        onCancel={() => setMediaConsent(null)}
        onConfirm={() => void grantMediaConsent()}
      />

      <BusyGuardModal
        opened={ask !== null}
        onGoOn={goOn}
        onBack={() => setAsk(null)}
        onStop={ask?.job ? () => void stopBusy() : undefined}
      />

      <ConfirmModal
        opened={pendingDelete !== null}
        onClose={() => setPendingDelete(null)}
        onConfirm={() => void confirmDelete()}
        loading={deleting}
        title={t('deleteTitle')}
        message={t('deleteMessage')}
      />
    </Stack>
  );
}

/**
 * The project's images as a list of buttons, for a touch screen: tap one to choose it (tap again to
 * let it go), then "Add to the picture". Each row is a 44 px target.
 */
function ImageTapList({
  options,
  value,
  onChange,
}: {
  options: readonly { value: string; label: string }[];
  value: string | null;
  onChange(value: string | null): void;
}) {
  const t = useT('editor-thumbnail-a.editor.addImage');
  return (
    <Stack gap={4}>
      <Text size="sm" fw={500} id="add-image-list-label">
        {t('label')}
      </Text>
      {options.length === 0 ? (
        <Text size="xs" c="dimmed">
          {t('noneDot')}
        </Text>
      ) : (
        <Stack
          gap={4}
          role="group"
          aria-labelledby="add-image-list-label"
          style={{ maxHeight: 240, overflowY: 'auto' }}
        >
          {options.map((option) => {
            const chosen = option.value === value;
            return (
              <UnstyledButton
                key={option.value}
                aria-pressed={chosen}
                onClick={() => onChange(chosen ? null : option.value)}
                style={{
                  minHeight: 44,
                  padding: '8px 12px',
                  borderRadius: 8,
                  border: `1px solid ${chosen ? 'var(--mantine-color-blue-5)' : 'var(--mantine-color-default-border)'}`,
                  background: chosen ? 'rgba(77,171,247,0.10)' : 'transparent',
                }}
              >
                <Text size="sm" truncate>
                  {option.label}
                </Text>
              </UnstyledButton>
            );
          })}
        </Stack>
      )}
    </Stack>
  );
}
