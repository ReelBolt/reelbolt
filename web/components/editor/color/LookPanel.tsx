'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Button, Group, Image, SimpleGrid, Stack, Switch, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconRefresh } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { AdjustmentClip, EditTimeline, GradeStack, MediaClip } from '@/lib/editing/types';
import { isActiveGrade, shotMatchPrimaries } from '@/lib/editing/color/grade';
import { computeScopes, sampleCanvas } from '@/lib/editing/color/scopes';
import {
  applyGradeToClips, clipsInScope, cloneStack, currentLook, DEFAULT_STRENGTH, FIX_KEYS, FIX_SPEC, fixValuesOf, GALLERY_LOOKS, gradeRgba, lookPreviewGrade, MAX_STRENGTH, scalePrimaries, withFix, withLook, withMatch, withoutLook,
  type ApplyScope, type FixKey, type GalleryLook,
} from '@/lib/editing/color/look-gallery';
import { fixSummaryParts, hasAutoFix, resolveFixTarget, sourceSizeFromPool, useFixExposure, type FixPart, type FixState } from '@/lib/editing/color/exposure-measure';
import { programPictureCanvas } from '@/lib/editing/stills/stills';
import { useMediaPool } from '@/lib/hooks/use-media';
import { LiveSlider } from '../shell/LiveSlider';
import { useCoarsePointer } from '../thumbnail/useCoarsePointer';
import { gradeClipboard } from './GradeStackEditor';
import { settle, useBeforeAfterSplit } from './useBeforeAfterSplit';
import type { GalleryStill } from './ColorWorkspace';
import { EditorIcon } from '../ui';
import { useFormat, useT } from '@/i18n';

const THUMB_WIDTH = 120;

export interface LookPanelProps {
  doc: EditTimeline;
  /** The clip being graded: a media clip or an adjustment clip. */
  target: MediaClip | AdjustmentClip | null;
  selection: readonly string[];
  readOnly: boolean;
  playheadFrame: number;
  newId: () => string;
  onWriteGrade: (stack: GradeStack | null, label: string) => void;
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSeek: (frame: number) => void;
  gradeBypass: boolean;
  onGradeBypass: (bypass: boolean) => void;
  getCanvas: () => HTMLCanvasElement | null;
  gallery: readonly GalleryStill[];
  onCaptureStill: (label: string) => Promise<void>;
  referenceId: string | null;
  onReference: (id: string | null) => void;
  wipe: number;
  onWipe: (fraction: number) => void;
  onMessage: (text: string | null) => void;
  /**
   * Wave 4: the panel's own brief bypass (taken to read the ungraded frame) starts and ends. A host that shows
   * the bypass elsewhere (the Colour page's compare bar) hides it for that moment, as the switch here does.
   */
  onPanelBypass?: (active: boolean) => void;
  /** Wave 4: counts the times the user chose the bypass outside this panel; a choice made during the brief bypass wins. */
  userBypassSeq?: number;
}

/** The current frame at thumbnail size, from the program canvas; null while it holds no picture. */
function readFrame(getCanvas: () => HTMLCanvasElement | null): ImageData | null {
  const canvas = programPictureCanvas(getCanvas());
  if (!canvas) return null;
  const buffer = sampleCanvas(canvas, THUMB_WIDTH);
  if (!buffer) return null;
  let lit = false;
  for (let i = 0; i < buffer.data.length; i += 16) if (buffer.data[i] + buffer.data[i + 1] + buffer.data[i + 2] > 12) { lit = true; break; }
  return lit ? new ImageData(new Uint8ClampedArray(buffer.data), buffer.width, buffer.height) : null;
}

function frameStats(getCanvas: () => HTMLCanvasElement | null) {
  const canvas = programPictureCanvas(getCanvas());
  const buffer = canvas ? sampleCanvas(canvas, 192) : null;
  return buffer ? computeScopes(buffer, { columns: 32, levels: 64, vectorSize: 16 }).stats : null;
}

function toDataUrl(frame: ImageData, look: GalleryLook | null): string {
  const canvas = document.createElement('canvas');
  canvas.width = frame.width;
  canvas.height = frame.height;
  const ctx = canvas.getContext('2d');
  if (!ctx) return '';
  const copy = new ImageData(new Uint8ClampedArray(frame.data), frame.width, frame.height);
  if (look) gradeRgba(copy.data, lookPreviewGrade(look, DEFAULT_STRENGTH));
  ctx.putImageData(copy, 0, 0);
  return canvas.toDataURL('image/png');
}

/**
 * The look gallery's thumbnails: the current frame with each look applied at full strength. When the
 * clip is already graded the frame is taken with the grade bypassed for a moment (once per clip, and
 * on Refresh), so a look never previews on top of itself.
 */
function useLookThumbnails(props: Pick<LookPanelProps, 'getCanvas' | 'gradeBypass' | 'onGradeBypass' | 'playheadFrame' | 'onPanelBypass' | 'userBypassSeq'> & { targetId: string | null; graded: boolean }) {
  const { getCanvas, gradeBypass, onGradeBypass, playheadFrame, targetId, graded, onPanelBypass, userBypassSeq = 0 } = props;
  const [thumbs, setThumbs] = useState<Record<string, string> | null>(null);
  // The brief bypass is the panel's, not the user's: the Show original switch does not show it, and a user who flips the switch during it wins.
  const [dancing, setDancing] = useState(false);
  const userChose = useRef(false);
  const live = useRef({ gradeBypass, graded, onGradeBypass, onPanelBypass, userBypassSeq });
  useEffect(() => {
    live.current = { gradeBypass, graded, onGradeBypass, onPanelBypass, userBypassSeq };
  });
  const token = useRef(0);

  const build = useCallback((frame: ImageData) => {
    const next: Record<string, string> = { original: toDataUrl(frame, null) };
    for (const look of GALLERY_LOOKS) next[look] = toDataUrl(frame, look);
    setThumbs(next);
  }, []);

  const capture = useCallback(async (allowBypass: boolean) => {
    const mine = ++token.current;
    const { graded: isGraded, gradeBypass: wasBypassed } = live.current;
    const dance = allowBypass && isGraded && !wasBypassed;
    const seqAtStart = live.current.userBypassSeq;
    if (dance) {
      userChose.current = false;
      setDancing(true);
      live.current.onPanelBypass?.(true);
      live.current.onGradeBypass(true);
      await settle(350);
    }
    let frame: ImageData | null = null;
    for (let attempt = 0; attempt < 8 && mine === token.current; attempt++) {
      frame = readFrame(getCanvas);
      if (frame) break;
      await settle(400);
    }
    if (dance) {
      setDancing(false);
      live.current.onPanelBypass?.(false);
      // A choice made on the switch here, or on the page's compare bar, during the brief bypass stays.
      if (!userChose.current && live.current.userBypassSeq === seqAtStart) live.current.onGradeBypass(false);
    }
    if (frame && mine === token.current && (!isGraded || wasBypassed || dance)) build(frame);
  }, [getCanvas, build]);

  // A new clip (or the first mount): capture with a brief bypass when it is graded.
  useEffect(() => {
    void capture(true);
    return () => { token.current++; };
  }, [targetId, capture]);

  // The playhead moved on an ungraded clip: refresh quietly, once it settles.
  useEffect(() => {
    if (live.current.graded && !live.current.gradeBypass) return undefined;
    const timer = setTimeout(() => void capture(false), 400);
    return () => clearTimeout(timer);
  }, [playheadFrame, capture]);

  const chooseBypass = (bypass: boolean) => {
    userChose.current = true;
    setDancing(false);
    onGradeBypass(bypass);
  };
  return { thumbs, refresh: () => void capture(true), shownBypass: gradeBypass && !dancing, chooseBypass };
}

export function LookPanel(props: LookPanelProps) {
  const { doc, target, selection, readOnly, playheadFrame, newId, onWriteGrade, onApply, onSeek, gradeBypass, onGradeBypass, getCanvas, gallery, onCaptureStill, referenceId, onReference, wipe, onWipe, onMessage, onPanelBypass, userBypassSeq } = props;
  const t = useT('editor-color.look');
  const f = useFormat();
  const pct = (v: number) => f.percent(v / 100, { maximumFractionDigits: 0 });
  const fix2 = (v: number) => f.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const lookSlug = (name: string) => name.toLowerCase().replace(/[^a-z]+/g, '-');
  const coarse = useCoarsePointer();
  const grade = target?.grade ?? null;
  const look = currentLook(grade);
  const fix = fixValuesOf(grade);
  const disabled = readOnly || !target;
  const btnSize = coarse ? 'md' : 'xs';
  const touch = coarse ? { minHeight: 44 } : undefined;
  const { thumbs, refresh, shownBypass, chooseBypass } = useLookThumbnails({ getCanvas, gradeBypass, onGradeBypass, playheadFrame, targetId: target?.id ?? null, graded: isActiveGrade(grade), onPanelBypass, userBypassSeq });
  const [matchTo, setMatchTo] = useState<string | null>(null);
  const [matchStrength, setMatchStrength] = useState(100);
  const [matching, setMatching] = useState(false);
  const mediaTarget = target?.type === 'mediaClip' ? target : null;
  const others = clipsInScope(doc, 'all', [], null).filter((c) => c.id !== target?.id);
  const split = useBeforeAfterSplit({ gradeBypass, onGradeBypass, gallery, referenceId, onReference, onCaptureStill });
  const docRef = useRef(doc);
  const playheadRef = useRef(playheadFrame);
  const bypassRef = useRef(gradeBypass);
  useEffect(() => {
    docRef.current = doc;
    playheadRef.current = playheadFrame;
    bypassRef.current = gradeBypass;
  });
  const { data: mediaPool } = useMediaPool(doc.projectId);
  // The picture rect is the compositor's own fit geometry, which needs the source's probed size.
  const getSourceSize = useCallback((clip: MediaClip) => sourceSizeFromPool(mediaPool, clip), [mediaPool]);
  const fixer = useFixExposure({
    clipId: target?.id ?? null, readOnly, label: t('fixExposure'), getDoc: () => docRef.current, getPlayhead: () => playheadRef.current,
    getBypass: () => bypassRef.current, setBypass: onGradeBypass, seek: onSeek, getCanvas, getSourceSize, apply: onApply, newId,
  });
  const fixTarget = target ? resolveFixTarget(doc, target.id) : null;
  const partText: Record<FixPart, (stops: string) => string> = {
    brighter: (stops) => t('fixBrighter', { stops }), darker: (stops) => t('fixDarker', { stops }), deeper: () => t('fixDeeper'),
    lifted: () => t('fixLifted'), highlightsUp: () => t('fixHighlightsUp'), highlightsDown: () => t('fixHighlightsDown'),
  };
  const fixLine = (s: FixState): string | null => {
    if (s.phase === 'fixed') {
      const stops = f.number(Math.abs(s.summary.stops), { minimumFractionDigits: 1, maximumFractionDigits: 1 });
      const text = fixSummaryParts(s.summary).map((part, i) => { const x = partText[part](stops); return i === 0 ? x : x.charAt(0).toLowerCase() + x.slice(1); }).join(', ');
      return text || t('fixDone');
    }
    if (s.phase === 'nothing') return s.reason === 'fine' ? t('fixFine') : s.reason === 'flat' ? t('fixFlat') : t('fixNoPicture');
    if (s.phase === 'tooManyNodes') return t('fixTooMany');
    if (s.phase === 'gone') return t('fixGone');
    if (s.phase === 'changed') return t('fixChanged');
    return null;
  };

  const pickLook = (name: GalleryLook | null) => {
    if (!target) return;
    onWriteGrade(name ? withLook(grade, newId, name, look?.strength ?? DEFAULT_STRENGTH) : withoutLook(grade), name ? t('lookLabel', { name: t(`looks.${lookSlug(name)}`) }) : t('removeLook'));
  };
  const setStrength = (pct: number) => look && onWriteGrade(withLook(grade, newId, look.look, pct), t('strengthLabel', { name: t(`looks.${lookSlug(look.look)}`), percent: Math.round(pct) }));
  const setFix = (key: FixKey, value: number) => onWriteGrade(withFix(grade, newId, { ...fix, [key]: value }), t(`fixes.${key}.label`));

  const hasLookOrFix = look !== null || FIX_KEYS.some((k) => fix[k] !== fixValuesOf(null)[k]);
  const applyTo = (scope: ApplyScope) => {
    if (!target) return;
    const clips = clipsInScope(doc, scope, selection, mediaTarget);
    if (clips.length === 0) return;
    const ops = applyGradeToClips(clips, (c) => {
      const withL = look ? withLook(c.grade, newId, look.look, look.strength) : withoutLook(c.grade);
      return withFix(withL, newId, fix);
    });
    const label = scope === 'all' ? t('applyAllLabel') : scope === 'selected' ? t('applySelectedLabel') : t('applyFileLabel');
    onApply(ops, label);
    onMessage(t('applied', { count: clips.length }));
  };
  const selectedMedia = clipsInScope(doc, 'selected', selection, null);
  const sameFile = clipsInScope(doc, 'sameFile', selection, mediaTarget);

  const copyGrade = () => {
    if (!grade) return;
    gradeClipboard.set(structuredClone(grade));
    onMessage(t('copied'));
  };
  const pasteGrade = () => {
    const copied = gradeClipboard.get();
    if (!copied) return;
    const clips = selectedMedia;
    if (clips.length === 0) { onMessage(t('selectToPaste')); return; }
    onApply(applyGradeToClips(clips, () => cloneStack(copied, newId)), t('pasteLabel', { count: clips.length }));
    onMessage(t('pasted', { count: clips.length }));
  };

  const matchClip = async () => {
    const reference = others.find((c) => c.id === matchTo);
    if (!mediaTarget || !reference) return;
    setMatching(true);
    const priorFrame = playheadFrame;
    const priorBypass = gradeBypass;
    try {
      onGradeBypass(true);
      onSeek(Math.floor((mediaTarget.recordIn + mediaTarget.recordOut) / 2));
      await settle(600);
      const current = frameStats(getCanvas);
      onGradeBypass(false);
      onSeek(Math.floor((reference.recordIn + reference.recordOut) / 2));
      await settle(600);
      const ref = frameStats(getCanvas);
      if (!current || !ref) {
        onMessage(t('noPicture'));
        return;
      }
      const primaries = scalePrimaries(shotMatchPrimaries(current, ref), matchStrength / 100);
      onWriteGrade(withMatch(mediaTarget.grade, newId, primaries), t('matchLabel', { name: reference.name }));
      onMessage(t('matched', { name: reference.name, percent: matchStrength }));
    } finally {
      onGradeBypass(priorBypass);
      onSeek(priorFrame);
      setMatching(false);
    }
  };

  return (
    <Stack gap="sm" data-testid="look-panel">
      {!target && <Text size="xs" c="dimmed" data-testid="look-no-target">{t('noTarget')}</Text>}

      <Group justify="space-between" align="center">
        <Text size="xs" fw={600}>{t('looks.title')}</Text>
        <Tooltip label={t('refreshTip')}>
          <EditorIcon tooltip={false} icon={IconRefresh} size="sm" onClick={refresh} label={t('refresh')} data-testid="look-refresh" />
        </Tooltip>
      </Group>
      <SimpleGrid cols={{ base: 3, sm: 4 }} spacing={6} data-testid="look-gallery">
        <LookTile name={t('original')} ariaLabel={t('tileLabel', { name: t('original') })} active={look === null} src={thumbs?.original ?? null} disabled={disabled} onPick={() => pickLook(null)} testId="look-none" />
        {GALLERY_LOOKS.map((name) => (
          <LookTile key={name} name={t(`looks.${lookSlug(name)}`)} ariaLabel={t('tileLabel', { name: t(`looks.${lookSlug(name)}`) })} active={look?.look === name} src={thumbs?.[name] ?? null} disabled={disabled} onPick={() => pickLook(name)} testId={`look-${name.toLowerCase().replace(/[^a-z]+/g, '-')}`} />
        ))}
      </SimpleGrid>

      <Group gap={8} wrap="nowrap" mih={coarse ? 44 : undefined}>
        <Text size="xs" w={64}>{t('strength')}</Text>
        <LiveSlider size={coarse ? 'lg' : 'sm'} style={{ flex: 1 }} min={0} max={MAX_STRENGTH} step={5} value={look?.strength ?? DEFAULT_STRENGTH} disabled={disabled || !look} onChangeEnd={setStrength} label={pct} aria-label={t('lookStrength')} data-testid="look-strength" />
        <Text size="xs" w={40} ta="right" data-testid="look-strength-value">{look ? pct(look.strength) : '-'}</Text>
      </Group>
      {!look && <Text size="xs" c="dimmed">{t('pickLook')}</Text>}

      <Text size="xs" fw={600}>{t('fix')}</Text>
      {FIX_KEYS.map((key) => {
        const spec = FIX_SPEC[key];
        return (
          <Group key={key} gap={8} wrap="nowrap" mih={coarse ? 44 : undefined}>
            <Text size="xs" w={76} title={t(`fixes.${key}.hint`)}>{t(`fixes.${key}.label`)}</Text>
            <LiveSlider size={coarse ? 'lg' : 'sm'} style={{ flex: 1 }} min={spec.min} max={spec.max} step={spec.step} value={fix[key]} disabled={disabled} onChangeEnd={(v) => setFix(key, v)} label={fix2} thumbLabel={t(`fixes.${key}.label`)} data-testid={`fix-${key}`} />
            <Text size="xs" w={40} ta="right">{fix2(fix[key])}</Text>
          </Group>
        );
      })}

      <Stack gap={4} data-testid="fix-exposure">
        {fixer.state.phase === 'measuring' ? (
          <Group gap={8} wrap="nowrap">
            <Text size="xs" data-testid="fix-exposure-progress">{t('fixMeasuring', { done: fixer.state.job.progress.done, total: fixer.state.job.progress.total })}</Text>
            <Button size={btnSize} fz="xs" variant="default" style={touch} onClick={fixer.cancel} data-testid="fix-exposure-cancel">{t('fixCancel')}</Button>
          </Group>
        ) : (
          <Button size={btnSize} fz="xs" variant="light" style={touch} disabled={disabled} onClick={fixer.start} data-testid="fix-exposure-run">
            {hasAutoFix(fixTarget) ? t('fixExposureAgain') : t('fixExposure')}
          </Button>
        )}
        {fixTarget?.group && fixer.state.phase !== 'measuring' && <Text size="xs" c="dimmed" data-testid="fix-exposure-group">{t('fixGroupNote')}</Text>}
        {fixLine(fixer.state) && <Text size="xs" role="status" data-testid="fix-exposure-result">{fixLine(fixer.state)}</Text>}
        {fixer.state.phase === 'changed' && (
          <Group gap={6}>
            <Button size={btnSize} fz="xs" style={touch} onClick={fixer.applyAnyway} data-testid="fix-exposure-apply">{t('fixApplyAnyway')}</Button>
            <Button size={btnSize} fz="xs" variant="light" style={touch} onClick={fixer.measureAgain} data-testid="fix-exposure-again">{t('fixMeasureAgain')}</Button>
          </Group>
        )}
      </Stack>

      <Text size="xs" fw={600}>{t('compare')}</Text>
      <Group gap={8} align="center">
        <Switch size={coarse ? 'lg' : 'sm'} label={t('showOriginal')} checked={shownBypass} onChange={(e) => chooseBypass(e.currentTarget.checked)} data-testid="color-bypass" styles={{ label: { fontSize: 12 } }} />
        {split.active ? (
          <Button size={btnSize} fz="xs" variant="light" style={touch} onClick={split.stop} data-testid="look-split-off">{t('closeSplit')}</Button>
        ) : (
          <Button size={btnSize} fz="xs" variant="light" style={touch} loading={split.busy} disabled={disabled} onClick={() => void split.start()} data-testid="look-split">{t('split')}</Button>
        )}
      </Group>
      {split.active && (
        <Group gap={8} wrap="nowrap" mih={coarse ? 44 : undefined}>
          <Text size="xs" w={64}>{t('splitAt')}</Text>
          <LiveSlider size={coarse ? 'lg' : 'sm'} style={{ flex: 1 }} min={0} max={1} step={0.01} value={wipe} onChangeEnd={onWipe} onChange={onWipe} label={(v) => pct(v * 100)} aria-label={t('splitPosition')} data-testid="look-split-position" />
        </Group>
      )}

      <Text size="xs" fw={600}>{t('useOnMore')}</Text>
      <Group gap={6}>
        <Button size={btnSize} fz="xs" style={touch} disabled={readOnly || !target || !hasLookOrFix} onClick={() => applyTo('all')} data-testid="look-apply-all">{t('applyAll')}</Button>
        {selectedMedia.length > 1 && (
          <Button size={btnSize} fz="xs" variant="light" style={touch} disabled={readOnly || !hasLookOrFix} onClick={() => applyTo('selected')} data-testid="look-apply-selected">{t('applySelected', { count: selectedMedia.length })}</Button>
        )}
        {mediaTarget && sameFile.length > 1 && (
          <Button size={btnSize} fz="xs" variant="light" style={touch} disabled={readOnly || !hasLookOrFix} onClick={() => applyTo('sameFile')} data-testid="look-apply-file">{t('applyFile', { count: sameFile.length })}</Button>
        )}
      </Group>
      <Group gap={6}>
        <Button size={btnSize} fz="xs" variant="subtle" style={touch} disabled={!grade} onClick={copyGrade} data-testid="look-copy">{t('copyGrade')}</Button>
        <Button size={btnSize} fz="xs" variant="subtle" style={touch} disabled={readOnly || gradeClipboard.get() === null || selectedMedia.length === 0} onClick={pasteGrade} data-testid="look-paste">{t('pasteSelected')}</Button>
      </Group>

      {mediaTarget && (
        <Stack gap={6} data-testid="look-match">
          <Text size="xs" fw={600}>{t('matchTitle')}</Text>
          <Group gap={6} wrap="wrap">
            {others.length === 0 && <Text size="xs" c="dimmed">{t('noOther')}</Text>}
            {others.map((c) => (
              <Button key={c.id} size={btnSize} fz="xs" style={touch} variant={matchTo === c.id ? 'filled' : 'light'} onClick={() => setMatchTo(c.id)} data-testid={`match-pick-${c.id}`}>{c.name}</Button>
            ))}
          </Group>
          <Group gap={8} wrap="nowrap" mih={coarse ? 44 : undefined}>
            <Text size="xs" w={64}>{t('amount')}</Text>
            <LiveSlider size={coarse ? 'lg' : 'sm'} style={{ flex: 1 }} min={0} max={100} step={5} value={matchStrength} disabled={readOnly} onChangeEnd={setMatchStrength} label={pct} aria-label={t('matchAmount')} data-testid="match-strength" />
            <Text size="xs" w={40} ta="right">{pct(matchStrength)}</Text>
          </Group>
          <Button size={btnSize} fz="xs" style={touch} loading={matching} disabled={readOnly || !matchTo} onClick={() => void matchClip()} data-testid="match-run">{t('match')}</Button>
        </Stack>
      )}
    </Stack>
  );
}

function LookTile({ name, ariaLabel, src, active, disabled, onPick, testId }: { name: string; ariaLabel: string; src: string | null; active: boolean; disabled: boolean; onPick: () => void; testId: string }) {
  return (
    <UnstyledButton
      onClick={onPick}
      disabled={disabled}
      aria-pressed={active}
      aria-label={ariaLabel}
      data-testid={testId}
      style={{ border: `2px solid ${active ? 'var(--mantine-color-blue-5)' : 'var(--mantine-color-default-border)'}`, borderRadius: 6, overflow: 'hidden', minHeight: 44, opacity: disabled ? 0.5 : 1 }}
    >
      <div style={{ aspectRatio: '16 / 9', background: '#111' }}>
        {src && <Image src={src} alt="" w="100%" h="100%" fit="cover" />}
      </div>
      <Text size="xs" ta="center" py={2}>{name}</Text>
    </UnstyledButton>
  );
}
