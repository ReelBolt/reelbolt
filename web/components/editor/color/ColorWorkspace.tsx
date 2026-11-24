'use client';

import React, { createContext, useContext, useEffect, useId, useMemo, useState } from 'react';
import { Alert, Badge, Button, Divider, Group, Image, ScrollArea, Select, Slider, Stack, Tabs, Text, TextInput, Tooltip } from '@mantine/core';
import { IconCamera, IconChevronDown, IconChevronRight, IconPhoto, IconPlus, IconTrash, IconUsersGroup } from '@tabler/icons-react';
import { EditorEmpty } from '../ui/EditorEmpty';
import type { EditOp, PropertyKey, PropertyValue } from '@/lib/editing/engine';
import type { AdjustmentClip, ColorGroup, EditTimeline, GradeNode, GradeStack, GradeWindow, MediaClip, Track } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { colorGroupOf, colorGroupsFromLookGroups, readLookGroups, shotMatchPrimaries, withNode, type FrameStats } from '@/lib/editing/color/grade';
import { readShots } from '@/lib/editing/cut/cut';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useGradeLuts } from '../viewer/useGradeLuts';
import { sourceMatcher, useTimelineAnalysis } from '@/lib/editing/api/analysis';
import { GradeStackEditor } from './GradeStackEditor';
import { useGradePreview, type GradePreview, type GradePreviewTarget } from '@/lib/editing/color/grade-preview';
import { gradeChain, mutedStack, soloStack, type ChainNode, type ChainSegment } from '@/lib/editing/color/grade-nodes';
import { useGraphOpen, writeGraphOpen } from '@/lib/editing/color/colour-page-prefs';
import { GradeGraph } from './GradeGraph';
import { CompareBar, SoloLine } from './CompareBar';
import { CameraProfileControl } from './CameraProfileControl';
import type { MediaProbe } from '@/lib/editing/media/types';
import { sampleVideoFrames, trackFeature, trackToWindowOffsets } from '@/lib/editing/color/feature-tracker';
import { LookPanel } from './LookPanel';
import { EditorPanel } from '../ui/EditorPanel';
import { PageEmptyState } from '../ui/PageEmptyState';
import { EditorIcon } from '../ui';
import { Preview3sButton } from '../viewer/Preview3s';
import { classifyItem } from '@/lib/editing/preview/parity';
import { offersPreview3s } from '@/lib/editing/preview/preview3s';
import { useT } from '@/i18n';
import { DEFAULT_ADJUSTMENT_NAME, DEFAULT_ADJUSTMENTS_TRACK_NAME, DEFAULT_SHOT_MATCH_NAME, defaultGroupName, useDefaultNameText } from '../shell/default-names';


export interface GalleryStill {
  id: string;
  dataUrl: string;
  label: string;
  stats: FrameStats | null;
}

export interface ColorWorkspaceProps {
  doc: EditTimeline;
  projectId: string;
  seedStepResultId: string | null;
  readOnly: boolean;
  selection: readonly string[];
  playheadFrame: number;
  /** R10.4: the timeline the "Preview 3 s" render runs against (absent: the button is not offered). */
  timelineId?: string;
  /** R10.4: called as a preview starts, so the host can pause playback. */
  onPreview3sStart?: () => void;
  newId: () => string;
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => void;
  onOp: (op: EditOp, label?: string) => void;
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSelect: (itemId: string) => void;
  onSeek: (frame: number) => void;
  gradeBypass: boolean;
  onGradeBypass: (bypass: boolean) => void;
  getCanvas: () => HTMLCanvasElement | null;
  /** R10.3: tells the viewer which grade node's power window to draw handles for (null: none). */
  onWindowTarget?: (target: { itemId: string; nodeId: string } | null) => void;
  /** §8.2: "Edit window on the picture" enters the window tool (pauses and focuses the picture). */
  onActivateWindowTool?: () => void;
  /** The stills gallery and the reference wipe live in the editor (the viewer draws the wipe). */
  gallery: readonly GalleryStill[];
  onCaptureStill: (label: string) => Promise<void>;
  onRemoveStill: (id: string) => void;
  referenceId: string | null;
  onReference: (id: string | null) => void;
  wipe: number;
  onWipe: (fraction: number) => void;
  /** P9: the playable URL of a clip's media (proxy or original), for the window tracker. */
  mediaUrlFor?: (clip: MediaClip) => string | null;
  /** The probe facts of a clip's source (the Input strip's suggestion reads the camera-profile hint). */
  probeFor?: (clip: MediaClip) => MediaProbe | null;
  /**
   * §8.2: the current frame's statistics from the always-sampling scopes, so "Match to reference" works
   * whether or not the scopes are expanded and from any tab. Absent: the host has no scopes (tests).
   */
  getCurrentStats?: () => FrameStats | null;
}

export interface ColorPageProviderProps extends ColorWorkspaceProps {
  children?: React.ReactNode;
}

/** The six grade-tool tabs of the right zone (§5.6/§8.2). */
export type GradeToolTab = 'primaries' | 'curves' | 'lut' | 'secondaries' | 'groups' | 'gallery';

interface ColorPageState {
  p: ColorWorkspaceProps;
  groups: ColorGroup[];
  target: string;
  setTarget: (value: string) => void;
  targetGroup: ColorGroup | null;
  gradeTarget: MediaClip | AdjustmentClip | null;
  selectedClip: MediaClip | null;
  adjustments: AdjustmentClip[];
  lutFiles: readonly { id: string; name: string }[];
  preview3s: React.ReactNode;
  message: string | null;
  setMessage: (message: string | null) => void;
  selectedNodeId: string | null;
  setSelectedNodeId: (id: string | null) => void;
  toolTab: GradeToolTab;
  setToolTab: (tab: GradeToolTab) => void;
  writeClipGrade: (stack: GradeStack | null, label: string) => void;
  writeGroupGrade: (group: ColorGroup, stack: GradeStack | null, label: string) => void;
  createGroup: () => void;
  groupsFromLooks: () => void;
  addSelectedToGroup: (group: ColorGroup) => void;
  removeMember: (group: ColorGroup, clipId: string) => void;
  addAdjustment: () => void;
  shotMatch: () => void;
  /** P9: tracks a power window through the selected clip; undefined for a group or no playable media. */
  onTrackWindowFor: ((node: GradeNode) => Promise<GradeWindow | null>) | undefined;
  /** R10.3: publishes the selected node's window so the viewer can draw its handles (clip stacks only). */
  windowTargetForClip: { itemId: string; publish: (target: { itemId: string; nodeId: string } | null) => void } | undefined;
  /** Wave 4: the serial chain of the current target, in application order (the group's stack first). */
  chain: ChainSegment[];
  /** The chain key of the selected node (the node the tools edit), or null. */
  selectedChainKey: string | null;
  selectChainNode: (node: ChainNode) => void;
  /**
   * Wave 4: the soloed node of the current target, or null. View state only: it is never written, it is
   * previewed through the transient grade preview and it ends with Escape, with the page, and whenever the
   * target (the clip or the group) changes.
   */
  soloNode: GradeNode | null;
  soloChainKey: string | null;
  setSolo: (nodeId: string | null) => void;
  /** Hands a would-be stack (a wheel drag) to the viewer; while a node is soloed the solo stays applied on top. */
  previewStack: (stack: GradeStack | null) => void;
  /** Moves focus to the grade tools (the graph's Enter). */
  focusTools: () => void;
  toolsDomId: string;
  /**
   * The bypass as the user set it: the look panel's own brief bypass (taken to read the ungraded frame) is
   * not shown. `chooseBypass` is the user's choice, which wins over that brief bypass.
   */
  shownBypass: boolean;
  chooseBypass: (bypass: boolean) => void;
  setPanelBypass: (active: boolean) => void;
  userBypassSeq: number;
}

const ColorPageContext = createContext<ColorPageState | null>(null);

function useColorPage(): ColorPageState {
  const value = useContext(ColorPageContext);
  if (!value) throw new Error('The colour panels must render inside a ColorPageProvider');
  return value;
}

const noop = () => {};

/**
 * The Colour page's one state (§8.2): the grade target, node selection and tab, the message line and every
 * write helper. `ColorPageProvider` publishes it to the two zones (nodes left, grade tools right) so they
 * stay in step; the viewer/scopes zone reads only the props it needs.
 */
function useColorPageState(props: ColorWorkspaceProps): ColorPageState {
  const t = useT('editor-color.workspace');
  const { doc, projectId, seedStepResultId, readOnly, selection, newId, onSetProperty, onOp, onApply, onSelect, gallery, referenceId, mediaUrlFor } = props;
  const { data: files } = useProjectFiles(projectId);
  const gradeLuts = useGradeLuts(projectId, noop);
  const { data: analysis } = useTimelineAnalysis(projectId, seedStepResultId);
  const artifact = analysis?.artifact;
  const lutFiles = (files ?? []).filter((f) => /\.cube$/i.test(f.originalFileName)).map((f) => ({ id: f.id, name: f.originalFileName }));
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;

  const clips = doc.tracks.flatMap((t) => t.items).filter((i): i is MediaClip => i.type === 'mediaClip');
  // A picture and its linked camera sound are selected together: the look goes to the picture, so that pair counts as one clip.
  const pictureIds = new Set(doc.tracks.filter((t) => t.kind === 'video').flatMap((t) => t.items.map((i) => i.id)));
  const selectedPictures = clips.filter((c) => selection.includes(c.id) && pictureIds.has(c.id));
  const pairOnly =
    selectedPictures.length === 1 &&
    selectedPictures[0].linkId != null &&
    selection.every((id) => id === selectedPictures[0].id || clips.find((c) => c.id === id)?.linkId === selectedPictures[0].linkId);
  const selectedClip = selection.length === 1 ? clips.find((c) => c.id === selection[0]) ?? null : pairOnly ? selectedPictures[0] : null;
  const adjustments = doc.tracks.flatMap((t) => t.items).filter((i): i is AdjustmentClip => i.type === 'adjustmentClip');
  const selectedAdjustment = selection.length === 1 ? adjustments.find((a) => a.id === selection[0]) ?? null : null;
  /** The clip a look is written to: the selected media clip, or the selected adjustment clip (R10.8). */
  const gradeTarget: MediaClip | AdjustmentClip | null = selectedClip ?? selectedAdjustment;
  const groups = doc.colorGroups ?? [];
  const [target, setTarget] = useState<string>('clip');
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [toolTab, setToolTab] = useState<GradeToolTab>('primaries');
  const [message, setMessage] = useState<string | null>(null);
  const targetGroup = target === 'clip' ? null : groups.find((g) => g.id === target) ?? null;

  const writeClipGrade = (stack: GradeStack | null, label: string) => gradeTarget && onSetProperty(gradeTarget.id, 'grade', stack, label);
  const writeGroupGrade = (group: ColorGroup, stack: GradeStack | null, label: string) => onOp({ type: 'setColorGroup', groupId: group.id, group: { ...group, grade: stack } }, label);

  const createGroup = () => {
    const members = clips.filter((c) => selection.includes(c.id) && !colorGroupOf(doc, c.id)).map((c) => c.id);
    if (members.length === 0) {
      setMessage(t('selectUngrouped'));
      return;
    }
    const group: ColorGroup = { id: newId(), name: defaultGroupName(groups.length + 1), clipIds: members, grade: null };
    onOp({ type: 'setColorGroup', groupId: group.id, group }, t('createGroupLabel', { count: members.length }));
    setTarget(group.id);
  };
  const groupsFromLooks = () => {
    const track = doc.tracks.find((t) => t.items.some((i) => i.type === 'mediaClip'));
    if (!track) return;
    const created = colorGroupsFromLookGroups(doc, track.id, readShots(artifact), readLookGroups(artifact), fps, newId, sourceMatcher(analysis, doc));
    if (created.length === 0) {
      setMessage(t('noLookGroupMatch'));
      return;
    }
    onApply(created.map((g) => ({ type: 'setColorGroup', groupId: g.id, group: g }) as EditOp), t('groupsFromLooksLabel', { count: created.length }));
    setMessage(t('groupsCreated', { count: created.length }));
  };
  const addSelectedToGroup = (group: ColorGroup) => {
    const extra = clips.filter((c) => selection.includes(c.id) && !colorGroupOf(doc, c.id)).map((c) => c.id);
    if (extra.length === 0) return;
    onOp({ type: 'setColorGroup', groupId: group.id, group: { ...group, clipIds: [...group.clipIds, ...extra] } }, t('addToGroupLabel', { count: extra.length, group: group.name }));
  };
  const removeMember = (group: ColorGroup, clipId: string) => onOp({ type: 'setColorGroup', groupId: group.id, group: { ...group, clipIds: group.clipIds.filter((id) => id !== clipId) } }, t('removeFromGroupLabel', { group: group.name }));

  const addAdjustment = () => {
    const over = selectedClip ?? null;
    const start = over ? over.recordIn : props.playheadFrame;
    const end = over ? over.recordOut : props.playheadFrame + Math.round(2 * fps);
    const clip: AdjustmentClip = { id: newId(), type: 'adjustmentClip', name: DEFAULT_ADJUSTMENT_NAME, recordIn: start, recordOut: Math.max(start + 1, end), enabled: true, grade: null, effects: null, provenance: null };
    const track: Track = { id: newId(), kind: 'video', name: DEFAULT_ADJUSTMENTS_TRACK_NAME, height: 40, locked: false, enabled: true, mute: false, solo: false, items: [clip] };
    const videoTracks = doc.tracks.filter((t) => t.kind === 'video').length;
    onApply([{ type: 'addTrack', index: videoTracks, track }], t('addAdjustmentLabel'));
    onSelect(clip.id);
    setMessage(over ? t('adjustmentAddedOver', { name: over.name }) : t('adjustmentAddedAtPlayhead', { seconds: 2 }));
  };

  const shotMatch = () => {
    const reference = gallery.find((s) => s.id === referenceId)?.stats ?? null;
    // §8.2: the scopes sample the page whether or not they are expanded, so the current statistics are
    // always available here — no "keep the scopes open" prerequisite.
    const current = props.getCurrentStats?.() ?? null;
    if (!selectedClip) {
      setMessage(t('selectToMatch'));
      return;
    }
    if (!reference || !current) {
      setMessage(t('needReference'));
      return;
    }
    const node = { id: newId(), name: DEFAULT_SHOT_MATCH_NAME, enabled: true, primaries: shotMatchPrimaries(current, reference), curves: null, lut: null };
    writeClipGrade(withNode(selectedClip.grade, node), t('shotMatch'));
    setMessage(t('shotMatchAdded', { name: t('shotMatch') }));
  };

  const preview3s =
    props.timelineId && selectedClip && offersPreview3s(classifyItem(selectedClip, doc, { gradeLutStatus: gradeLuts.gradeLutStatus }).parity) ? (
      <Preview3sButton projectId={projectId} timelineId={props.timelineId} atFrame={props.playheadFrame} readOnly={readOnly} onBeforeStart={props.onPreview3sStart} />
    ) : null;

  const onTrackWindowFor = selectedClip && mediaUrlFor ? (node: GradeNode) => trackWindowThroughClip(selectedClip, node, mediaUrlFor, doc.settings, t('noPlayableMedia')) : undefined;
  const windowTargetForClip = !targetGroup && gradeTarget && props.onWindowTarget ? { itemId: gradeTarget.id, publish: props.onWindowTarget } : undefined;

  // ── Wave 4: the serial chain, and solo ────────────────────────────────────
  const activeStack: GradeStack | null = targetGroup ? targetGroup.grade ?? null : gradeTarget?.grade ?? null;
  const activeNodes = activeStack?.nodes ?? [];
  const chain = gradeChain(doc, gradeTarget, targetGroup);
  // The tools edit the selected node, or the first one when the selection is not in this stack.
  const effectiveNode = activeNodes.find((n) => n.id === selectedNodeId) ?? activeNodes[0] ?? null;
  const ownerKey = targetGroup ? `group:${targetGroup.id}` : gradeTarget ? `clip:${gradeTarget.id}` : null;
  const selectedChainKey = ownerKey && effectiveNode ? `${ownerKey}:${effectiveNode.id}` : null;
  const selectChainNode = (node: ChainNode) => {
    if (node.owner.kind === 'group') {
      if (target !== node.owner.id) setTarget(node.owner.id);
    } else if (target !== 'clip') setTarget('clip');
    setSelectedNodeId(node.node.id);
  };

  const [solo, setSoloState] = useState<{ ownerKey: string; nodeId: string } | null>(null);
  const soloNode = solo && solo.ownerKey === ownerKey ? activeNodes.find((n) => n.id === solo.nodeId) ?? null : null;
  // Another clip or group, or the node is gone: solo ends there and then (it never comes back on its own).
  if (solo && !soloNode) setSoloState(null);
  const setSolo = (nodeId: string | null) => setSoloState(nodeId && ownerKey ? { ownerKey, nodeId } : null);
  const soloNodeId = soloNode?.id ?? null;
  const soloChainKey = ownerKey && soloNodeId ? `${ownerKey}:${soloNodeId}` : null;

  const gradePreview = useGradePreview();
  const previewKind: GradePreviewTarget['kind'] | null = targetGroup ? 'group' : gradeTarget ? 'item' : null;
  const previewId = targetGroup?.id ?? gradeTarget?.id ?? null;
  // The other stack of the chain is switched off while a node is soloed, so ONLY that node shows.
  const clipGroup = !targetGroup && selectedClip ? colorGroupOf(doc, selectedClip.id) : null;
  const memberClip = targetGroup && selectedClip && targetGroup.clipIds.includes(selectedClip.id) ? selectedClip : null;
  const otherKind: GradePreviewTarget['kind'] | null = clipGroup?.grade ? 'group' : memberClip?.grade ? 'item' : null;
  const otherId = clipGroup?.grade ? clipGroup.id : memberClip?.grade ? memberClip.id : null;
  const otherStack: GradeStack | null = clipGroup?.grade ?? memberClip?.grade ?? null;
  const soloPreviewOf = (stack: GradeStack | null): GradePreview | null =>
    buildSoloPreview(stack, soloNodeId, previewKind, previewId, otherKind, otherId, otherStack);
  // Bumped when a drag's own preview ends while a node is soloed: the solo is then published again.
  const [soloRefresh, setSoloRefresh] = useState(0);
  const soloPreview = useMemo(
    () => (soloRefresh >= 0 ? buildSoloPreview(activeStack, soloNodeId, previewKind, previewId, otherKind, otherId, otherStack) : null),
    [activeStack, soloNodeId, previewKind, previewId, otherKind, otherId, otherStack, soloRefresh],
  );
  // The preview belongs to one document: `gradePreview` changes with it, and the solo is published again.
  useEffect(() => {
    if (!soloPreview) return undefined;
    gradePreview.set(soloPreview);
    return () => gradePreview.set(null);
  }, [gradePreview, soloPreview]);
  // Escape leaves solo (a control that uses Escape itself, such as a rename field, keeps it).
  useEffect(() => {
    if (!soloNodeId) return undefined;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setSoloState(null);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [soloNodeId]);
  const previewStack = (stack: GradeStack | null) => {
    if (!previewKind || !previewId) {
      gradePreview.set(null);
      return;
    }
    if (soloNodeId) {
      // A drag while a node is soloed shows the dragged stack through the solo. When the drag's preview ends,
      // the solo effect publishes the stored stack again; after the page has gone that update is a no-op, so
      // a panel's unmount can never leave a preview behind.
      if (stack) gradePreview.set(soloPreviewOf(stack));
      else setSoloRefresh((n) => n + 1);
      return;
    }
    gradePreview.set(stack ? { target: { kind: previewKind, id: previewId }, stack } : null);
  };

  const [panelBypass, setPanelBypass] = useState(false);
  const [userBypassSeq, setUserBypassSeq] = useState(0);
  const chooseBypass = (bypass: boolean) => {
    setUserBypassSeq((n) => n + 1);
    setPanelBypass(false);
    props.onGradeBypass(bypass);
  };

  const toolsDomId = useId();
  const focusTools = () => {
    if (toolTab === 'groups' || toolTab === 'gallery') setToolTab('primaries');
    // After the tab change has rendered: the selected tab is the tools' first stop.
    window.setTimeout(() => document.getElementById(toolsDomId)?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]')?.focus(), 0);
  };

  return {
    p: props,
    groups,
    target,
    setTarget,
    targetGroup,
    gradeTarget,
    selectedClip,
    adjustments,
    lutFiles,
    preview3s,
    message,
    setMessage,
    selectedNodeId,
    setSelectedNodeId,
    toolTab,
    setToolTab,
    writeClipGrade,
    writeGroupGrade,
    createGroup,
    groupsFromLooks,
    addSelectedToGroup,
    removeMember,
    addAdjustment,
    shotMatch,
    onTrackWindowFor,
    windowTargetForClip,
    chain,
    selectedChainKey,
    selectChainNode,
    soloNode,
    soloChainKey,
    setSolo,
    previewStack,
    focusTools,
    toolsDomId,
    shownBypass: props.gradeBypass && !panelBypass,
    chooseBypass,
    setPanelBypass,
    userBypassSeq,
  };
}

/**
 * The transient preview of a soloed node: the target's stack with only that node on, and the other stack of
 * the chain (the clip's colour group, or the selected member clip of a group) switched off. Never written.
 */
function buildSoloPreview(
  stack: GradeStack | null,
  soloNodeId: string | null,
  kind: GradePreviewTarget['kind'] | null,
  id: string | null,
  otherKind: GradePreviewTarget['kind'] | null,
  otherId: string | null,
  otherStack: GradeStack | null,
): GradePreview | null {
  if (!soloNodeId || !kind || !id) return null;
  const soloed = soloStack(stack, soloNodeId);
  if (!soloed) return null;
  return { target: { kind, id }, stack: soloed, also: otherKind && otherId && otherStack ? [{ target: { kind: otherKind, id: otherId }, stack: mutedStack(otherStack) }] : [] };
}

export function ColorPageProvider({ children, ...props }: ColorPageProviderProps) {
  const value = useColorPageState(props);
  return <ColorPageContext.Provider value={value}>{children}</ColorPageContext.Provider>;
}

/** Left zone (§5.6): the grade target and the node stack. The scopes and the tools live in their own zones. */
export function ColorNodesPanel() {
  const t = useT('editor-color.workspace');
  const tx = useT('editor.cameraProfile');
  const nameText = useDefaultNameText();
  const tGraph = useT('editor-color.graph');
  const s = useColorPage();
  const group = s.selectedClip ? colorGroupOf(s.p.doc, s.selectedClip.id) : null;
  const graphOpen = useGraphOpen();
  const hasChain = s.targetGroup !== null || s.gradeTarget !== null;

  return (
    <EditorPanel title={t('title')} testId="color-workspace" scroll={false}>
      <Stack gap="sm" id="inspector-section-colour">
        {s.preview3s}
        <Group justify="flex-end">
          <Select
            size="xs"
            data={[{ value: 'clip', label: s.gradeTarget ? t('targetClip', { name: s.gradeTarget.name }) : t('targetClipNone') }, ...s.groups.map((g) => ({ value: g.id, label: t('targetGroup', { name: g.name }) }))]}
            value={s.target}
            onChange={(v) => v && s.setTarget(v)}
            allowDeselect={false}
            aria-label={t('target')}
            w={220}
            data-testid="color-target"
          />
        </Group>

        {s.message && (
          <Text size="xs" c="dimmed" role="status" data-testid="color-message">
            {s.message}
          </Text>
        )}

        {hasChain && (
          <Stack gap={4} data-testid="color-graph-band" data-open={graphOpen}>
            <Group gap={4} wrap="nowrap">
              <Divider label={tGraph('title')} labelPosition="left" style={{ flex: 1 }} />
              <EditorIcon icon={graphOpen ? IconChevronDown : IconChevronRight} size="sm" dense label={graphOpen ? tGraph('collapse') : tGraph('expand')} aria-expanded={graphOpen} onClick={() => writeGraphOpen(!graphOpen)} data-testid="color-graph-toggle" />
            </Group>
            {graphOpen && <GradeGraph segments={s.chain} selectedKey={s.selectedChainKey} soloKey={s.soloChainKey} onSelect={s.selectChainNode} onFocusTools={s.focusTools} />}
          </Stack>
        )}

        {s.targetGroup ? (
          <GradeStackEditor
            key={s.targetGroup.id}
            mode="nodes"
            projectId={s.p.projectId}
            stack={s.targetGroup.grade}
            readOnly={s.p.readOnly}
            onChange={(stack, label) => s.writeGroupGrade(s.targetGroup!, stack, label)}
            onPreview={s.previewStack}
            newId={s.p.newId}
            lutFiles={s.lutFiles}
            subject={s.targetGroup.name}
            selectedNodeId={s.selectedNodeId}
            onSelectNode={s.setSelectedNodeId}
            soloNodeId={s.soloNode?.id ?? null}
            onSolo={s.setSolo}
          />
        ) : s.gradeTarget ? (
          <>
            {s.selectedClip && group && (
              <Text size="xs" c="dimmed" data-testid="color-clip-group-note">
                {t('inGroup', { name: nameText(group.name) })}
              </Text>
            )}
            {s.selectedClip && s.gradeTarget.id === s.selectedClip.id && (
              <Stack gap={4} data-testid="color-input-strip" id="inspector-section-camera">
                <Divider label={tx('input')} labelPosition="left" />
                <CameraProfileControl
                  clip={s.selectedClip}
                  doc={s.p.doc}
                  readOnly={s.p.readOnly}
                  probe={s.p.probeFor?.(s.selectedClip) ?? null}
                  onSet={(profile, label) => s.p.onSetProperty(s.selectedClip!.id, 'inputProfile', profile, label)}
                  onBatch={s.p.onApply}
                />
              </Stack>
            )}
            <GradeStackEditor
              key={s.gradeTarget.id}
              mode="nodes"
              projectId={s.p.projectId}
              stack={s.gradeTarget.grade}
              readOnly={s.p.readOnly}
              onChange={s.writeClipGrade}
              onPreview={s.previewStack}
              newId={s.p.newId}
              lutFiles={s.lutFiles}
              subject={nameText(s.gradeTarget.name)}
              selectedNodeId={s.selectedNodeId}
              onSelectNode={s.setSelectedNodeId}
              soloNodeId={s.soloNode?.id ?? null}
              onSolo={s.setSolo}
            />
          </>
        ) : (
          <PageEmptyState page="color" hint={t('selectOne')} testId="color-empty" compact />
        )}

        <Divider label={t('adjustmentLayers')} labelPosition="left" />
        <Stack gap={4} data-testid="adjustment-layers">
          <Text size="xs" c="dimmed">
            {t('adjustmentIntro')}
          </Text>
          {s.adjustments.map((a) => (
            <Group key={a.id} gap={6} wrap="nowrap" data-testid={`adjustment-row-${a.id}`}>
              <Button size="compact-xs" fz="xs" variant={s.p.selection.includes(a.id) ? 'filled' : 'light'} onClick={() => { s.p.onSelect(a.id); s.p.onSeek(a.recordIn); }} data-testid={`adjustment-select-${a.id}`}>
                {nameText(a.name)}
              </Button>
              <Text size="xs" c="dimmed" data-testid={`adjustment-range-${a.id}`}>
                {t('range', { from: framesToTimecode(a.recordIn, s.p.doc.settings.rate), to: framesToTimecode(a.recordOut, s.p.doc.settings.rate) })}
              </Text>
            </Group>
          ))}
          <Group>
            <Button size="compact-xs" fz="xs" variant="light" leftSection={<IconPlus size={12} />} disabled={s.p.readOnly} onClick={s.addAdjustment} data-testid="adjustment-add">
              {s.selectedClip ? t('addAdjustmentOver') : t('addAdjustment')}
            </Button>
          </Group>
        </Stack>
      </Stack>
    </EditorPanel>
  );
}

/**
 * Right zone (§5.6/§8.2): the compare bar (bypass, before / after split, reference still) and the six
 * grade-tool tabs. The viewer previews every tab's work — primaries, curves, LUTs and secondaries — so no
 * tab carries a "render only" note any more; the viewer's own parity badge speaks when something differs.
 */
export function ColorGradeToolsPanel() {
  const t = useT('editor-color.workspace');
  const tGrade = useT('editor-color.grade');
  const nameText = useDefaultNameText();
  const s = useColorPage();
  const target = s.targetGroup ?? s.gradeTarget;
  const stack = s.targetGroup ? s.targetGroup.grade : s.gradeTarget?.grade ?? null;
  const write = s.targetGroup ? (next: GradeStack | null, label: string) => s.writeGroupGrade(s.targetGroup!, next, label) : s.writeClipGrade;
  const subject = s.targetGroup ? s.targetGroup.name : nameText(s.gradeTarget?.name ?? '');
  const tabs: { value: GradeToolTab; label: string }[] = [
    { value: 'primaries', label: t('tabs.primaries') },
    { value: 'curves', label: t('tabs.curves') },
    { value: 'lut', label: t('tabs.lut') },
    { value: 'secondaries', label: t('tabs.secondaries') },
    { value: 'groups', label: t('tabs.groups') },
    { value: 'gallery', label: t('tabs.gallery') },
  ];
  const toolTab = s.toolTab === 'primaries' || s.toolTab === 'curves' || s.toolTab === 'lut' || s.toolTab === 'secondaries' ? s.toolTab : 'primaries';
  const soloName = s.soloNode ? (s.soloNode.name ? nameText(s.soloNode.name) : tGrade('correction')) : null;

  return (
    <EditorPanel title={t('gradeTools')} testId="color-grade-tools" scroll={false}>
      <CompareBar
        shownBypass={s.shownBypass}
        onChooseBypass={s.chooseBypass}
        gradeBypass={s.p.gradeBypass}
        onGradeBypass={s.p.onGradeBypass}
        gallery={s.p.gallery}
        referenceId={s.p.referenceId}
        onReference={s.p.onReference}
        wipe={s.p.wipe}
        onWipe={s.p.onWipe}
        onCaptureStill={s.p.onCaptureStill}
        stillLabel={s.selectedClip?.name ?? t('stillName')}
      />
      {soloName && <SoloLine name={soloName} onLeave={() => s.setSolo(null)} testId="color-solo" />}
      <div id={s.toolsDomId}>
      <Tabs value={s.toolTab} onChange={(v) => v && s.setToolTab(v as GradeToolTab)}>
        <Tabs.List style={{ flexWrap: 'nowrap', overflowX: 'auto' }}>
          {tabs.map((tab) => (
            <Tabs.Tab key={tab.value} value={tab.value} fz={12} px={8} data-testid={`color-tab-${tab.value}`}>
              {tab.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>

        {s.toolTab === 'groups' ? (
            <Stack gap="sm" mt={6} data-testid="color-groups">
              <Divider label={t('groups', { count: s.groups.length })} labelPosition="left" />
              <Group gap={6}>
                <Button size="compact-xs" fz="xs" variant="light" leftSection={<IconUsersGroup size={12} />} disabled={s.p.readOnly} onClick={s.createGroup} data-testid="color-group-create">
                  {t('groupSelected')}
                </Button>
                <Tooltip label={t('fromLooksTip')}>
                  <Button size="compact-xs" fz="xs" variant="light" disabled={s.p.readOnly || s.p.seedStepResultId === null} onClick={s.groupsFromLooks} data-testid="color-groups-from-looks">
                    {t('fromLooks')}
                  </Button>
                </Tooltip>
              </Group>
              <Stack gap={4}>
                {s.groups.map((group) => (
                  <Stack key={group.id} gap={2} data-testid={`color-group-${group.id}`} style={{ borderLeft: '2px solid var(--mantine-color-violet-5)', paddingLeft: 6 }}>
                    <Group gap={6} wrap="nowrap">
                      <TextInput size="xs" defaultValue={group.name} key={`${group.id}:${group.name}`} disabled={s.p.readOnly} onBlur={(e) => { const name = e.currentTarget.value.trim(); if (name && name !== group.name) s.p.onOp({ type: 'setColorGroup', groupId: group.id, group: { ...group, name } }, t('renameGroupLabel')); }} style={{ flex: 1 }} aria-label={t('groupName')} />
                      {group.lookGroupId && <Badge size="xs" fz="xs" variant="light">{group.lookGroupId}</Badge>}
                      <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => s.setTarget(group.id)}>{t('grade')}</Button>
                      <Button size="compact-xs" fz="xs" variant="subtle" disabled={s.p.readOnly} onClick={() => s.addSelectedToGroup(group)} data-testid={`color-group-add-${group.id}`}>{t('addSelected')}</Button>
                      <EditorIcon icon={IconTrash} size="sm" color="red" disabled={s.p.readOnly} onClick={() => s.p.onOp({ type: 'setColorGroup', groupId: group.id, group: undefined }, t('removeGroupLabel', { group: group.name }))} label={t('removeGroup', { group: group.name })} data-testid={`color-group-remove-${group.id}`} />
                    </Group>
                    <Group gap={4}>
                      {group.clipIds.map((clipId) => {
                        const clip = s.p.doc.tracks.flatMap((t) => t.items).find((i) => i.id === clipId) as MediaClip | undefined;
                        return (
                          <Badge key={clipId} size="xs" fz="xs" variant={clip ? 'light' : 'outline'} color={clip ? 'gray' : 'orange'} style={{ cursor: clip ? 'var(--rb-cursor-pointer)' : 'var(--rb-cursor-default)' }} onClick={() => clip && (s.p.onSelect(clip.id), s.p.onSeek(clip.recordIn))} rightSection={s.p.readOnly ? undefined : <span role="button" aria-label={t('removeMember', { clip: clip?.name ?? clipId, group: group.name })} onClick={(e) => { e.stopPropagation(); s.removeMember(group, clipId); }} style={{ cursor: 'var(--rb-cursor-pointer)' }}>×</span>}>
                            {clip ? clip.name : t('removedClip')}
                          </Badge>
                        );
                      })}
                      {group.clipIds.length === 0 && <Text size="xs" c="dimmed">{t('noMembers')}</Text>}
                    </Group>
                  </Stack>
                ))}
              </Stack>
            </Stack>
          ) : s.toolTab === 'gallery' ? (
            <Stack gap="sm" mt={6}>
              <LookPanel
                doc={s.p.doc}
                target={s.gradeTarget}
                selection={s.p.selection}
                readOnly={s.p.readOnly}
                playheadFrame={s.p.playheadFrame}
                newId={s.p.newId}
                onWriteGrade={(next, label) => s.writeClipGrade(next, label)}
                onApply={s.p.onApply}
                onSeek={s.p.onSeek}
                gradeBypass={s.p.gradeBypass}
                onGradeBypass={s.p.onGradeBypass}
                getCanvas={s.p.getCanvas}
                gallery={s.p.gallery}
                onCaptureStill={s.p.onCaptureStill}
                referenceId={s.p.referenceId}
                onReference={s.p.onReference}
                wipe={s.p.wipe}
                onWipe={s.p.onWipe}
                onMessage={s.setMessage}
                onPanelBypass={s.setPanelBypass}
                userBypassSeq={s.userBypassSeq}
              />
              <Divider label={t('galleryWipe')} labelPosition="left" />
              <Group gap={6}>
                <Button size="compact-xs" fz="xs" variant="light" leftSection={<IconCamera size={12} />} onClick={() => void s.p.onCaptureStill(s.selectedClip?.name ?? t('stillName'))} data-testid="color-capture-still">
                  {t('grabStill')}
                </Button>
                <Button size="compact-xs" fz="xs" variant="light" disabled={s.p.readOnly || !s.selectedClip || !s.p.referenceId} onClick={s.shotMatch} data-testid="color-shot-match">
                  {t('matchReference')}
                </Button>
                {s.p.referenceId && (
                  <Group gap={4} style={{ flex: 1 }}>
                    <Text size="xs" c="dimmed">{t('wipe')}</Text>
                    <Slider size="xs" style={{ flex: 1, minWidth: 80 }} min={0} max={1} step={0.01} value={s.p.wipe} onChange={s.p.onWipe} label={null} data-testid="color-wipe" />
                  </Group>
                )}
              </Group>
              {s.p.gallery.length === 0 ? (
                <EditorEmpty compact icon={IconPhoto} title={t('noStills')} hint={t('noStillsHint')} testId="color-gallery-empty" />
              ) : (
                <ScrollArea.Autosize mah={140}>
                  <Group gap={6}>
                    {s.p.gallery.map((still) => (
                      <Stack key={still.id} gap={2} align="center" data-testid={`color-still-${still.id}`}>
                        <Image
                          src={still.dataUrl}
                          alt={still.label}
                          w={96}
                          radius={4}
                          role="button"
                          tabIndex={0}
                          aria-pressed={s.p.referenceId === still.id}
                          aria-label={t('useAsReference', { label: still.label })}
                          data-testid={`color-still-image-${still.id}`}
                          style={{ cursor: 'var(--rb-cursor-pointer)', outline: s.p.referenceId === still.id ? '2px solid var(--mantine-color-violet-5)' : undefined }}
                          onClick={() => s.p.onReference(s.p.referenceId === still.id ? null : still.id)}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter' || e.key === ' ') {
                              e.preventDefault();
                              s.p.onReference(s.p.referenceId === still.id ? null : still.id);
                            }
                          }}
                        />
                        <Group gap={2}>
                          <Text size="xs" c="dimmed" maw={70} truncate>{still.label}</Text>
                          <EditorIcon icon={IconTrash} size="sm" color="red" onClick={() => s.p.onRemoveStill(still.id)} label={t('removeStill', { label: still.label })} />
                        </Group>
                      </Stack>
                    ))}
                  </Group>
                </ScrollArea.Autosize>
              )}
            </Stack>
          ) : !target ? (
            <Alert color="gray" p="xs" mt={6}>{t('selectOne')}</Alert>
          ) : (
            <Stack gap="xs" mt={6}>
              <GradeStackEditor
                key={`${s.targetGroup?.id ?? s.gradeTarget?.id ?? 'none'}:${s.toolTab}`}
                mode="tools"
                toolTab={toolTab}
                projectId={s.p.projectId}
                stack={stack}
                readOnly={s.p.readOnly}
                onChange={write}
                onPreview={s.previewStack}
                newId={s.p.newId}
                lutFiles={s.lutFiles}
                subject={subject}
                selectedNodeId={s.selectedNodeId}
                onSelectNode={s.setSelectedNodeId}
                windowTarget={s.windowTargetForClip}
                onTrackWindow={s.onTrackWindowFor}
                onEditWindow={s.p.onActivateWindowTool}
              />
            </Stack>
          )}
        </Tabs>
      </div>
    </EditorPanel>
  );
}

/**
 * "Solo: <name>" above the scopes, for the viewer zone (the right zone can be a closed drawer on a tablet).
 * Renders nothing outside the Colour page or while no node is soloed.
 */
export function ColorSoloBanner() {
  const tGrade = useT('editor-color.grade');
  const nameText = useDefaultNameText();
  const s = useContext(ColorPageContext);
  if (!s?.soloNode) return null;
  return <SoloLine name={s.soloNode.name ? nameText(s.soloNode.name) : tGrade('correction')} onLeave={() => s.setSolo(null)} testId="color-solo-viewer" />;
}

/**
 * The Color workspace (P7) in one column, for callers that do not use the page's zones (tests, legacy).
 * The editor itself renders {@link ColorNodesPanel} on the left, the viewer with the docked scopes in the
 * centre and {@link ColorGradeToolsPanel} on the right, all under one {@link ColorPageProvider}.
 */
export function ColorWorkspace(props: ColorWorkspaceProps) {
  return (
    <ColorPageProvider {...props}>
      <Stack gap="sm">
        <ColorNodesPanel />
        <ColorGradeToolsPanel />
      </Stack>
    </ColorPageProvider>
  );
}

/**
 * P9: follows the feature under a node's power window through its clip — the window's box on the
 * clip's first frame is tracked by NCC (`feature-tracker.ts`) over frames sampled from the clip's
 * media at the timeline rate (at most 10 s), and becomes the window's offset keyframes in timeline
 * frames from the clip's record-in. The box assumes the source fills the canvas (the aspect-policy
 * fit can offset it slightly); lost frames are skipped and the curve interpolates across them.
 */
async function trackWindowThroughClip(clip: MediaClip, node: GradeNode, mediaUrlFor: (clip: MediaClip) => string | null, settings: EditTimeline['settings'], noPlayableMedia: string): Promise<GradeWindow | null> {
  const window = node.window;
  if (!window) return null;
  const url = mediaUrlFor(clip);
  if (!url) throw new Error(noPlayableMedia);
  const fps = settings.rate.den > 0 ? settings.rate.num / settings.rate.den : 30;
  const count = Math.max(2, Math.min(clip.recordOut - clip.recordIn, Math.round(fps * 10)));
  const frames = await sampleVideoFrames(url, (clip.sourceIn ?? 0) / fps, count, fps, 320);
  const first = frames[0];
  const shape = window.shape;
  const box = { x: ((shape.x ?? 0.5) - (shape.w ?? 0.5) / 2) * first.width, y: ((shape.y ?? 0.5) - (shape.h ?? 0.5) / 2) * first.height, w: (shape.w ?? 0.5) * first.width, h: (shape.h ?? 0.5) * first.height };
  const points = trackFeature(frames, box, { searchRadius: 12 });
  const { offsetX, offsetY } = trackToWindowOffsets(points, first.width, first.height, clip.recordIn);
  return { ...window, offsetX, offsetY };
}
