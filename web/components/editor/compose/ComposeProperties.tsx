'use client';

import React, { useMemo, useState } from 'react';
import { Alert, Box, Button, Divider, Group, NumberInput, Select, Stack, Switch, Text, Tooltip } from '@mantine/core';
import { LiveSlider } from '../shell/LiveSlider';
import { IconTrash } from '@tabler/icons-react';
import { PresetPicker } from '../presets/PresetPicker';
import type { EffectPresetPayload } from '@/lib/editing/api/editor-p9';
import type { PropertyKey, PropertyValue } from '@/lib/editing/engine';
import { EFFECT_TYPES, MAX_BLUR_AMOUNT, RETIME_QUALITIES } from '@/lib/editing/engine';
import { segmentationAnalysesOf } from '@/lib/editing/preview/segmentation-tracks';
import { useSegmentationTracks } from '@/lib/hooks/use-segmentation-tracks';
import type { ClipEffect, EditTimeline, InsertClip, Keyframe, MediaClip, QuadCorners } from '@/lib/editing/types';
import { composeEntries, findComposeItem, resolveComposeEntry, type ComposeEntryId } from '@/lib/editing/compose/entries';
import { framesToTimecode } from '@/lib/editing/time';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { GradeStackEditor } from '../color/GradeStackEditor';
import { useGradePreview } from '@/lib/editing/color/grade-preview';
import { KeyerFields } from '../color/KeyerFields';
import { MaskReframeFields, type MaskReframeTool } from '../panels/MaskReframeFields';
import { NodeView } from './NodeView';
import { entryName } from './ComposeList';
import { EditorPanel } from '../ui/EditorPanel';
import { EditorIcon } from '../ui';
import { Preview3sButton } from '../viewer/Preview3s';
import { useLiveChord } from '../prefs/live-chords';
import { useFormat, useT } from '@/i18n';

export interface ComposePropertiesProps {
  doc: EditTimeline;
  projectId: string;
  readOnly: boolean;
  selection: readonly string[];
  playheadFrame: number;
  /** R10.4: the timeline the "Preview 3 s" render runs against (absent: the button is not offered). */
  timelineId?: string;
  /** R10.4: called as a preview starts, so the host can pause playback. */
  onPreview3sStart?: () => void;
  newId: () => string;
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => void;
  /** Moves the playhead; the quad key chips use it to jump to their key. */
  onSeek: (frame: number) => void;
  /** §7.3: the mask/reframe fields and Add corner pin activate the matching viewer tool (pauses playback and focuses the picture). */
  onActivateViewerTool?: (tool: MaskReframeTool | 'pin') => void;
  /** "Draw on the picture" (false) and "Redraw" (true): enter the mask tool, with Redraw armed for the selected clip. */
  onDrawMask?: (redraw: boolean) => void;
  /** The list's selected entry (null: the first one applies). */
  composeEntry: ComposeEntryId | null;
}

const IDENTITY_QUAD: QuadCorners = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };
const CORNERS = ['topLeft', 'topRight', 'bottomRight', 'bottomLeft'] as const;

const AXES = ['x', 'y'] as const;

/** The four corners as one table: a single X / Y header row, then a labelled row per corner. */
function CornerFields({ testPrefix, value, disabled, range = false, onChange }: { testPrefix: string; value: QuadCorners; disabled: boolean; range?: boolean; onChange: (next: QuadCorners) => void }) {
  const t = useT('editor-misc.compose');
  const bounds = range ? { min: -1, max: 2 } : {};
  const axis = (corner: (typeof CORNERS)[number], key: 'x' | 'y') => (
    <NumberInput
      key={`${corner}-${key}`}
      size="xs"
      w={90}
      aria-label={`${t(`corners.${corner}`)} ${key.toUpperCase()}`}
      step={0.01}
      decimalScale={3}
      value={value[corner][key]}
      disabled={disabled}
      {...bounds}
      onChange={(v) => typeof v === 'number' && onChange({ ...value, [corner]: { ...value[corner], [key]: v } })}
    />
  );
  return (
    <Stack gap={4}>
      <Group gap={4} wrap="nowrap" aria-hidden>
        <Box w={90} />
        <Text size="xs" c="dimmed" w={90} ta="center">X</Text>
        <Text size="xs" c="dimmed" w={90} ta="center">Y</Text>
      </Group>
      {CORNERS.map((corner) => (
        <Group key={corner} gap={4} wrap="nowrap" align="center" data-testid={`${testPrefix}-${corner}`}>
          <Text size="xs" w={90} c="dimmed">{t(`corners.${corner}`)}</Text>
          {AXES.map((key) => axis(corner, key))}
        </Group>
      ))}
    </Stack>
  );
}
const NEW_KEYER = { color: '#00FF00', similarity: 0.3, blend: 0.1, spill: 0.5 } as const;

/** The effects list with its presets and the add row; used by media and adjustment clips. */
function EffectsEditor({ projectId, readOnly, effects, write }: { projectId: string; readOnly: boolean; effects: ClipEffect[] | null | undefined; write: (next: ClipEffect[] | null) => void }) {
  const t = useT('editor-misc.compose');
  const nf = useFormat();
  const dec2 = (v: number) => nf.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const [newEffect, setNewEffect] = useState<string>('blur');
  return (
    <Stack gap={4} data-testid="compose-effects" id="inspector-section-effects">
      <PresetPicker<EffectPresetPayload>
        projectId={projectId}
        kind="effect"
        current={effects && effects.length > 0 ? { effects: effects.map((e) => ({ type: e.type, amount: e.amount ?? 0.5, enabled: e.enabled !== false })) } : null}
        readOnly={readOnly}
        onApply={(p) => write(p.effects.slice(0, 8).map((e) => ({ type: e.type as ClipEffect['type'], amount: Math.min(e.type === 'blur' ? MAX_BLUR_AMOUNT : 1, Math.max(0, e.amount ?? 0.5)), enabled: e.enabled !== false })))}
        testId="effect-presets"
      />
      {(effects ?? []).map((effect, index) => (
        <Group key={index} gap={6} wrap="nowrap" data-testid={`compose-effect-${index}`}>
          <Switch size="xs" checked={effect.enabled !== false} disabled={readOnly} onChange={(e) => write((effects ?? []).map((f, i) => (i === index ? { ...f, enabled: e.currentTarget.checked } : f)))} aria-label={t('enableEffect', { effect: t(`effectTypes.${effect.type}`) })} />
          <Text size="xs" w={70}>{t(`effectTypes.${effect.type}`)}</Text>
          <LiveSlider size="xs" style={{ flex: 1 }} thumbLabel={t('effectAmount', { effect: t(`effectTypes.${effect.type}`) })} min={0} max={effect.type === 'blur' ? MAX_BLUR_AMOUNT : 1} step={0.01} value={effect.amount ?? 0.5} disabled={readOnly} onChangeEnd={(v) => write((effects ?? []).map((f, i) => (i === index ? { ...f, amount: v } : f)))} label={dec2} />
          <EditorIcon icon={IconTrash} size="sm" color="red" disabled={readOnly} onClick={() => { const next = (effects ?? []).filter((_, i) => i !== index); write(next.length === 0 ? null : next); }} label={t('removeEffect', { effect: t(`effectTypes.${effect.type}`) })} data-testid={`compose-effect-remove-${index}`} />
        </Group>
      ))}
      <Group gap={6}>
        <Select size="xs" data={EFFECT_TYPES.map((e) => ({ value: e, label: t(`effectTypes.${e}`) }))} value={newEffect} onChange={(v) => v && setNewEffect(v)} allowDeselect={false} w={120} aria-label={t('effectToAdd')} data-testid="compose-effect-type" />
        <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || (effects?.length ?? 0) >= 8} onClick={() => write([...(effects ?? []), { type: newEffect as ClipEffect['type'], amount: 0.5, enabled: true }])} data-testid="compose-effect-add">
          {t('addEffect')}
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * One entry's note on what the viewer shows, in plain words. Masks, the key, the corner pin, effects and the node
 * view are drawn live; stabilising needs its steadied copy and smooth slow motion blends frames only in the render.
 */
function RenderNote({ motion = false }: { motion?: boolean }) {
  const t = useT('editor-misc.compose');
  return (
    <Text size="xs" c="dimmed" data-testid="compose-render-note">
      {motion ? t('motionNote') : t('previewNote')}
    </Text>
  );
}

/**
 * Compose's right column (§5.5): the properties of the ONE selected list entry. An entry that is off offers its
 * one-click Add here; mask and reframe render the one shared `MaskReframeFields`. Every control that used to
 * sit in the long Compose form is reachable through exactly one entry.
 */
export function ComposeProperties({ doc, projectId, readOnly, selection, playheadFrame, timelineId, onPreview3sStart, newId, onSetProperty, onSeek, onActivateViewerTool, onDrawMask, composeEntry }: ComposePropertiesProps) {
  const t = useT('editor-misc.compose');
  const gradePreview = useGradePreview();
  const maskChord = useLiveChord('toolMask');
  const removeChord = useLiveChord('composeRemoveEntry');
  const { data: files } = useProjectFiles(projectId);
  const analysisIds = useMemo(() => segmentationAnalysesOf(doc), [doc]);
  const { lookup: tracksFor } = useSegmentationTracks(projectId, analysisIds);
  const lutFiles = (files ?? []).filter((f) => /\.cube$/i.test(f.originalFileName)).map((f) => ({ id: f.id, name: f.originalFileName }));
  const item = findComposeItem(doc, selection);
  const entries = composeEntries(item);
  const entry = resolveComposeEntry(entries, composeEntry);
  const preview3s = timelineId ? <Preview3sButton projectId={projectId} timelineId={timelineId} atFrame={playheadFrame} readOnly={readOnly} onBeforeStart={onPreview3sStart} /> : null;

  if (!item) {
    return (
      <EditorPanel title={t('propertiesTitle')} testId="compose-properties" scroll={false}>
        <Text size="xs" c="dimmed">{selection.length > 1 ? t('multiSelect') : t('noSelectionProps')}</Text>
      </EditorPanel>
    );
  }
  if (!entry) {
    return (
      <EditorPanel title={t('propertiesTitle')} testId="compose-properties" scroll={false}>
        <Alert color="gray" p="xs">{t('noControls', { type: item.type })}</Alert>
      </EditorPanel>
    );
  }

  const set = (key: PropertyKey, value: PropertyValue, label: string) => onSetProperty(item.id, key, value, label);
  const panel = (children: React.ReactNode, actions?: React.ReactNode) => (
    <EditorPanel title={t('propertiesTitle')} actions={actions} testId="compose-properties" scroll={false}>
      <Stack gap="sm" role="region" aria-label={t('propertiesLabel', { entry: entryName(t, entry) })} data-entry={entry}>
        {children}
      </Stack>
    </EditorPanel>
  );

  if (item.type === 'adjustmentClip') {
    const adjustment = item;
    const enabledSwitch = <Switch size="xs" label={t('enabled')} checked={adjustment.enabled !== false} disabled={readOnly} onChange={(e) => set('enabled', e.currentTarget.checked, t('toggleAdjustment'))} />;
    return panel(
      <>
        {entry === 'grade' && (
          <>
            <Divider label={t('gradeBelow')} labelPosition="left" />
            <GradeStackEditor projectId={projectId} stack={adjustment.grade} readOnly={readOnly} onChange={(stack, label) => set('grade', stack, label)} onPreview={(stack) => gradePreview.set(stack ? { target: { kind: 'item', id: adjustment.id }, stack } : null)} newId={newId} lutFiles={lutFiles} subject={adjustment.name} />
          </>
        )}
        {entry === 'effects' && (
          <>
            <Divider label={t('effects')} labelPosition="left" />
            <EffectsEditor projectId={projectId} readOnly={readOnly} effects={adjustment.effects} write={(next) => set('effects', next, t('adjustmentEffects'))} />
          </>
        )}
        {entry === 'chain' && (
          <>
            <Divider label={t('nodeView')} labelPosition="left" />
            <NodeView doc={doc} item={adjustment} />
          </>
        )}
      </>,
      <Group gap="sm" wrap="nowrap">
        {enabledSwitch}
        {preview3s}
      </Group>,
    );
  }

  if (item.type === 'insertClip') {
    const insert = item as InsertClip;
    const keys = insert.quad?.keys ?? [];
    const atPlayhead = keys.find((k) => k.frame === playheadFrame) ?? null;
    const writeQuad = (next: Keyframe<QuadCorners>[] | null, value?: QuadCorners | null) => set('quad', next === null && (value === null || value === undefined) ? null : { value: value === undefined ? insert.quad?.value ?? null : value, keys: next }, t('insertQuad'));
    const editCorners = (key: Keyframe<QuadCorners> | null, corners: QuadCorners) => {
      if (key) writeQuad(keys.map((k) => (k.frame === key.frame ? { ...k, value: corners } : k)));
      else writeQuad(keys.length === 0 ? null : keys, corners);
    };
    const editing: QuadCorners = atPlayhead?.value ?? insert.quad?.value ?? IDENTITY_QUAD;
    return panel(
      entry === 'matte' ? (
        <>
          <Divider label={t('matte')} labelPosition="left" />
          {insert.matte ? (
            <>
              <Button size="compact-xs" fz="xs" variant="subtle" color="red" w="fit-content" disabled={readOnly} onClick={() => set('matte', null, t('insertMatte'))} data-testid="compose-matte-toggle">
                {t('removeMatte')}
              </Button>
              <KeyerFields value={insert.matte} readOnly={readOnly} onChange={(next) => set('matte', next, t('insertMatte'))} testId="compose-matte" />
            </>
          ) : (
            <Button size="compact-xs" fz="xs" variant="light" w="fit-content" disabled={readOnly} onClick={() => set('matte', { ...NEW_KEYER }, t('insertMatte'))} data-testid="compose-matte-toggle">
              {t('addMatte')}
            </Button>
          )}
        </>
      ) : (
        <>
          <Divider label={t('quadKeys', { count: keys.length })} labelPosition="left" />
          <Text size="xs" c="dimmed">
            {atPlayhead ? t('editingKey', { time: framesToTimecode(atPlayhead.frame, doc.settings.rate) }) : keys.length > 0 ? t('noKeyHere') : t('editingStatic')}
          </Text>
          <CornerFields
            testPrefix="compose-quad"
            value={editing}
            disabled={readOnly}
            onChange={(next) => editCorners(atPlayhead, next)}
          />
          <Group gap={6}>
            <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || atPlayhead !== null} onClick={() => writeQuad([...keys, { frame: playheadFrame, value: editing, interp: 'linear' as const }].sort((a, b) => a.frame - b.frame))} data-testid="compose-quad-add-key">
              {t('addKey')}
            </Button>
            <Button size="compact-xs" fz="xs" variant="subtle" color="red" disabled={readOnly || atPlayhead === null} onClick={() => { const next = keys.filter((k) => k.frame !== playheadFrame); writeQuad(next.length === 0 ? null : next); }} data-testid="compose-quad-remove-key">
              {t('removeKey')}
            </Button>
            <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} onClick={() => editCorners(atPlayhead, IDENTITY_QUAD)}>
              {t('resetCorners')}
            </Button>
          </Group>
          {keys.length > 0 && (
            <Group gap={4}>
              {keys.map((k) => (
                <Button key={k.frame} size="compact-xs" fz="xs" variant={k.frame === playheadFrame ? 'filled' : 'subtle'} ff="monospace" onClick={() => onSeek(k.frame)} aria-label={t('goToKey', { time: framesToTimecode(k.frame, doc.settings.rate) })} data-testid={`compose-quad-key-${k.frame}`}>
                  {framesToTimecode(k.frame, doc.settings.rate)}
                </Button>
              ))}
            </Group>
          )}
        </>
      ),
    );
  }

  if (item.type !== 'mediaClip') {
    return panel(<Alert color="gray" p="xs">{t('noControls', { type: item.type })}</Alert>);
  }

  const clip = item as MediaClip;
  const pin = clip.cornerPin ?? IDENTITY_QUAD;
  // A segmentation mask is made elsewhere (the analysis) and is not a shape: no shape controls, so editing
  // them cannot silently replace it. Show which objects it follows and offer to remove it.
  const objectMask = clip.mask && clip.mask.kind === 'segmentation' ? clip.mask : null;
  const objectLabels = objectMask
    ? objectMask.trackIds.map((id) => tracksFor(objectMask.analysisStepResultId)?.find((track) => track.trackId === id)?.label || id).join(', ')
    : '';

  return panel(
    <>
      {(entry === 'mask' || entry === 'reframe') && (
        <>
          <Divider id={`inspector-section-${entry}`} label={entryName(t, entry)} labelPosition="left" />
          {/* The one way in is the first control: draw on the picture (Redraw when a mask exists). Add mask and Add ellipse stay, quieter. */}
          {entry === 'mask' && !objectMask && !readOnly && (
            <Button
              size="compact-xs"
              fz="xs"
              variant="filled"
              w="fit-content"
              onClick={() => (onDrawMask ? onDrawMask(!!clip.mask) : onActivateViewerTool?.('mask'))}
              aria-keyshortcuts={maskChord.aria}
              title={maskChord.display ? maskChord.tip(clip.mask ? t('redrawMask') : t('drawMask')) : undefined}
              data-testid="compose-mask-draw"
            >
              {clip.mask ? t('redrawMask') : t('drawMask')}
            </Button>
          )}
          <MaskReframeFields part={entry} clip={clip} readOnly={readOnly} onSetProperty={onSetProperty} onActivateViewerTool={onActivateViewerTool} segmentationLabel={objectLabels} quietAdd={entry === 'mask'} removeChord={removeChord} />
          <RenderNote />
        </>
      )}
      {entry === 'keyer' && (
        <>
          <Divider id="inspector-section-keyer" label={t('keyer')} labelPosition="left" />
          {clip.keyer ? (
            <>
              <Button size="compact-xs" fz="xs" variant="subtle" color="red" w="fit-content" disabled={readOnly} aria-keyshortcuts={removeChord.aria} title={removeChord.display ? removeChord.tip(t('removeKeyer')) : undefined} onClick={() => set('keyer', null, t('keyerLabel'))} data-testid="compose-keyer-toggle">
                {t('removeKeyer')}
              </Button>
              <KeyerFields value={clip.keyer} readOnly={readOnly} onChange={(next) => set('keyer', next, t('keyerLabel'))} testId="compose-keyer" />
            </>
          ) : (
            <Button size="compact-xs" fz="xs" variant="light" w="fit-content" disabled={readOnly} onClick={() => set('keyer', { ...NEW_KEYER }, t('keyerLabel'))} data-testid="compose-keyer-toggle">
              {t('addKeyer')}
            </Button>
          )}
          <RenderNote />
        </>
      )}
      {entry === 'pin' && (
        <>
          <Divider id="inspector-section-corner-pin" label={t('cornerPin')} labelPosition="left" />
          {clip.cornerPin ? null : (
            <Button size="compact-xs" fz="xs" variant="light" w="fit-content" disabled={readOnly} onClick={() => {
              set('cornerPin', IDENTITY_QUAD, t('cornerPinLabel'));
              // Add puts the handles on the picture: the tool is entered, as Add mask and Add reframe do.
              onActivateViewerTool?.('pin');
            }} data-testid="compose-pin-toggle">
              {t('addPin')}
            </Button>
          )}
          {clip.cornerPin && (
            <CornerFields testPrefix="compose-pin" value={pin} disabled={readOnly} range onChange={(next) => set('cornerPin', next, t('cornerPinLabel'))} />
          )}
          {clip.cornerPin && (
            <Button size="compact-xs" fz="xs" variant="subtle" color="red" w="fit-content" disabled={readOnly} aria-keyshortcuts={removeChord.aria} title={removeChord.display ? removeChord.tip(t('removePin')) : undefined} onClick={() => set('cornerPin', null, t('cornerPinLabel'))} data-testid="compose-pin-toggle">
              {t('removePin')}
            </Button>
          )}
          <RenderNote />
        </>
      )}
      {entry === 'effects' && (
        <>
          <Divider label={t('effects')} labelPosition="left" />
          <EffectsEditor projectId={projectId} readOnly={readOnly} effects={clip.effects} write={(next) => set('effects', next, t('effectsLabel'))} />
          <RenderNote />
        </>
      )}
      {entry === 'motion' && (
        <>
          <Divider label={t('motionFraming')} labelPosition="left" />
          <Group gap="md">
            <Tooltip label={t('stabiliseTip')}>
              <Switch id="inspector-section-stabilize" size="xs" label={t('stabilise')} checked={clip.stabilize === true} disabled={readOnly} onChange={(e) => set('stabilize', e.currentTarget.checked ? true : undefined, t('stabilise'))} data-testid="compose-stabilize" />
            </Tooltip>
            <Select size="xs" label={t('slowMotion')} data={RETIME_QUALITIES.map((q) => ({ value: q, label: q === 'opticalFlow' ? t('opticalFlow') : t('frameDuplication') }))} value={clip.retimeQuality ?? 'frame'} onChange={(v) => v && set('retimeQuality', v, t('retimeQuality'))} allowDeselect={false} disabled={readOnly} w={200} data-testid="compose-retime-quality" />
          </Group>
          <RenderNote motion />
        </>
      )}
      {entry === 'chain' && (
        <>
          <Divider label={t('nodeView')} labelPosition="left" />
          <NodeView doc={doc} item={clip} />
          <RenderNote />
        </>
      )}
    </>,
    preview3s,
  );
}
