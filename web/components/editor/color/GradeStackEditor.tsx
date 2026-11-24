'use client';

import React, { useEffect, useId, useRef, useState } from 'react';
import { Button, Collapse, Divider, Group, NumberInput, Select, Stack, Switch, Text, Tooltip } from '@mantine/core';
import { LiveSlider } from '../shell/LiveSlider';
import { IconClipboardCopy, IconClipboardText } from '@tabler/icons-react';
import type { GradeLut, GradeNode, GradePrimaries, GradeStack, GradeWheel, GradeWindow } from '@/lib/editing/types';
import { GRADE_RANGES } from '@/lib/editing/engine';
import { HIGHLIGHT_TONES, LOOKS, SCALAR_KEYS, SHADOW_TONES, STRENGTHS, WHEEL_KEYS, IDENTITY_PRIMARIES, lookNode, newNode, withNode, type Look, type ScalarKey, type Strength, type WheelKey } from '@/lib/editing/color/grade';
import { loadViewingLut } from '@/lib/editing/color/cube-lut-loader';
import { lutReasonFromError, lutReasonMsg, type LutReason } from '@/lib/editing/color/lut-reason';
import { fetcher } from '@/lib/api/client';
import type { ProjectFileContent } from '@/lib/types/project';
import { canAddNode } from '@/lib/editing/color/grade-nodes';
import { ColorWheel } from './ColorWheel';
import { GradeNodeList } from './GradeNodeList';
import { CurveEditor } from './CurveEditor';
import { SecondaryFields } from './SecondaryFields';
import { LargerScreenOnly } from '../shell/LargerScreen';
import { EditorIcon } from '../ui';
import { useFormat, useT } from '@/i18n';
import { defaultCorrectionName, useDefaultNameText } from '../shell/default-names';

/** The one grade clipboard of the session (copy/paste between clips and groups). */
let clipboard: GradeStack | null = null;
export const gradeClipboard = { get: () => clipboard, set: (stack: GradeStack | null) => { clipboard = stack; } };

export interface GradeStackEditorProps {
  stack: GradeStack | null | undefined;
  readOnly: boolean;
  /** Writes the whole stack (one undo step per call). `null` clears the grade. */
  onChange: (stack: GradeStack | null, label: string) => void;
  newId: () => string;
  /** The project's `.cube` files, for the LUT picker. */
  lutFiles: readonly { id: string; name: string }[];
  /** A picked `.cube` is read from this project and checked before the node is written (a refused file is not written). */
  projectId: string;
  /** Where this stack lives, for the labels. */
  subject: string;
  /** P9: tracks a node's power window through the clip (clip stacks only). */
  onTrackWindow?: (node: GradeNode) => Promise<GradeWindow | null>;
  /** R10.3: publishes the selected node's power window so the viewer can draw its handles (clip stacks inside the Advanced tools only). */
  windowTarget?: { itemId: string; publish: (target: { itemId: string; nodeId: string } | null) => void };
  /**
   * §8.2: which half this instance renders. `nodes` is the left zone's stack, `tools` is the right zone's
   * selected-node editor, `both` (the default, Compose) stacks everything as before.
   */
  mode?: 'both' | 'nodes' | 'tools';
  /** `tools` mode: the selected grade-tool tab. */
  toolTab?: 'primaries' | 'curves' | 'lut' | 'secondaries';
  /** §8.2: the node selection is shared between the zones; uncontrolled when absent. */
  selectedNodeId?: string | null;
  onSelectNode?: (id: string) => void;
  /** §8.2: "Edit window on the picture" - enters the window viewer tool (paused auto-show is unchanged). */
  onEditWindow?: () => void;
  /** T-008: the stack a drag would write, for the viewer's transient preview; `null` clears it. */
  onPreview?: (stack: GradeStack | null) => void;
  /** Wave 4: the soloed node (view state held by the Colour page). Absent: the node list offers no solo. */
  soloNodeId?: string | null;
  onSolo?: (nodeId: string | null) => void;
}

/**
 * The grade stack editor (P7): the stack's bypass switch, its ordered nodes (`GradeNodeList`), and
 * the selected node's primaries (four wheels as R/G/B/master, seven scalars), curves and LUT. Every
 * change writes the WHOLE stack through `onChange` — one `setProperty grade` per edit, so each is
 * one undo step and the server validator sees the same object the engine checked.
 */
export function GradeStackEditor({ stack, readOnly, onChange, newId, lutFiles, projectId, subject, onTrackWindow, windowTarget, mode = 'both', toolTab = 'primaries', selectedNodeId, onSelectNode, onEditWindow, onPreview, soloNodeId, onSolo }: GradeStackEditorProps) {
  const t = useT('editor-color.grade');
  const nameText = useDefaultNameText();
  const f = useFormat();
  const SCALAR_LABELS = (key: ScalarKey): string => t(`scalars.${key}`);
  const WHEEL_LABELS = (key: WheelKey): string => t(`wheels.${key}`);
  const nodes = stack?.nodes ?? [];
  const [localSelectedId, setLocalSelectedId] = useState<string | null>(nodes[0]?.id ?? null);
  const selectNode = (id: string) => {
    setLocalSelectedId(id);
    onSelectNode?.(id);
  };
  const selectedId = selectedNodeId !== undefined ? selectedNodeId : localSelectedId;
  const selected = nodes.find((n) => n.id === selectedId) ?? nodes[0] ?? null;
  const showNodes = mode !== 'tools';
  const showTools = mode !== 'nodes';
  const activeTab = mode === 'both' ? null : toolTab;
  const windowNodeId = selected?.window ? selected.id : null;
  const publishWindow = windowTarget?.publish;
  const windowItemId = windowTarget?.itemId;
  useEffect(() => {
    if (!publishWindow || !windowItemId) return;
    publishWindow(windowNodeId ? { itemId: windowItemId, nodeId: windowNodeId } : null);
    return () => publishWindow(null);
  }, [publishWindow, windowItemId, windowNodeId]);
  // The exact R/G/B/master fields stay open by default: the colour e2e specs type straight into
  // `grade-wheel-*-*` without opening a disclosure first. They can still be collapsed.
  const [exactOpen, setExactOpen] = useState(true);
  const exactId = useId();
  const [look, setLook] = useState<Look>('Warm');
  const [strength, setStrength] = useState<Strength>('Normal');
  const [shadow, setShadow] = useState<(typeof SHADOW_TONES)[number]>('Neutral');
  const [highlight, setHighlight] = useState<(typeof HIGHLIGHT_TONES)[number]>('Neutral');

  const writeNode = (node: GradeNode, label: string) => onChange(withNode(stack, node), label);
  const tr = useT('editor-color.lutReason');
  const [lutProblem, setLutProblem] = useState<LutReason | null>(null);
  const [lutChecking, setLutChecking] = useState(false);
  const pickToken = useRef(0);
  const latest = useRef({ selected, stack });
  useEffect(() => {
    latest.current = { selected, stack };
  });
  /** Picks a LUT: reads and parses the file first (1D and shaper + 3D are fine); a refusal is shown and nothing is written. */
  const pickLut = async (fileId: string | null) => {
    if (!selected) return;
    const token = (pickToken.current += 1);
    setLutProblem(null);
    const apply = () => {
      const { selected: node, stack: current } = latest.current;
      if (!node || node.id !== selected.id) return;
      const lut: GradeLut | null = fileId ? { file: { kind: 'projectFile', projectFileId: fileId }, intensity: node.lut?.intensity ?? 1 } : null;
      onChange(withNode(current, { ...node, lut }), t(lut ? 'undoSetLut' : 'undoClearLut', { subject }));
    };
    if (!fileId) {
      setLutChecking(false);
      apply();
      return;
    }
    setLutChecking(true);
    try {
      await loadViewingLut(async () => (await fetcher<ProjectFileContent>(`/api/v1/projects/${projectId}/files/${fileId}/content`)).content);
      if (token !== pickToken.current) return;
      setLutChecking(false);
      apply();
    } catch (err) {
      if (token !== pickToken.current) return;
      setLutChecking(false);
      setLutProblem(lutReasonFromError(err));
    }
  };
  const writePrimaries = (patch: Partial<GradePrimaries>, label: string) => {
    if (!selected) return;
    writeNode({ ...selected, primaries: { ...(selected.primaries ?? {}), ...patch } }, label);
  };
  const writeWheel = (key: WheelKey, patch: Partial<GradeWheel>) => {
    if (!selected) return;
    const wheel: GradeWheel = { ...(selected.primaries?.[key] ?? {}), ...patch };
    const identity = (wheel.r ?? 0) === 0 && (wheel.g ?? 0) === 0 && (wheel.b ?? 0) === 0 && (wheel.master ?? 0) === 0;
    writePrimaries({ [key]: identity ? null : wheel }, t('undoLabel', { subject, label: WHEEL_LABELS(key) }));
  };
  // T-008: the same stack the release would write, carried to the viewer's transient preview.
  const previewWheel = (key: WheelKey, wheel: GradeWheel | null) => {
    if (!onPreview) return;
    if (!wheel || !selected) {
      onPreview(null);
      return;
    }
    const identity = (wheel.r ?? 0) === 0 && (wheel.g ?? 0) === 0 && (wheel.b ?? 0) === 0 && (wheel.master ?? 0) === 0;
    onPreview(withNode(stack, { ...selected, primaries: { ...(selected.primaries ?? {}), [key]: identity ? null : wheel } }));
  };
  const previewPrimaries = (patch: Partial<GradePrimaries>) => {
    if (onPreview && selected) onPreview(withNode(stack, { ...selected, primaries: { ...(selected.primaries ?? {}), ...patch } }));
  };
  // T-011: a scalar drag's transient preview must not outlive the panel (same as ColorWheel).
  const onPreviewRef = useRef(onPreview);
  useEffect(() => {
    onPreviewRef.current = onPreview;
  }, [onPreview]);
  useEffect(() => () => {
    onPreviewRef.current?.(null);
  }, []);

  const addNode = () => {
    const node = newNode(newId(), defaultCorrectionName(nodes.length + 1));
    onChange(withNode(stack, node), t('undoAddCorrection', { subject }));
    selectNode(node.id);
  };
  const addLook = () => {
    const node = lookNode(newId(), look, strength, shadow, highlight);
    onChange(withNode(stack, node), t('undoLook', { subject, look: t(`looks.${look}`) }));
    selectNode(node.id);
  };

  return (
    <Stack gap="xs" data-testid="grade-stack-editor">
      <Group justify="space-between">
        <Switch size="xs" label={stack?.enabled === false ? t('bypassed') : t('on')} checked={stack ? stack.enabled !== false : true} disabled={readOnly || !stack} onChange={(e) => stack && onChange({ ...stack, enabled: e.currentTarget.checked }, t(e.currentTarget.checked ? 'undoEnableGrade' : 'undoBypassGrade', { subject }))} data-testid="grade-enabled" />
        <Group gap={4}>
          <Tooltip label={t('copyTip')}>
            <EditorIcon tooltip={false} icon={IconClipboardCopy} size="sm" disabled={!stack} onClick={() => gradeClipboard.set(stack ? structuredClone(stack) : null)} label={t('copy')} data-testid="grade-copy" />
          </Tooltip>
          <Tooltip label={t('pasteTip')}>
            <EditorIcon tooltip={false} icon={IconClipboardText} size="sm" disabled={readOnly || gradeClipboard.get() === null} onClick={() => { const c = gradeClipboard.get(); if (c) onChange({ ...structuredClone(c), nodes: (c.nodes ?? []).map((n) => ({ ...n, id: newId() })) }, t('undoPaste', { subject })); }} label={t('paste')} data-testid="grade-paste" />
          </Tooltip>
        </Group>
      </Group>

      <Stack gap={2} data-testid="grade-nodes">
        {showNodes && (
          <GradeNodeList stack={stack} readOnly={readOnly} subject={subject} selectedId={selected?.id ?? null} onSelect={selectNode} onChange={onChange} newId={newId} soloNodeId={soloNodeId} onSolo={onSolo} />
        )}
      </Stack>
      {showNodes && (
      <Group gap={6}>
        <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || !canAddNode(stack)} onClick={addNode} data-testid="grade-add-node">
          {t('addCorrection')}
        </Button>
        <Select size="xs" data={LOOKS.map((l) => ({ value: l, label: t(`looks.${l}`) }))} value={look} onChange={(v) => v && setLook(v as Look)} allowDeselect={false} w={96} aria-label={t('look')} data-testid="grade-look" />
        <Select size="xs" data={STRENGTHS.map((s) => ({ value: s, label: t(`strengths.${s}`) }))} value={strength} onChange={(v) => v && setStrength(v as Strength)} allowDeselect={false} w={92} aria-label={t('lookStrength')} />
        <Select size="xs" data={SHADOW_TONES.map((s) => ({ value: s, label: t('shadowsTone', { tone: t(`shadowTones.${s}`) }) }))} value={shadow} onChange={(v) => v && setShadow(v as (typeof SHADOW_TONES)[number])} allowDeselect={false} w={150} aria-label={t('shadowTone')} />
        <Select size="xs" data={HIGHLIGHT_TONES.map((s) => ({ value: s, label: t('highlightsTone', { tone: t(`highlightTones.${s}`) }) }))} value={highlight} onChange={(v) => v && setHighlight(v as (typeof HIGHLIGHT_TONES)[number])} allowDeselect={false} w={170} aria-label={t('highlightTone')} />
        <Tooltip label={t('addLookTip')}>
          <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || !canAddNode(stack)} onClick={addLook} data-testid="grade-add-look">
            {t('addLook')}
          </Button>
        </Tooltip>
      </Group>
      )}

      {selected && (
        <>
          {showTools && (activeTab === null || activeTab === 'primaries') && (
          <>
          <Divider label={t('primaries', { name: selected.name ? nameText(selected.name) : t('correction') })} labelPosition="left" />
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: 8, alignItems: 'start' }}>
            {WHEEL_KEYS.map((key) => (
              <ColorWheel key={key} wheelKey={key} label={WHEEL_LABELS(key)} value={selected.primaries?.[key] ?? {}} readOnly={readOnly} onChange={(wheel) => writeWheel(key, wheel)} onPreview={(wheel) => previewWheel(key, wheel)} />
            ))}
          </div>
          <Group justify="space-between" gap={6}>
            <Button size="compact-xs" fz="xs" variant="subtle" aria-expanded={exactOpen} aria-controls={exactId} onClick={() => setExactOpen((v) => !v)} data-testid="grade-exact-toggle">
              {t('exactValues')}
            </Button>
          </Group>
          <Collapse expanded={exactOpen} id={exactId}>
            <Stack gap={4} data-testid="grade-exact-values">
              {WHEEL_KEYS.map((key) => {
                const wheel = selected.primaries?.[key] ?? {};
                return (
                  <Group key={key} gap={4} wrap="nowrap" align="flex-end" data-testid={`grade-exact-${key}`}>
                    <Text size="xs" w={118} c="dimmed">
                      {WHEEL_LABELS(key)}
                    </Text>
                    {/* eslint-disable-next-line i18next/no-literal-string -- ids, not text */}
                    {(['r', 'g', 'b', 'master'] as const).map((c) => (
                      <NumberInput key={c} size="xs" w={70} label={c === 'master' ? t('masterShort') : c.toUpperCase()} min={-1} max={1} step={0.01} decimalScale={3} value={wheel[c] ?? 0} disabled={readOnly} onChange={(v) => typeof v === 'number' && writeWheel(key, { [c]: Math.min(1, Math.max(-1, v)) })} data-testid={`grade-wheel-${key}-${c}`} />
                    ))}
                  </Group>
                );
              })}
            </Stack>
          </Collapse>
          {SCALAR_KEYS.map((key) => {
            const range = GRADE_RANGES[key];
            const value = selected.primaries?.[key] ?? IDENTITY_PRIMARIES[key];
            return (
              <Group key={key} gap={6} wrap="nowrap" align="center" data-testid={`grade-scalar-${key}`}>
                <Text size="xs" w={118} c="dimmed">
                  {SCALAR_LABELS(key)}
                </Text>
                <LiveSlider size="xs" style={{ flex: 1 }} thumbLabel={SCALAR_LABELS(key)} min={range.min} max={range.max} step={key === 'hue' ? 1 : 0.01} value={value} disabled={readOnly} onChange={(v) => previewPrimaries({ [key]: v })} onChangeEnd={(v) => { writePrimaries({ [key]: v }, t('undoLabel', { subject, label: SCALAR_LABELS(key) })); onPreview?.(null); }} label={(v) => f.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} />
                <NumberInput size="xs" w={84} aria-label={SCALAR_LABELS(key)} min={range.min} max={range.max} step={key === 'hue' ? 1 : 0.01} decimalScale={3} value={value} disabled={readOnly} onChange={(v) => typeof v === 'number' && writePrimaries({ [key]: Math.min(range.max, Math.max(range.min, v)) }, t('undoLabel', { subject, label: SCALAR_LABELS(key) }))} data-testid={`grade-scalar-${key}-input`} />
              </Group>
            );
          })}
          </>
          )}
          {showTools && (activeTab === null || activeTab === 'curves') && (
          <>
          <Divider label={t('curves')} labelPosition="left" />
          <LargerScreenOnly feature="curves" what={t('editingCurves')}>
            <CurveEditor curves={selected.curves} readOnly={readOnly} onChange={(curves) => writeNode({ ...selected, curves }, t('undoCurves', { subject }))} />
          </LargerScreenOnly>
          </>
          )}
          {showTools && (activeTab === null || activeTab === 'lut') && (
          <>
          <Divider label={t('lut')} labelPosition="left" />
          <Group gap={6} align="flex-end">
            <Select
              size="xs"
              label={t('cubeFile')}
              placeholder={lutFiles.length === 0 ? t('noCube') : t('none')}
              clearable
              data={lutFiles.map((f) => ({ value: f.id, label: f.name }))}
              value={selected.lut?.file.projectFileId ?? null}
              disabled={readOnly || lutFiles.length === 0}
              onChange={(v) => {
                void pickLut(v);
              }}
              style={{ flex: 1 }}
              data-testid="grade-lut"
            />
            <NumberInput size="xs" label={t('intensity')} w={90} min={0} max={1} step={0.05} decimalScale={2} value={selected.lut?.intensity ?? 1} disabled={readOnly || !selected.lut} onChange={(v) => typeof v === 'number' && selected.lut && writeNode({ ...selected, lut: { ...selected.lut, intensity: Math.min(1, Math.max(0, v)) } }, t('undoLutIntensity', { subject }))} data-testid="grade-lut-intensity" />
          </Group>
          {lutChecking && (
            <Text size="xs" c="dimmed" role="status" data-testid="grade-lut-checking">
              {tr('checking')}
            </Text>
          )}
          {lutProblem && (
            <Text size="xs" c="red" role="alert" data-testid="grade-lut-problem">
              {tr.msg(lutReasonMsg(lutProblem))}
            </Text>
          )}
          <Text size="xs" c="dimmed">
            {t('lutNote')}
          </Text>
          </>
          )}
          {showTools && (activeTab === null || activeTab === 'secondaries') && (
          <>
          <Divider label={t('secondary')} labelPosition="left" />
          <SecondaryFields node={selected} readOnly={readOnly} onChange={(node, label) => writeNode(node, t('undoLabel', { subject, label }))} onTrackWindow={onTrackWindow} onEditWindow={onEditWindow} />
          </>
          )}
        </>
      )}
    </Stack>
  );
}
