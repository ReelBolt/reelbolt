'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Button, Text, TextInput } from '@mantine/core';
import { FIX_KEYS, FIX_SPEC, GALLERY_LOOKS, MAX_STRENGTH, type GalleryLook } from '@/lib/editing/color/look-gallery';
import { TITLE_TEMPLATE_CHOICES } from '@/lib/editing/structure/graphics';
import { hasRemovedCharacters, REMOVED_CHARACTERS_NOTE } from '@/lib/editing/title-text';
import type { TitleTemplate } from '@/lib/editing/types';
import type { PhoneActions } from './phone-actions';
import { CameraProfileRow } from '../color/CameraProfileControl';
import { Refusal, SliderField } from './phone-controls';
import classes from './phone.module.css';
import { useFormat, useT } from '@/i18n';
import { fixSummaryParts, hasAutoFix, resolveFixTarget, sourceSizeFromPool, useFixExposure, type FixPart, type FixState } from '@/lib/editing/color/exposure-measure';
import { useMediaPool } from '@/lib/hooks/use-media';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';

const TEMPLATES = TITLE_TEMPLATE_CHOICES.map((t) => ({ id: t.id, label: t.label.replace(' (centred)', '') }));

function Heading({ children }: { children: React.ReactNode }) {
  return (
    <Text size="sm" fw={600} component="h3" m={0}>
      {children}
    </Text>
  );
}

/** Compare: hold the button to see the pictures without their grades, or switch it on and off. */
function Compare({ actions }: { actions: PhoneActions }) {
  const tx = useT('editor.phone.lookSheet');
  const a = actions.look;
  const held = useRef(false);
  // The preview never stays on "original" after the sheet closes.
  const setBypass = a.setBypass;
  const latest = useRef(setBypass);
  useEffect(() => {
    latest.current = setBypass;
  });
  useEffect(() => () => latest.current(false), []);
  const release = () => {
    if (!held.current) return;
    held.current = false;
    setBypass(false);
  };
  return (
    <div className={classes.sheetRow} role="group" aria-label={tx('compare')}>
      <Button variant={a.bypass ? 'filled' : 'default'} h={44} aria-pressed={a.bypass} onClick={() => setBypass(!a.bypass)} data-testid="look-compare-toggle">
        {a.bypass ? tx('showingTheOriginal') : tx('showTheOriginal')}
      </Button>
      <Button
        variant="default"
        h={44}
        onPointerDown={() => {
          held.current = true;
          setBypass(true);
        }}
        onPointerUp={release}
        onPointerCancel={release}
        onPointerLeave={release}
        data-testid="look-compare-hold"
      >
        {tx('holdToCompare')}
      </Button>
    </div>
  );
}

function TemplateChips({ value, onPick, disabled, prefix }: { value: TitleTemplate; onPick: (t: TitleTemplate) => void; disabled?: boolean; prefix: string }) {
  const tx = useT('editor.phone.lookSheet');
  return (
    <div className={classes.sheetRow} role="group" aria-label={tx('titleStyle')}>
      {TEMPLATES.map((t) => (
        <Button key={t.id} variant={value === t.id ? 'filled' : 'default'} h={44} aria-pressed={value === t.id} disabled={disabled} onClick={() => onPick(t.id)} data-testid={`${prefix}-${t.id}`}>
          {t.label}
        </Button>
      ))}
    </div>
  );
}

export function TitlesSection({ actions }: { actions: PhoneActions }) {
  const tx = useT('editor.phone.lookSheet');
  const a = actions.look;
  const [text, setText] = useState('');
  const [template, setTemplate] = useState<TitleTemplate>('plain');
  const [problem, setProblem] = useState<string | null>(null);
  const [editProblem, setEditProblem] = useState<string | null>(null);
  const [editText, setEditText] = useState(a.title?.text ?? '');
  const titleId = a.title?.id ?? null;
  const titleText = a.title?.text ?? '';
  const [seenTitle, setSeenTitle] = useState(`${titleId}|${titleText}`);
  if (seenTitle !== `${titleId}|${titleText}`) {
    setSeenTitle(`${titleId}|${titleText}`);
    setEditText(titleText);
  }
  const ro = actions.readOnly;
  return (
    <>
      <Heading>{tx('titles')}</Heading>
      {a.title && (
        <div className={classes.sheetStack} style={{ padding: 0 }} data-testid="look-title-edit">
          <TextInput
            label={tx('wordsOnTheSelected')}
            size="md"
            styles={{ input: { height: 44 } }}
            value={editText}
            disabled={ro}
            onChange={(e) => setEditText(e.currentTarget.value)}
            onKeyDown={(e) => {
              e.stopPropagation();
              if (e.key === 'Enter') e.currentTarget.blur();
            }}
            onBlur={() => {
              if (editText.trim() !== titleText) setEditProblem(a.setTitleText(editText));
              else setEditProblem(null);
            }}
            data-testid="look-title-edit-text"
          />
          {hasRemovedCharacters(editText) && <p className={classes.sheetHint}>{REMOVED_CHARACTERS_NOTE}</p>}
          <TemplateChips value={a.title.template ?? 'plain'} disabled={ro} onPick={(t) => setEditProblem(a.setTitleTemplate(t))} prefix="look-title-edit-style" />
          <Refusal text={editProblem} testId="look-title-edit-problem" />
        </div>
      )}
      <TextInput
        label={tx('addATitleAt')}
        placeholder={tx('typeTheWords')}
        size="md"
        styles={{ input: { height: 44 } }}
        value={text}
        disabled={ro}
        onChange={(e) => setText(e.currentTarget.value)}
        onKeyDown={(e) => e.stopPropagation()}
        data-testid="look-title-text"
      />
      {hasRemovedCharacters(text) && <p className={classes.sheetHint}>{REMOVED_CHARACTERS_NOTE}</p>}
      <TemplateChips value={template} disabled={ro} onPick={setTemplate} prefix="look-title-style" />
      <Button
        h={44}
        disabled={ro}
        onClick={() => {
          const p = a.addTitle(text, template);
          setProblem(p);
          if (!p) setText('');
        }}
        data-testid="look-title-add"
      >
        {tx('addTitle')}
      </Button>
      <Refusal text={problem} testId="look-title-problem" />
    </>
  );
}

/**
 * RM.10 / R10.6: the dock's "Look and fix" sheet. A look in words, how strong it is, five plain sliders,
 * Compare, Apply to all clips, and titles. Every control is 44 px, every slider has a number box, every change
 * is one undo step and a refusal is one plain sentence. Wheels, curves, keyer, pin, qualifier, masks and reframe
 * stay on desktop: a clip that already has such colour work keeps it and says so.
 */
/** What the Fix exposure button needs from the editor to read the viewer (the sheet has no access to it otherwise). */
export interface PhoneFixEnv {
  getDoc: () => EditTimeline;
  getPlayhead: () => number;
  seek: (frame: number) => void;
  getCanvas: () => HTMLCanvasElement | null;
  apply: (ops: readonly EditOp[], label: string) => void;
  newId: () => string;
}

function FixExposure({ actions, env }: { actions: PhoneActions; env: PhoneFixEnv | null }) {
  const tx = useT('editor.phone.lookSheet');
  const f = useFormat();
  const a = actions.look;
  const clipId = a.state?.target.id ?? null;
  const fallback: PhoneFixEnv = { getDoc: () => ({}) as EditTimeline, getPlayhead: () => 0, seek: () => undefined, getCanvas: () => null, apply: () => undefined, newId: () => '' };
  const e = env ?? fallback;
  const { data: mediaPool } = useMediaPool(env ? e.getDoc().projectId : null);
  const getSourceSize = (clip: MediaClip) => sourceSizeFromPool(mediaPool, clip);
  const fixer = useFixExposure({
    clipId: env ? clipId : null, readOnly: actions.readOnly, label: tx('fixExposure'), getDoc: e.getDoc, getPlayhead: e.getPlayhead,
    getBypass: () => a.bypass, setBypass: a.setBypass, seek: e.seek, getCanvas: e.getCanvas, getSourceSize, apply: e.apply, newId: e.newId,
  });
  const target = env && clipId ? resolveFixTarget(env.getDoc(), clipId) : null;
  const partText: Record<FixPart, (stops: string) => string> = {
    brighter: (stops) => tx('fixBrighter', { stops }), darker: (stops) => tx('fixDarker', { stops }), deeper: () => tx('fixDeeper'),
    lifted: () => tx('fixLifted'), highlightsUp: () => tx('fixHighlightsUp'), highlightsDown: () => tx('fixHighlightsDown'),
  };
  const line = (s: FixState): string | null => {
    if (s.phase === 'fixed') {
      const stops = f.number(Math.abs(s.summary.stops), { minimumFractionDigits: 1, maximumFractionDigits: 1 });
      const text = fixSummaryParts(s.summary).map((part, i) => { const x = partText[part](stops); return i === 0 ? x : x.charAt(0).toLowerCase() + x.slice(1); }).join(', ');
      return text || tx('fixDone');
    }
    if (s.phase === 'nothing') return s.reason === 'fine' ? tx('fixFine') : s.reason === 'flat' ? tx('fixFlat') : tx('fixNoPicture');
    if (s.phase === 'tooManyNodes') return tx('fixTooMany');
    if (s.phase === 'gone') return tx('fixGone');
    if (s.phase === 'changed') return tx('fixChanged');
    return null;
  };
  const text = line(fixer.state);
  return (
    <>
      {fixer.state.phase === 'measuring' ? (
        <>
          <p className={classes.sheetHint} data-testid="fix-exposure-progress">{tx('fixMeasuring', { done: fixer.state.job.progress.done, total: fixer.state.job.progress.total })}</p>
          <Button variant="default" h={44} onClick={fixer.cancel} data-testid="fix-exposure-cancel">{tx('fixCancel')}</Button>
        </>
      ) : (
        <Button variant="light" h={44} disabled={actions.readOnly || !a.state || !env} onClick={fixer.start} data-testid="fix-exposure-run">
          {hasAutoFix(target) ? tx('fixExposureAgain') : tx('fixExposure')}
        </Button>
      )}
      {target?.group && fixer.state.phase !== 'measuring' && <p className={classes.sheetHint} data-testid="fix-exposure-group">{tx('fixGroupNote')}</p>}
      {text && <p className={classes.sheetHint} role="status" data-testid="fix-exposure-result">{text}</p>}
      {fixer.state.phase === 'changed' && (
        <div className={classes.sheetRow}>
          <Button h={44} onClick={fixer.applyAnyway} data-testid="fix-exposure-apply">{tx('fixApplyAnyway')}</Button>
          <Button variant="light" h={44} onClick={fixer.measureAgain} data-testid="fix-exposure-again">{tx('fixMeasureAgain')}</Button>
        </div>
      )}
    </>
  );
}

/** `panel`: the Simple shell hosts the same body in its side panel, where titles have their own task and the full editor is one click away (so no "on desktop" note). */
export function LookSheetBody({ actions, fix = null, panel = false }: { actions: PhoneActions; fix?: PhoneFixEnv | null; panel?: boolean }) {
  const tx = useT('editor.phone.lookSheet');
  const a = actions.look;
  const state = a.state;
  const ro = actions.readOnly;
  const [problem, setProblem] = useState<string | null>(null);
  const pick = (look: GalleryLook | null) => setProblem(a.change({ kind: 'look', look }));
  const fixed = state ? FIX_KEYS.some((k) => state.fix[k] !== { brightness: 0, contrast: 1, saturation: 1, warmth: 0, tint: 0 }[k]) : false;
  return (
    <div className={classes.sheetStack} data-testid="look-sheet">
      {!state ? (
        <p className={classes.sheetHint} data-testid="look-no-clip">
          {tx('addAVideoClip')}
        </p>
      ) : (
        <Text size="sm" data-testid="look-target">
          {tx('changing')} <b>{state.target.name || tx('thisClip')}</b>
        </Text>
      )}

      {state && !a.hdr && <CameraProfileRow clip={state.target} readOnly={ro} touch onSet={(p) => setProblem(a.setProfile(p))} testIdPrefix="look-camera-profile" />}

      <Heading>{tx('look')}</Heading>
      <div className={classes.sheetRow} role="group" aria-label={tx('look2')}>
        <Button variant={state && !state.look ? 'filled' : 'default'} h={44} miw={64} aria-pressed={!!state && !state.look} disabled={ro || !state} onClick={() => pick(null)} data-testid="look-none">
          {tx('original')}
        </Button>
        {GALLERY_LOOKS.map((name) => (
          <Button key={name} variant={state?.look?.look === name ? 'filled' : 'default'} h={44} miw={64} aria-pressed={state?.look?.look === name} disabled={ro || !state} onClick={() => pick(name)} data-testid={`look-${name.toLowerCase().replace(/[^a-z]+/g, '-')}`}>
            {name}
          </Button>
        ))}
      </div>
      <SliderField label={tx('howStrong')} value={state?.look?.strength ?? 100} min={0} max={MAX_STRENGTH} step={5} unit="%" disabled={ro || !state?.look} onCommit={(v) => a.change({ kind: 'strength', percent: v })} testId="look-strength" />
      {state && !state.look && <p className={classes.sheetHint}>{tx('pickALookThen')}</p>}

      <Heading>{tx('fix')}</Heading>
      {FIX_KEYS.map((key) => {
        const spec = FIX_SPEC[key];
        return <SliderField key={key} label={spec.label} value={state?.fix[key] ?? 0} min={spec.min} max={spec.max} step={spec.step} decimals={2} disabled={ro || !state} onCommit={(v) => a.change({ kind: 'fix', key, value: v })} testId={`fix-${key}`} />;
      })}
      <FixExposure actions={actions} env={fix} />
      <Button variant="default" h={44} disabled={ro || !state || !fixed} onClick={() => setProblem(a.change({ kind: 'resetFix' }))} data-testid="fix-reset">
        {tx('resetTheFiveSettings')}
      </Button>
      {state && state.extraAdjustments > 0 && (
        <p className={classes.sheetHint} data-testid="look-extra">
          {tx('extraAdjustments', { count: state.extraAdjustments })}
        </p>
      )}

      <Heading>{tx('compare2')}</Heading>
      <Compare actions={actions} />

      <Heading>{tx('allClips')}</Heading>
      <Button h={44} disabled={ro || !state} onClick={() => setProblem(a.applyToAll())} data-testid="look-apply-all">
        {state && state.clipCount > 1 ? tx('applyToAllClips', { clipCount: state.clipCount }) : tx('applyToAllClips2')}
      </Button>
      <Refusal text={problem} testId="look-problem" />

      {!panel && <TitlesSection actions={actions} />}

      {!panel && (
      <p className={classes.sheetHint} data-testid="look-desktop-note">
        {tx('colourWheelsCurvesKeying')}
      </p>
      )}
    </div>
  );
}
