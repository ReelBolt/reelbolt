'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Button, Select, Text } from '@mantine/core';
import { IconRotateClockwise } from '@tabler/icons-react';
import { framesToSeconds, SPEED_MIN } from '@/lib/editing/clip-depth';
import { BLEND_MODES } from '@/lib/editing/engine/ops';
import type { BlendMode } from '@/lib/editing/types';
import type { PhoneActions } from './phone-actions';
import { ChipRow, Refusal, SliderField } from './phone-controls';
import classes from './phone.module.css';
import { useT } from '@/i18n';
import { useHelperText } from './helper-text';
import { ObjectEffectsSection } from '../panels/ObjectEffectsSection';
import { EnhanceSection } from '../enhance/EnhanceSection';
import { useEnhanceHost } from '../enhance/enhance-host';
import { enhanceSheetState } from './clip-sheet-actions';
import { FollowSection } from '../panels/FollowSection';

export type ClipSheetSection = 'picture' | 'speed' | 'volume' | 'objects';

const SPEED_CHIPS_PHONE = [0.5, 1, 1.5, 2] as const;
const PAD_NAMES = ['top left', 'top', 'top right', 'left', 'centre', 'right', 'bottom left', 'bottom', 'bottom right'];
const ANIMATED_NOTE = 'Animated on desktop. Change it there.';

function Heading({ children }: { children: React.ReactNode }) {
  return (
    <Text size="sm" fw={600} component="h3" m={0}>
      {children}
    </Text>
  );
}

/**
 * The volume control alone: readout, slider, Mute and Back to 100%, or "Extract audio" when the clip has no sound
 * of its own yet. Used by the clip sheet and by the Audio sheet (which needs a volume slider, not the whole sheet).
 */
export function ClipVolumeSection({ actions, sectionRef, onProblem }: { actions: PhoneActions; sectionRef?: React.Ref<HTMLDivElement>; onProblem?: (problem: string | null) => void }) {
  const t = useT('editor.phone.clipSheet');
  const a = actions;
  const clip = a.clip;
  const ro = a.readOnly;
  if (!clip || !clip.isMedia) return null;
  const percent = clip.volumePercent;
  const set = (p: string | null) => {
    onProblem?.(p);
    return p;
  };
  return (
    <div ref={sectionRef} className={classes.sheetStack} style={{ padding: 0 }} data-testid="volume-sheet">
      <Heading>{t('volume')}</Heading>
      {percent === null ? (
        <>
          <p className={classes.sheetHint}>{t('thisClipHasNo')}</p>
          {clip.onPictureTrack && (
            <Button variant="default" h={44} disabled={ro} onClick={a.extractAudio} data-testid="phone-volume-extract">
              {t('extractAudio')}
            </Button>
          )}
        </>
      ) : (
        <>
          <Text size="sm" data-testid="phone-volume-readout">
            {t('volume2', { percent })}
          </Text>
          <SliderField label={t('volume3')} value={percent} min={0} max={200} step={1} unit="%" disabled={ro} onCommit={(v) => set(a.setVolume(v))} testId="phone-volume" />
          <div className={classes.sheetRow}>
            <Button variant="default" h={44} disabled={ro} onClick={() => set(a.setVolume(0))} data-testid="phone-volume-mute">
              {t('mute')}
            </Button>
            <Button variant="default" h={44} disabled={ro || percent === 100} onClick={() => set(a.setVolume(100))} data-testid="phone-volume-reset">
              {t('backTo100')}
            </Button>
          </div>
        </>
      )}
    </div>
  );
}

/**
 * R07.7: the phone clip sheet, the ONE place for a clip's Fit/Fill, position, size, opacity, speed, volume and
 * fades. Each control applies the same ops the desktop inspector does (one undo step per change). Animated
 * (keyframed) values are shown, not edited ("animated on desktop"); the free rotation, crop, reverse, transitions and
 * generators are not offered here (the picture's quarter-turn orientation is), and an existing value is told in words.
 */
export function ClipSheetBody({ actions, focus, animatedNote = ANIMATED_NOTE }: { actions: PhoneActions; focus?: ClipSheetSection; /** What an animated value says where it can be changed (the Simple shell points at the full editor). */ animatedNote?: string }) {
  const t = useT('editor.phone.clipSheet');
  const tEn = useT('editor-enhance');
  const show = useHelperText();
  const a = actions;
  const sheet = a.clipSheet;
  const info = sheet.info;
  const clip = a.clip;
  const ro = a.readOnly;
  const enhanceHost = useEnhanceHost();
  const [problem, setProblem] = useState<string | null>(null);
  const speedRef = useRef<HTMLDivElement>(null);
  const volumeRef = useRef<HTMLDivElement>(null);
  const objectsRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = focus === 'speed' ? speedRef.current : focus === 'volume' ? volumeRef.current : focus === 'objects' ? objectsRef.current : null;
    el?.scrollIntoView?.({ block: 'start' });
  }, [focus]);

  if (!clip) {
    return (
      <div className={classes.sheetStack} data-testid="clip-sheet">
        <p className={classes.sheetHint}>{t('selectAClipOn')}</p>
      </div>
    );
  }
  const durationSeconds = info ? Math.max(0.1, framesToSeconds(info.durationFrames, a.rate)) : 1;
  const set = (p: string | null) => {
    setProblem(p);
    return p;
  };

  const blendLabels: Record<BlendMode, string> = {
    normal: t('blendNormal'),
    multiply: t('blendMultiply'),
    screen: t('blendScreen'),
    overlay: t('blendOverlay'),
    softLight: t('blendSoftLight'),
    hardLight: t('blendHardLight'),
    darken: t('blendDarken'),
    lighten: t('blendLighten'),
    add: t('blendAdd'),
    difference: t('blendDifference'),
  };

  return (
    <div className={classes.sheetStack} data-testid="clip-sheet">
      <Text size="sm" fw={600} data-testid="clip-sheet-name">
        {clip.name}
      </Text>

      {info && info.onPictureTrack && (
        <>
          <Heading>{t('picture')}</Heading>
          <div className={classes.sheetRow} role="group" aria-label={t('fitOrFill')}>
            <Button variant="default" h={44} miw={88} disabled={ro} onClick={() => set(sheet.fitFill('fit'))} data-testid="clip-fit">
              {t('fit')}
            </Button>
            <Button variant="default" h={44} miw={88} disabled={ro} onClick={() => set(sheet.fitFill('fill'))} data-testid="clip-fill">
              {t('fill')}
            </Button>
          </div>
          <p className={classes.sheetHint}>{t('fitShowsTheWhole')}</p>
          <div className={classes.sheetRow}>
            <Button variant="default" h={44} miw={88} disabled={ro} leftSection={<IconRotateClockwise size={18} aria-hidden />} aria-label={t('rotate90')} title={t('rotate90Tip')} onClick={() => set(sheet.rotate())} data-testid="clip-rotate">
              {t('rotate90')}
            </Button>
            <Text size="sm" c="dimmed" data-testid="clip-rotate-readout">{info.clip.sourceRotation ? `${info.clip.sourceRotation}°` : t('upright')}</Text>
          </div>
          <div role="group" aria-label={t('placeInTheFrame')} style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 48px)', gap: 6 }} data-testid="clip-pad">
            {([-1, 0, 1] as const).flatMap((row) =>
              ([-1, 0, 1] as const).map((col) => (
                <Button key={`${col}:${row}`} variant="default" h={48} p={0} disabled={ro} aria-label={`Place ${PAD_NAMES[(row + 1) * 3 + col + 1]}`} onClick={() => set(sheet.pad(col, row))} data-testid={`clip-pad-${col}-${row}`}>
                  <span aria-hidden style={{ width: 10, height: 10, borderRadius: 2, background: 'currentColor', opacity: 0.7 }} />
                </Button>
              )),
            )}
          </div>
          <SliderField label={t('acrossTheFrame')} value={Math.round(info.values.positionX * 100)} min={-100} max={100} step={1} unit="%" disabled={ro} readOnlyNote={info.animated.positionX ? animatedNote : undefined} onCommit={(v) => set(sheet.setChannel('positionX', v))} testId="clip-x" />
          <SliderField label={t('upAndDown')} value={Math.round(info.values.positionY * 100)} min={-100} max={100} step={1} unit="%" disabled={ro} readOnlyNote={info.animated.positionY ? animatedNote : undefined} onCommit={(v) => set(sheet.setChannel('positionY', v))} testId="clip-y" />
          <SliderField label={t('size')} value={Math.round(info.values.scale * 100)} min={10} max={300} step={1} unit="%" disabled={ro} readOnlyNote={info.animated.scale ? animatedNote : undefined} onCommit={(v) => set(sheet.setChannel('scale', v))} testId="clip-scale" />
          <SliderField label={t('opacity')} value={info.opacityPercent} min={0} max={100} step={1} unit="%" disabled={ro} readOnlyNote={info.animated.opacity ? animatedNote : undefined} onCommit={(v) => set(sheet.setOpacity(v))} testId="clip-opacity" />
          {sheet.setBlendMode && (
            <Select
              label={t('blend')}
              data={(BLEND_MODES as readonly BlendMode[]).map((word) => ({ value: word, label: blendLabels[word] }))}
              value={clip.blendMode ?? 'normal'}
              allowDeselect={false}
              disabled={ro}
              onChange={(v) => v && set(sheet.setBlendMode!(v as BlendMode))}
              data-testid="clip-blend-mode"
            />
          )}
        </>
      )}

      {clip.isMedia && (
        <div ref={speedRef} className={classes.sheetStack} style={{ padding: 0 }} data-testid="speed-sheet">
          <Heading>{t('speed')}</Heading>
          <ChipRow label={t('speed2')} options={SPEED_CHIPS_PHONE.map((s) => ({ id: s, label: t('x', { s }) }))} value={clip.speed} disabled={ro} onPick={(s) => set(a.setSpeed(s))} testIdPrefix="phone-speed" />
          <SliderField label={t('anySpeed')} value={clip.speed} min={SPEED_MIN} max={4} step={0.05} unit="x" decimals={2} disabled={ro} onCommit={(v) => set(a.setSpeed(v))} testId="phone-speed-custom" />
          <p className={classes.sheetHint}>{t('aFasterClipGets')}</p>
        </div>
      )}
      {!clip.isMedia && <p className={classes.sheetHint}>{t('onlyVideoAndSound')}</p>}

      {clip.isMedia && <ClipVolumeSection actions={a} sectionRef={volumeRef} onProblem={setProblem} />}

      {info && clip.isMedia && (
        <>
          <Heading>{t('fades')}</Heading>
          <SliderField label={t('fadeIn')} value={framesToSeconds(info.fadeInFrames, a.rate)} min={0} max={Math.round(durationSeconds * 10) / 10} step={0.1} unit=" s" decimals={1} disabled={ro} onCommit={(v) => set(sheet.setFade('in', v))} testId="clip-fade-in" />
          <SliderField label={t('fadeOut')} value={framesToSeconds(info.fadeOutFrames, a.rate)} min={0} max={Math.round(durationSeconds * 10) / 10} step={0.1} unit=" s" decimals={1} disabled={ro} onCommit={(v) => set(sheet.setFade('out', v))} testId="clip-fade-out" />
          <p className={classes.sheetHint}>{t('pictureAndSoundFade')}</p>
        </>
      )}

      {info && clip.isMedia && (
        <div ref={objectsRef} className={classes.sheetStack} style={{ padding: 0 }} data-testid="objects-sheet">
          <Heading>{t('objects')}</Heading>
          {info.onPictureTrack ? (
            <ObjectEffectsSection key={info.clip.id} clip={info.clip} projectId={a.clipSheet.objects?.projectId ?? null} readOnly={ro || !a.clipSheet.objects} onSetProperty={(_id, key, value, label) => set(a.clipSheet.objects?.setProperty(key, value, label) ?? null)} />
          ) : (
            <p className={classes.sheetHint} data-testid="objects-needs-picture">{t('objectsNeedPicture')}</p>
          )}
        </div>
      )}

      {info && clip.isMedia && enhanceHost && (
        <div className={classes.sheetStack} style={{ padding: 0 }} data-testid="enhance-sheet">
          <Heading>{tEn('section.title')}</Heading>
          {enhanceSheetState(enhanceHost.getDoc(), info.clip.id) === 'ok' ? (
            <EnhanceSection key={`enhance-${info.clip.id}`} clip={info.clip} projectId={enhanceHost.projectId} readOnly={ro} doc={enhanceHost.getDoc()} onBatch={enhanceHost.dispatchBatch} phone hideTitle />
          ) : (
            <p className={classes.sheetHint} data-testid="enhance-needs-picture">{tEn('section.needsPicture')}</p>
          )}
        </div>
      )}

      {info && clip.isMedia && info.onPictureTrack && (
        <div className={classes.sheetStack} style={{ padding: 0 }} data-testid="follow-sheet">
          <FollowSection clip={info.clip} projectId={a.clipSheet.objects?.projectId ?? null} readOnly={ro || !a.clipSheet.objects} allowDraw heading="heading" />
        </div>
      )}

      {info && info.readOnlyNotes.length > 0 && (
        <div data-testid="clip-readonly-notes">
          {info.readOnlyNotes.map((n) => (
            <p key={n} className={classes.sheetHint}>
              {show(n)}
            </p>
          ))}
        </div>
      )}
      <Refusal text={problem} testId="phone-sheet-problem" />
    </div>
  );
}
