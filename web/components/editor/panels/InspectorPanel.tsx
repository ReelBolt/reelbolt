'use client';

import { OverlayTextInput } from './OverlayTextInput';
import React, { useRef, useState } from 'react';
import { Badge, Button, Collapse, Divider, Group, NumberInput, SegmentedControl, Select, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core';
import { IconDiamond, IconDiamondFilled, IconPointer, IconRotateClockwise } from '@tabler/icons-react';
import { framesToTimecode } from '@/lib/editing/time';
import type { AssetRef, EditTimeline, Item, Keyframed, MediaClip, Rational } from '@/lib/editing/types';
import {
  TITLE_TEMPLATES,
  GENERATOR_TYPES,
  INTERP_KINDS,
  KEYFRAME_PARAM_RANGE,
  SET_PROPERTY_ALLOWLIST,
  TRANSITION_TYPES,
  type EditOp,
  type KeyframeParam,
  type PropertyKey,
  type PropertyValue,
} from '@/lib/editing/engine';
import type { TransformMirror } from '@/lib/editing/viewer-transform';
import { HDR_CLIP_NOTE, type HdrKindLookup } from '@/lib/editing/media/hdr';
import { classifyItem, evaluateKeyframed, mediaRefOf, type StabilizeLookup } from '@/lib/editing/preview';
import { CameraProfileControl } from '../color/CameraProfileControl';
import type { MediaProbe } from '@/lib/editing/media/types';
import { PresetPicker } from '../presets/PresetPicker';
import type { TransitionPresetPayload } from '@/lib/editing/api/editor-p9';
import { KeyframeLane } from './KeyframeLane';
import { groupOf, linkIdOf, partnersOf } from '@/lib/editing/link/groups';
import { useRefusalToast } from '../shell/refusal-toast';
import { CommitNumberInput } from '../shell/CommitNumberInput';
import { ClipTimingFields } from './ClipTimingFields';
import { ColorSwatchPicker } from '../place/ColorSwatchPicker';
import { TitlePositionPad } from '../place/TitlePositionPad';
import { MaskReframeFields, type MaskReframeTool } from './MaskReframeFields';
import { ObjectEffectsSection } from './ObjectEffectsSection';
import { EnhanceSection } from '../enhance/EnhanceSection';
import { FollowSection } from './FollowSection';
import { AnimateSection } from './clip-depth/AnimateSection';
import { AutoFadeButton, BlendControl, FadeControls, OpacityControl, ResetButton, SpeedControl, VolumeControl } from './clip-depth/ClipControls';
import { TransitionEditor, TransitionPicker } from './clip-depth/TransitionPicker';
import { MUTE_DB, transitionLabel } from '@/lib/editing/clip-depth';
import { itemTypeLabel } from './itemTypeLabel';
import { EditorIcon } from '../ui';
import { EditorEmpty } from '../ui';
import { EditorPanel } from '../ui/EditorPanel';
import { useT } from '@/i18n';

export interface InspectorPanelProps {
  /** R07.2: the clip's transform at the playhead following a live viewer drag; absent = read the document. */
  transformMirror?: TransformMirror | null;
  doc: EditTimeline;
  selection: readonly string[];
  rate: Rational;
  readOnly: boolean;
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => void;
  /** P5: the playhead, where "add key" lands. Absent = the P3/P4 inspector without keyframe controls. */
  playheadFrame?: number;
  /** P5: dispatch any op (keyframes, retime, transitions) as one undo step. */
  onOp?: (op: EditOp, label?: string) => void;
  /** R07: apply several ops as ONE undo step (motion presets, transitions, make room). */
  onBatch?: (ops: EditOp[], label: string) => void;
  /** P5: fresh ids for the transitions the inspector adds. */
  newId?: () => string;
  onSeek?: (frame: number) => void;
  /** P6: the project's sources a media clip may be relinked to (value = the pool mediaRef). */
  relinkOptions?: readonly { value: string; label: string; assetRef: AssetRef }[];
  /** P9: the project, for its saved transition presets. */
  projectId?: string | null;
  /** Extra actions for a selected media clip (e.g. "Extract audio"), given the clip and its track's kind. */
  mediaClipActions?: (item: MediaClip, trackKind: 'video' | 'audio') => React.ReactNode;
  /** R03.3: typed in / out / duration. Returns a plain sentence when refused, else null. Absent = read-only readout. */
  onNumericTrim?: (itemId: string, field: 'sourceIn' | 'recordOut' | 'duration', frames: number) => string | null;
  /** R10.7: whether a clip's source is HDR (the Inspector says so and the parity reason explains the preview). */
  hdrKindFor?: HdrKindLookup;
  /** Whether a stabilised clip's stabilised proxy is ready (parity wording); see StabilizeLookup. */
  stabilizeFor?: StabilizeLookup;
  /** Whether a grade LUT file has loaded (the parity reason says "still loading" otherwise). */
  gradeLutStatus?: (file: AssetRef) => 'loading' | 'ready' | 'failed';
  /** The probe facts of the clip's source (the camera-profile suggestion reads the hint). */
  probeFor?: (clip: MediaClip) => MediaProbe | null;
  /** §7.3: the mask/reframe fields activate the matching viewer tool (pauses playback and focuses the picture). */
  onActivateViewerTool?: (tool: MaskReframeTool) => void;
}

/** The short side of a probed source (the plan cap is checked on it). */
function shortSide(probe: MediaProbe | null | undefined): number | null {
  if (!probe?.height) return null;
  return probe.width ? Math.min(probe.width, probe.height) : probe.height;
}

function findItem(doc: EditTimeline, id: string): { item: Item; trackId: string; trackName: string; index: number; next: Item | undefined } | null {
  for (const track of doc.tracks) {
    const index = track.items.findIndex((i) => i.id === id);
    if (index >= 0) return { item: track.items[index], trackId: track.id, trackName: track.name, index, next: track.items[index + 1] };
  }
  return null;
}

/** The ids of the selection when it is exactly one link group (a picture and its sound), in track order; else null. */
function linkedGroupIds(doc: EditTimeline, selection: readonly string[]): string[] | null {
  const first = findItem(doc, selection[0]);
  const linkId = first ? linkIdOf(first.item) : null;
  if (linkId === null) return null;
  const members = groupOf(doc, selection[0]).map((m) => m.item.id);
  return members.length === selection.length && selection.every((id) => members.includes(id)) ? members : null;
}

/** One fade property as a setProperty op (0 frames removes the key). */
function setFadeOp(itemId: string, key: 'fadeInFrames' | 'fadeOutFrames', frames: number): EditOp {
  return { type: 'setProperty', itemId, key, value: frames > 0 ? Math.round(frames) : undefined };
}

function groupLabel(doc: EditTimeline, id: string): string {
  const found = findItem(doc, id);
  return found ? found.trackName : id;
}

const TRANSFORM_CHANNELS: { param: KeyframeParam; step: number; identity: number }[] = [
  { param: 'positionX', step: 0.01, identity: 0 },
  { param: 'positionY', step: 0.01, identity: 0 },
  { param: 'scale', step: 0.05, identity: 1 },
  { param: 'rotation', step: 1, identity: 0 },
  { param: 'opacity', step: 0.05, identity: 1 },
];

/**
 * The inspector — wired to REAL ops. Every field writes through `setProperty`, which the engine
 * validates against the per-kind allowlist; nothing here mutates the document directly. P5 adds
 * the Transform, Crop, Retime, Fades and Audio sections for a media clip, each numeric channel
 * with a keyframe toggle: with no keys the field edits the static value; once a channel has keys
 * the field writes a key AT THE PLAYHEAD (`setKeyframe`), and the lane under it is the curve editor.
 */
export function InspectorPanel({ doc, selection, rate, readOnly, onSetProperty, playheadFrame = 0, onOp, onBatch, newId, onSeek, relinkOptions, projectId = null, mediaClipActions, onNumericTrim, transformMirror = null, hdrKindFor, gradeLutStatus, stabilizeFor, probeFor, onActivateViewerTool }: InspectorPanelProps) {
  const tx = useT('editor.panels.inspectorPanel');
  const { showRefusalMessage } = useRefusalToast();
  const [lane, setLane] = useState<KeyframeParam | null>(null);
  const [focusId, setFocusId] = useState<string | null>(null);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  /** The volume a muted clip returns to when unmuted (kept for the session, per clip). */
  const volumeBeforeMute = useRef<Record<string, number>>({});
  // W0.6 (d3 N1): leaving a number field remounts it from the document, so a refused value never
  // stays on screen while the document keeps the old one. Each field has its own epoch (C-326-1):
  // only the field being left remounts, never the one the focus is moving to.
  const [fieldEpochs, setFieldEpochs] = useState<Record<string, number>>({});
  const fieldKey = (field: string) => `${field}-${fieldEpochs[field] ?? 0}`;
  const revertOnBlur = (field: string) => () => setFieldEpochs((e) => ({ ...e, [field]: (e[field] ?? 0) + 1 }));
  // T-008: one surface header language — the Inspector is an EditorPanel inside its tab group.
  const surface = (body: React.ReactNode, actions?: React.ReactNode) => <EditorPanel title={tx('inspector')} actions={actions} testId="inspector-panel">{body}</EditorPanel>;

  if (selection.length === 0) {
    return surface(<EditorEmpty compact icon={IconPointer} title={tx('nothingSelected')} hint={tx('selectAClipTo')} testId="inspector-empty" />);
  }
  // R08.1: a selected linked pair is ONE thing to inspect; a switch picks which half the fields show.
  const groupIds = selection.length > 1 ? linkedGroupIds(doc, selection) : null;
  const activeId = groupIds ? (groupIds.includes(focusId ?? '') ? (focusId as string) : groupIds[0]) : selection[0];
  if (selection.length > 1 && !groupIds) {
    return surface(
      <Text size="xs" c="dimmed">
        {tx('itemsSelectedEditThem', { length: selection.length })}
      </Text>,
    );
  }

  const located = findItem(doc, activeId);
  if (!located) {
    return surface(
      <Text size="xs" c="dimmed">
        {tx('theSelectedItemIs')}
      </Text>,
    );
  }

  const { item, trackName, next } = located;
  // S7: a clip's controls follow the track it sits on. A picture track shows the picture (transform, crop,
  // freeze frame) and no sound controls - the sound of a clip on a picture track is neither previewed nor
  // rendered (SegmentPlanner mixes only audio tracks); a sound track shows the sound controls and no picture ones.
  const trackKind: 'video' | 'audio' = doc.tracks.find((t) => t.id === located.trackId)?.kind ?? 'video';
  const onPictureTrack = trackKind === 'video';
  const fields = item as unknown as Record<string, unknown>;
  const allowed = SET_PROPERTY_ALLOWLIST[item.type] ?? [];
  const parity = classifyItem(item, doc, { hdrKindFor, gradeLutStatus, stabilizeFor });
  const isHdr = item.type === 'mediaClip' && Boolean(hdrKindFor?.(item as MediaClip));
  const provenance = item.provenance ?? null;
  const keyframesEnabled = Boolean(onOp) && !readOnly;
  /** Several ops as one undo step; without a batch dispatcher they go one by one. */
  const batch = (ops: EditOp[], label: string) => {
    if (onBatch) onBatch(ops, label);
    else for (const op of ops) onOp?.(op, label);
  };

  /** The state and writers of one keyframable channel of a media clip. */
  const channelState = (clip: MediaClip, param: KeyframeParam, identity: number) => {
    const channel: Keyframed<number> | null | undefined = param === 'gainDb' ? clip.gainDb : clip.transform?.[param as keyof NonNullable<MediaClip['transform']>];
    const hasKeys = Array.isArray(channel?.keys) && channel!.keys!.length > 0;
    const stored = evaluateKeyframed(channel ?? null, playheadFrame, typeof channel?.value === 'number' ? channel.value : identity);
    // A viewer drag in progress shows in the number before release (nothing is written until then).
    const liveValue = transformMirror?.live && transformMirror && param !== 'gainDb' && param !== 'opacity' ? transformMirror.values[param as 'positionX' | 'positionY' | 'scale' | 'rotation'] : undefined;
    const current = typeof liveValue === 'number' ? liveValue : stored;
    const writeStatic = (value: number) => {
      if (param === 'gainDb') {
        onSetProperty(clip.id, 'gainDb', { value, keys: clip.gainDb?.keys ?? null });
        return;
      }
      const transform = { ...(clip.transform ?? {}) };
      (transform as Record<string, unknown>)[param] = value === identity && !hasKeys ? undefined : { value, keys: channel?.keys ?? null };
      const cleaned = Object.fromEntries(Object.entries(transform).filter(([, v]) => v !== undefined));
      onSetProperty(clip.id, 'transform', Object.keys(cleaned).length === 0 ? undefined : cleaned);
    };
    const writeKey = (value: number) => onOp?.({ type: 'setKeyframe', target: { itemId: clip.id }, param, frame: playheadFrame, value, interp: 'linear' }, `Set ${param} key`);
    /** A typed value: a key at the playhead once the channel is animated, else the static value. */
    const commit = (value: number) => {
      if (hasKeys && keyframesEnabled) writeKey(value);
      else writeStatic(value);
    };
    return { channel, hasKeys, current, writeStatic, writeKey, commit };
  };

  /**
   * One keyframable channel of a media clip. `parts`: the number field, the keyframe controls (diamond, key
   * count, lane) or both. The diamond and lane live under "Fine-tune keyframes" (R07.1); the field stays in place.
   */
  const channelRow = (clip: MediaClip, param: KeyframeParam, label: string, step: number, identity: number, parts: 'field' | 'keys' | 'both' = 'both') => {
    const { channel, hasKeys, current, commit, writeKey } = channelState(clip, param, identity);
    const range = KEYFRAME_PARAM_RANGE[param];
    const atPlayhead = hasKeys && channel!.keys!.some((k) => k.frame === playheadFrame);
    return (
      <Stack gap={2} key={param}>
        <Group gap={4} wrap="nowrap" align="flex-end">
          {parts !== 'keys' ? (
            <CommitNumberInput
              key={fieldKey(param)}
              size="xs"
              label={label}
              value={Number(current.toFixed(3))}
              step={step}
              min={range.exclusiveMin ? range.min + step : range.min}
              max={range.max}
              disabled={readOnly}
              onCommit={commit}
              onBlur={revertOnBlur(param)}
              style={{ flex: 1 }}
              data-testid={`inspector-${param}`}
            />
          ) : (
            <Text size="xs" style={{ flex: 1 }}>
              {label}
            </Text>
          )}
          {parts !== 'field' && keyframesEnabled && (
            <Tooltip label={atPlayhead ? tx('aKeySitsAt') : hasKeys ? tx('addAKeyAt') : tx('startAnimatingAddThe')}>
              <EditorIcon tooltip={false} icon={atPlayhead ? IconDiamondFilled : IconDiamond} size="sm"
                variant={hasKeys ? 'light' : 'subtle'}
                color={atPlayhead ? 'yellow' : 'blue'}
                label={tx('toggleKeyframe', { label })}
                onClick={() => {
                  if (atPlayhead) onOp?.({ type: 'removeKeyframe', target: { itemId: clip.id }, param, frame: playheadFrame }, `Remove ${param} key`);
                  else writeKey(current);
                  setLane(param);
                }}
                data-testid={`inspector-key-${param}`} />
            </Tooltip>
          )}
          {parts !== 'field' && hasKeys && (
            <Button size="compact-xs" fz="xs" variant={lane === param ? 'filled' : 'subtle'} onClick={() => setLane(lane === param ? null : param)} data-testid={`inspector-lane-${param}`}>
              {channel!.keys!.length}{'◆' /* eslint-disable-line i18next/no-literal-string -- symbol, not copy */}
            </Button>
          )}
        </Group>
        {parts !== 'field' && lane === param && hasKeys && onOp && (
          <KeyframeLane
            target={{ itemId: clip.id }}
            param={param}
            channel={channel}
            startFrame={clip.recordIn}
            endFrame={clip.recordOut}
            playheadFrame={playheadFrame}
            readOnly={readOnly}
            onOp={onOp}
            onSeek={onSeek}
          />
        )}
      </Stack>
    );
  };

  return surface(
    <Stack gap="xs" data-item-id={item.id}>

      {/* eslint-disable-next-line i18next/no-literal-string -- ids and symbols, not copy */}
      {allowed.includes('name') && (
        <TextInput
          size="xs"
          label={tx('name')}
          defaultValue={item.name}
          key={`${item.id}-name-${item.name}`}
          disabled={readOnly}
          onBlur={(e) => {
            const value = e.currentTarget.value;
            if (value === item.name) return;
            if (value.trim().length === 0) {
              // d1 N6: a clip keeps a name; say so and put the stored one back.
              e.currentTarget.value = item.name;
              showRefusalMessage('A clip needs a name. The old name is kept.');
              return;
            }
            onSetProperty(item.id, 'name', value);
          }}
          onKeyDown={(e) => e.stopPropagation()}
          data-testid="inspector-name"
        />
      )}
      {groupIds && (
        <SegmentedControl
          size="xs"
          fullWidth
          value={activeId}
          onChange={setFocusId}
          data={groupIds.map((id) => ({ value: id, label: groupLabel(doc, id) }))}
          data-testid="inspector-link-member"
        />
      )}
      {item.type === 'mediaClip' && partnersOf(doc, item.id).length > 0 && (
        <Group gap={6} wrap="nowrap" data-testid="inspector-linked">
          <Badge size="md" variant="light" color="blue" tt="none">
            {tx('linked')}
          </Badge>
          <Text size="12px" c="dimmed">
            {tx('movesTrimsSplitsAnd')} {partnersOf(doc, item.id).map((p) => p.track.name).join(', ')}{tx('rightClickThenUnlink')}
          </Text>
        </Group>
      )}
      {/* eslint-disable-next-line i18next/no-literal-string -- ids and symbols, not copy */}
      {allowed.includes('enabled') && (
        <Switch
          size="xs"
          label={tx('enabled')}
          checked={fields.enabled !== false}
          disabled={readOnly}
          onChange={(e) => onSetProperty(item.id, 'enabled', e.currentTarget.checked)}
          data-testid="inspector-enabled"
        />
      )}

      <Divider id="inspector-section-transform" label={tx('geometry')} labelPosition="left" />
      <Group gap="md">
        <Stack gap={0}>
          <Text size="12px" c="dimmed">
            {tx('track')}
          </Text>
          <Text size="xs">{trackName}</Text>
        </Stack>
      </Group>

      {onNumericTrim ? (
        <ClipTimingFields item={item} rate={rate} readOnly={readOnly} onApply={(field, frames) => onNumericTrim(item.id, field, frames)} />
      ) : (
        <Text size="xs" ff="monospace">
          {tx('timeRange', { start: framesToTimecode(item.recordIn, rate), end: framesToTimecode(item.recordOut, rate), frames: item.recordOut - item.recordIn })}
        </Text>
      )}

      {item.type === 'mediaClip' && (
        <>
          <Divider label={tx('retime')} labelPosition="left" />
          <SpeedControl
            clip={item}
            rate={rate}
            readOnly={readOnly || !onOp}
            fieldKey={fieldKey}
            revertOnBlur={revertOnBlur}
            onApply={(plan) => {
              if (!onOp) return;
              if (plan.fadesShrunk) {
                // The fades shrink first (the engine refuses a retime that leaves them longer than the clip).
                onSetProperty(item.id, 'fadeInFrames', plan.fadeInFrames > 0 ? plan.fadeInFrames : undefined);
                onSetProperty(item.id, 'fadeOutFrames', plan.fadeOutFrames > 0 ? plan.fadeOutFrames : undefined);
                showRefusalMessage(tx('theFadesWereLonger'));
              }
              onOp({ type: 'retime', itemId: item.id, playbackRate: plan.playbackRate, recordOut: item.recordIn + plan.durationFrames }, `Retime ${plan.playbackRate}x`);
            }}
          />
          <Group gap="md">
            <Switch size="xs" label={tx('reverse')} checked={item.reverse === true} disabled={readOnly || item.hold === true} onChange={(e) => onSetProperty(item.id, 'reverse', e.currentTarget.checked ? true : undefined)} data-testid="inspector-reverse" />
            {onPictureTrack && (
              <Switch size="xs" label={tx('freezeFrame')} checked={item.hold === true} disabled={readOnly || item.reverse === true} onChange={(e) => onSetProperty(item.id, 'hold', e.currentTarget.checked ? true : undefined)} data-testid="inspector-hold" />
            )}
            <ResetButton testId="inspector-reset-speed" disabled={readOnly || !onOp || (item.playbackRate ?? 1) === 1} onClick={() => onOp?.({ type: 'retime', itemId: item.id, playbackRate: 1, recordOut: item.recordIn + Math.max(1, Math.round((item.recordOut - item.recordIn) * (item.playbackRate ?? 1))) }, tx('resetSpeed'))} />
          </Group>
          {onPictureTrack && (
            <Group gap="xs" data-testid="inspector-orientation">
              <Tooltip label={tx('rotate90Tip')}>
                <Button size="compact-xs" fz="xs" variant="default" leftSection={<IconRotateClockwise size={14} aria-hidden />} aria-label={tx('rotate90')} disabled={readOnly} onClick={() => { const r = item.sourceRotation; onSetProperty(item.id, 'sourceRotation', r === 90 ? 180 : r === 180 ? 270 : r === 270 ? undefined : 90); }} data-testid="inspector-rotate90">
                  {tx('rotate90')}
                </Button>
              </Tooltip>
              <Text size="xs" c="dimmed" aria-label={tx('orientation')} data-testid="inspector-orientation-readout">{item.sourceRotation ? `${item.sourceRotation}°` : tx('upright')}</Text>
            </Group>
          )}

          {!onPictureTrack && (
            <>
          <Divider label={tx('audioChannels')} labelPosition="left" />
          <Select
            size="xs"
            label={tx('channels')}
            description={tx('howTheSourceS')}
            data={[
              { value: 'stereo', label: tx('stereoAsRecorded') },
              { value: 'left', label: tx('leftOnlyBothSides') },
              { value: 'right', label: tx('rightOnlyBothSides') },
              { value: 'mono', label: tx('monoFoldDown') },
              { value: 'swap', label: tx('swapLeftRight') },
            ]}
            value={item.channelMap ?? 'stereo'}
            allowDeselect={false}
            disabled={readOnly}
            onChange={(v) => v && onSetProperty(item.id, 'channelMap', v === 'stereo' ? undefined : v)}
            data-testid="inspector-channel-map"
          />
            </>
          )}
          {onPictureTrack && (
            <>
          {keyframesEnabled && (
            <>
              <Divider label={tx('animate')} labelPosition="left" />
              <AnimateSection
                key={item.id}
                clip={item}
                rate={rate}
                readOnly={readOnly}
                onBatch={batch}
                onSeek={onSeek}
                fineTune={TRANSFORM_CHANNELS.map((c) => channelRow(item, c.param, tx(`transformChannels.${c.param}`), c.step, c.identity, 'keys'))}
              />
            </>
          )}

          {item.type === 'mediaClip' && !isHdr && (
            <>
              <Divider label={tx('cameraProfile')} labelPosition="left" />
              <CameraProfileControl
                clip={item}
                doc={doc}
                readOnly={readOnly}
                probe={probeFor?.(item) ?? null}
                onSet={(profile, label) => onSetProperty(item.id, 'inputProfile', profile, label)}
                onBatch={batch}
              />
            </>
          )}

          <Divider label={tx('transform')} labelPosition="left" />
          {/* eslint-disable i18next/no-literal-string -- property names and channel ids, not copy */}
          {TRANSFORM_CHANNELS.filter((c) => c.param !== 'opacity').flatMap((c) => {
            if (c.param !== 'scale') return [channelRow(item, c.param, tx(`transformChannels.${c.param}`), c.step, c.identity, 'field')];
            // Scale is the width once the clip has its own height (`scaleY`); the link switch goes back to one uniform scale.
            const linked = !item.transform?.scaleY;
            const scaleNow = channelState(item, 'scale', 1).current;
            const heightState = channelState(item, 'scaleY', 1);
            const writeHeight = (v: number) => onSetProperty(item.id, 'transform', { ...(item.transform ?? {}), scaleY: { value: v, keys: heightState.channel?.keys ?? null } });
            const setLinked = (on: boolean) => {
              if (!on) return writeHeight(scaleNow);
              const { scaleY: _drop, ...rest } = item.transform ?? {};
              void _drop;
              onSetProperty(item.id, 'transform', Object.keys(rest).length === 0 ? undefined : rest);
            };
            return [
              channelRow(item, c.param, linked ? tx('transformChannels.scale') : tx('width'), c.step, c.identity, 'field'),
              <Group key="scaleY" gap={8} wrap="nowrap" align="flex-end">
                <CommitNumberInput
                  key={fieldKey('scaleY')}
                  size="xs"
                  label={tx('height')}
                  value={Number((linked ? scaleNow : heightState.current).toFixed(3))}
                  step={c.step}
                  min={KEYFRAME_PARAM_RANGE.scaleY.min + c.step}
                  max={KEYFRAME_PARAM_RANGE.scaleY.max}
                  disabled={readOnly}
                  onCommit={writeHeight}
                  onBlur={revertOnBlur('scaleY')}
                  style={{ flex: 1 }}
                  data-testid="inspector-scaleY"
                />
                <Switch size="xs" label={tx('keepShape')} checked={linked} disabled={readOnly} onChange={(e) => setLinked(e.currentTarget.checked)} data-testid="inspector-scale-link" />
              </Group>,
            ];
          })}
          {/* eslint-enable i18next/no-literal-string */}
          <OpacityControl
            current={channelState(item, 'opacity', 1).current}
            fieldKey={fieldKey}
            revertOnBlur={revertOnBlur}
            readOnly={readOnly}
            onApply={channelState(item, 'opacity', 1).commit}
          />
          {item.type === 'mediaClip' && onPictureTrack && (
            <BlendControl current={item.blendMode} readOnly={readOnly} onApply={(mode) => onSetProperty(item.id, 'blendMode', mode === 'normal' ? undefined : mode)} />
          )}
          <Group justify="flex-end">
            <ResetButton testId="inspector-reset-transform" disabled={readOnly || !item.transform} onClick={() => onSetProperty(item.id, 'transform', undefined)} />
          </Group>

          <Divider id="inspector-section-crop" label={tx('crop')} labelPosition="left" />
          <Group grow>
            {/* eslint-disable-next-line i18next/no-literal-string -- ids, units and test ids, not copy */}
            {(['left', 'top', 'right', 'bottom'] as const).map((edge) => (
              <CommitNumberInput
                key={edge}
                size="xs"
                label={edge[0].toUpperCase() + edge.slice(1)}
                value={item.crop?.[edge] ?? 0}
                min={0}
                max={0.9}
                step={0.05}
                disabled={readOnly}
                onCommit={(n) => {
                  const crop = { ...(item.crop ?? {}), [edge]: n };
                  const cleaned = Object.fromEntries(Object.entries(crop).filter(([, value]) => typeof value === 'number' && value > 0));
                  onSetProperty(item.id, 'crop', Object.keys(cleaned).length === 0 ? undefined : cleaned);
                }}
                data-testid={`inspector-crop-${edge}`}
              />
            ))}
          </Group>

          <FollowSection key={`follow-${item.id}`} clip={item} projectId={projectId} readOnly={readOnly} allowDraw probe={probeFor?.(item) ?? null} />

          <Divider label={tx('maskAndReframe')} labelPosition="left" />
          <MaskReframeFields clip={item} readOnly={readOnly} onSetProperty={onSetProperty} onActivateViewerTool={onActivateViewerTool} />

          <Divider label={tx('objects')} labelPosition="left" />
          <ObjectEffectsSection key={item.id} clip={item} projectId={projectId} readOnly={readOnly} onSetProperty={onSetProperty} />
          {onBatch && <EnhanceSection key={`enhance-${item.id}`} clip={item} projectId={projectId ?? null} readOnly={readOnly} doc={doc} onBatch={onBatch} sourceHeight={shortSide(probeFor?.(item))} />}
            </>
          )}

          <Divider label={tx('fades')} labelPosition="left" />
          <FadeControls
            clip={item}
            rate={rate}
            readOnly={readOnly}
            fieldKey={fieldKey}
            revertOnBlur={revertOnBlur}
            onApply={(which, frames) => onSetProperty(item.id, which === 'in' ? 'fadeInFrames' : 'fadeOutFrames', frames > 0 ? Math.round(frames) : undefined)}
          />
          <Group gap={4}>
            <AutoFadeButton clip={item} rate={rate} readOnly={readOnly} onApply={(frames) => batch([setFadeOp(item.id, 'fadeInFrames', frames), setFadeOp(item.id, 'fadeOutFrames', frames)], tx('autoFade'))} />
            <ResetButton testId="inspector-reset-fades" disabled={readOnly || (item.fadeInFrames ?? 0) + (item.fadeOutFrames ?? 0) === 0} onClick={() => batch([setFadeOp(item.id, 'fadeInFrames', 0), setFadeOp(item.id, 'fadeOutFrames', 0)], tx('resetFades'))} />
          </Group>

          <Divider label={onPictureTrack ? tx('sound') : tx('volume')} labelPosition="left" />
          {onPictureTrack ? (
            <Text size="12px" c="dimmed" data-testid="inspector-picture-track-sound-note">
              {tx('theSoundOfA')}
            </Text>
          ) : (
            <>
              <VolumeControl
                gainDb={typeof item.gainDb?.value === 'number' ? item.gainDb.value : 0}
                fieldKey={fieldKey}
                revertOnBlur={revertOnBlur}
                readOnly={readOnly}
                onApply={(db) => channelState(item, 'gainDb', 0).writeStatic(db)}
                onMute={(muted) => {
                  const now = typeof item.gainDb?.value === 'number' ? item.gainDb.value : 0;
                  if (muted) {
                    volumeBeforeMute.current[item.id] = now;
                    channelState(item, 'gainDb', 0).writeStatic(MUTE_DB);
                  } else channelState(item, 'gainDb', 0).writeStatic(volumeBeforeMute.current[item.id] ?? 0);
                }}
              />
              <Group justify="flex-end">
                <ResetButton testId="inspector-reset-volume" disabled={readOnly || (typeof item.gainDb?.value === 'number' ? item.gainDb.value : 0) === 0} onClick={() => channelState(item, 'gainDb', 0).writeStatic(0)} />
              </Group>
            </>
          )}
          {mediaClipActions?.(item, trackKind)}

          {relinkOptions && relinkOptions.length > 0 && (
            <>
              <Divider label={tx('source')} labelPosition="left" />
              <Select
                size="xs"
                label={tx('relinkTo')}
                description={tx('pointThisClipAt')}
                data={relinkOptions.map((o) => ({ value: o.value, label: o.label }))}
                value={mediaRefOf(item.assetRef)}
                searchable
                allowDeselect={false}
                disabled={readOnly}
                onChange={(v) => {
                  const option = relinkOptions.find((o) => o.value === v);
                  if (option && v !== mediaRefOf(item.assetRef)) onSetProperty(item.id, 'assetRef', option.assetRef);
                }}
                data-testid="inspector-relink"
              />
            </>
          )}

          {onOp && newId && next?.type === 'mediaClip' && next.recordIn === item.recordOut && (
            <>
              <Divider label={tx('transition')} labelPosition="left" />
              <TransitionPicker doc={doc} seam={{ trackId: located.trackId, outgoing: item, incoming: next }} rate={rate} readOnly={readOnly} newId={newId} onApply={batch} />
            </>
          )}

          <Divider label={tx('advanced')} labelPosition="left" />
          <Button size="compact-sm" fz="xs" variant="subtle" onClick={() => setAdvancedOpen((o) => !o)} aria-expanded={advancedOpen} data-testid="inspector-advanced-toggle">
            {advancedOpen ? tx('hideAdvanced') : tx('showAdvanced')}
          </Button>
          <Collapse expanded={advancedOpen} data-testid="inspector-advanced">
            <Stack gap="xs">
              {!onPictureTrack && channelRow(item, 'gainDb', tx('clipGainDb'), 0.5, 0) /* eslint-disable-line i18next/no-literal-string -- ids, units and test ids, not copy */}
              {onOp && newId && next?.type === 'mediaClip' && next.recordIn === item.recordOut && (
                <>
                  <Text size="xs" fw={600}>
                    {tx('transitionByFrames')}
                  </Text>
                  <AddTransitionRow
                    projectId={projectId}
                    readOnly={readOnly}
                    outgoing={item}
                    incoming={next}
                    onAdd={(transitionType, frames) => {
                      const before = Math.floor(frames / 2);
                      onOp(
                        {
                          type: 'addTransition',
                          afterItemId: item.id,
                          transition: { id: newId(), type: 'transition', name: transitionLabel(transitionType), transitionType, easing: 'linear', enabled: true, provenance: null },
                          framesBefore: before,
                          framesAfter: frames - before,
                        },
                        `Add ${transitionType}`,
                      );
                    }}
                  />
                </>
              )}
              {onPictureTrack && (
                <Text size="12px" c="dimmed">
                  {tx('nothingElseIsAdvanced')}
                </Text>
              )}
            </Stack>
          </Collapse>
        </>
      )}

      {item.type === 'transition' && (
        <>
          <Divider label={tx('transition2')} labelPosition="left" />
          <TransitionEditor
            transition={item}
            cutFrame={item.recordIn + Math.floor((item.recordOut - item.recordIn) / 2)}
            readOnly={readOnly || !onOp}
            onApply={batch}
          />
          <Select
            size="xs"
            label={tx('easingAdvanced')}
            data={INTERP_KINDS.map((k) => ({ value: k, label: k }))}
            value={item.easing ?? 'linear'}
            allowDeselect={false}
            disabled={readOnly}
            onChange={(v) => v && onSetProperty(item.id, 'easing', v)}
            data-testid="inspector-transition-easing"
          />
        </>
      )}

      {item.type === 'generatorClip' && (
        <>
          <Divider label={tx('generator')} labelPosition="left" />
          <Select
            size="xs"
            label={tx('type')}
            data={GENERATOR_TYPES.map((g) => ({ value: g, label: g }))}
            value={item.generatorType}
            allowDeselect={false}
            disabled={readOnly}
            onChange={(v) => v && onSetProperty(item.id, 'generatorType', v)}
            data-testid="inspector-generator-type"
          />
          {item.generatorType === 'color' && (
            <ColorSwatchPicker
              value={typeof item.generatorConfig?.color === 'string' ? item.generatorConfig.color : '#000000'}
              disabled={readOnly}
              testIdPrefix="inspector-generator-swatch"
              hexTestId="inspector-generator-color"
              onChange={(color) => onSetProperty(item.id, 'generatorConfig', { ...(item.generatorConfig ?? {}), color })}
            />
          )}
        </>
      )}

      {item.type === 'titleClip' && (
        <>
          <Divider label={tx('title')} labelPosition="left" />
          <Select
            size="xs"
            label={tx('template')}
            data={TITLE_TEMPLATES.map((t) => ({ value: t, label: t === 'plain' ? tx('plainCentred') : t === 'lowerThird' ? tx('lowerThird') : t === 'boxed' ? tx('templateBoxed') : t === 'outline' ? tx('templateOutline') : tx('templateCard') }))}
            value={item.template ?? 'plain'}
            allowDeselect={false}
            disabled={readOnly}
            onChange={(v) => v && onSetProperty(item.id, 'template', v === 'plain' ? null : v)}
            data-testid="inspector-title-template"
          />
          <TitlePositionPad
            value={item.position ?? null}
            disabled={readOnly || item.template === 'lowerThird' || item.template === 'card'}
            testIdPrefix="inspector-title-position"
            onChange={(next) => onSetProperty(item.id, 'position', next)}
          />
          {(item.template === 'lowerThird' || item.template === 'card') && (
            <Text size="xs" c="dimmed">
              {tx('thisTemplateKeepsIts')}
            </Text>
          )}
          <OverlayTextInput
            size="xs"
            label={tx('text')}
            value={item.text}
            key={`${item.id}-text-${item.text}`}
            disabled={readOnly}
            onCommit={(text) => onSetProperty(item.id, 'text', text)}
            data-testid="inspector-text"
            noteTestId="inspector-text-note"
          />
          <Group grow>
            <CommitNumberInput
              key={fieldKey('font-size')}
              onBlur={revertOnBlur('font-size')}
              size="xs"
              label={tx('fontSize')}
              value={item.fontSize ?? 48}
              min={6}
              max={400}
              disabled={readOnly}
              onCommit={(n) => onSetProperty(item.id, 'fontSize', n)}
              data-testid="inspector-font-size"
            />
            <TextInput
              size="xs"
              label={tx('colour')}
              defaultValue={item.color ?? '#FFFFFF'}
              key={`${item.id}-tcolor-${item.color}`}
              disabled={readOnly}
              onBlur={(e) => {
                const value = e.currentTarget.value.trim();
                if (/^#[0-9a-fA-F]{6}$/.test(value) && value !== item.color) onSetProperty(item.id, 'color', value);
              }}
              onKeyDown={(e) => e.stopPropagation()}
              data-testid="inspector-title-color"
            />
          </Group>
        </>
      )}

      {item.type === 'caption' && (
        <>
          <Divider label={tx('caption')} labelPosition="left" />
          <OverlayTextInput
            size="xs"
            label={tx('text2')}
            value={item.text}
            key={`${item.id}-ctext-${item.text}`}
            disabled={readOnly}
            onCommit={(text) => onSetProperty(item.id, 'text', text)}
            data-testid="inspector-caption-text"
            noteTestId="inspector-caption-text-note"
          />
        </>
      )}

      <Divider label={tx('previewParity')} labelPosition="left" />
      {isHdr && (
        <Badge size="xs" fz="xs" color="grape" variant="light" c="var(--mantine-color-text)" data-testid="inspector-hdr-note">
          {HDR_CLIP_NOTE}
        </Badge>
      )}
      <Badge size="xs" fz="xs" color={parity.parity === 'faithful' ? 'teal' : parity.parity === 'approximate' ? 'yellow' : 'red'} variant="light" c="var(--mantine-color-text)" data-testid="inspector-parity" data-parity={parity.parity}>
        {parity.parity}
      </Badge>
      <Text size="12px" c="dimmed" data-testid="inspector-parity-reason">
        {parity.reason}
      </Text>

      <Divider label={tx('provenance')} labelPosition="left" />
      {provenance ? (
        <Stack gap={2} data-testid="inspector-provenance">
          <Text size="xs">
            {tx('origin')} <Text span fw={600}>{provenance.origin}</Text>
            {provenance.editedByUser ? tx('editedByUser') : ''}
          </Text>
          {provenance.agentName && <Text size="xs">{tx('agent')} {provenance.agentName}</Text>}
          {provenance.providerName && <Text size="xs">{tx('provider')} {provenance.providerName}</Text>}
          {provenance.offeredId && <Text size="xs">{tx('offeredId')} {provenance.offeredId}</Text>}
          {provenance.rationale && (
            <Text size="xs" c="dimmed">
              {provenance.rationale}
            </Text>
          )}
          {!provenance.agentName && !provenance.providerName && provenance.origin !== 'manual' && (
            <Text size="12px" c="dimmed">
              {tx('noAgentOrProvider')}
            </Text>
          )}
        </Stack>
      ) : (
        <Text size="xs" c="dimmed">
          {tx('noProvenanceRecorded')}
        </Text>
      )}
    </Stack>,
    <Badge size="xs" fz="xs" variant="light">
      {itemTypeLabel(item.type, trackKind)}
    </Badge>,
  );
}

function AddTransitionRow({ readOnly, outgoing, incoming, onAdd, projectId }: { readOnly: boolean; outgoing: MediaClip; incoming: MediaClip; onAdd: (type: string, frames: number) => void; projectId?: string | null }) {
  const tx = useT('editor.panels.inspectorPanel');
  const [type, setType] = useState('dissolve');
  const [frames, setFrames] = useState(12);
  const rate = incoming.playbackRate ?? 1;
  const handle = incoming.reverse === true ? Number.POSITIVE_INFINITY : Math.floor((incoming.sourceIn ?? 0) / rate);
  const maxFrames = Math.max(0, Math.min(2 * handle, outgoing.recordOut - outgoing.recordIn - 1, (incoming.recordOut - incoming.recordIn - 1) * 2));
  const ok = frames >= 1 && Math.floor(frames / 2) <= handle && frames - Math.floor(frames / 2) < incoming.recordOut - incoming.recordIn && Math.floor(frames / 2) < outgoing.recordOut - outgoing.recordIn;
  return (
    <Stack gap={4}>
      <Group grow align="flex-end">
        <Select size="xs" label={tx('type2')} data={TRANSITION_TYPES.map((t) => ({ value: t, label: t }))} value={type} allowDeselect={false} searchable onChange={(v) => v && setType(v)} data-testid="inspector-add-transition-type" />
        <NumberInput size="xs" label={tx('lengthFrames')} value={frames} min={1} max={Math.max(1, maxFrames)} onChange={(v) => setFrames(typeof v === 'number' ? v : 1)} data-testid="inspector-add-transition-frames" />
      </Group>
      <PresetPicker<TransitionPresetPayload>
        projectId={projectId}
        kind="transition"
        current={{ transitionType: type, easing: 'linear', framesBefore: Math.floor(frames / 2), framesAfter: frames - Math.floor(frames / 2) }}
        readOnly={readOnly}
        onApply={(p) => {
          setType(p.transitionType);
          setFrames(Math.max(1, p.framesBefore + p.framesAfter));
        }}
        testId="transition-presets"
      />
      <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || !ok} onClick={() => onAdd(type, frames)} data-testid="inspector-add-transition">
        {tx('addCentredOnThe', { type })}
      </Button>
      <Text size="12px" c="dimmed">
        {Number.isFinite(handle)
          ? tx('theNextClipHas', { handle, v3: Math.max(0, Math.min(handle, Math.floor(maxFrames / 2))) })
          : tx('theNextClipIs')}
      </Text>
    </Stack>
  );
}
