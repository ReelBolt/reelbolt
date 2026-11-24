'use client';

import { useId, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { Button, Menu, VisuallyHidden } from '@mantine/core';
import { IconCheck, IconChevronDown, IconPlus } from '@tabler/icons-react';
import type { EditorStore } from '@/lib/editing/store';
import type { EditOp, TrimMode } from '@/lib/editing/engine';
import { runSelectCommand, type SelectCommand } from '@/lib/editing/selection';
import { addAtPlayhead, type PlayheadAddKind } from '@/lib/editing/structure/add-at-playhead';
import { framesToTimecode } from '@/lib/editing/time';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { buildGraphicOps, type GraphicSpec } from '@/lib/editing/structure/graphics';
import { AddColorCardDialog, AddTitleDialog } from '../place/GraphicDialogs';
import { useLandedToast } from '../place/landed-toast';
import { useT } from '@/i18n';
import classes from './TimelineMenus.module.css';
import type { KeyAction } from '@/lib/editing/keymap';
import { useLiveChordLookup } from '../prefs/live-chords';

/** W0.6 (P3-SEL-03): the Select menu — the same commands as the keyboard, reachable by pointer and touch. */
export function SelectMenu({ store, height }: { store: EditorStore; height?: number }) {
  const t = useT('editor-timeline.selectMenu');
  const run = (command: SelectCommand) => {
    const s = store.getState();
    store.setUi({ selection: runSelectCommand(s.doc, s.ui, command) });
  };
  return (
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <Button size="compact-sm" fz="xs" h={height} px={height ? 6 : undefined} variant="subtle" color="gray" rightSection={<IconChevronDown size={13} />} data-testid="select-menu" fw={500}>
          {t('button')}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item onClick={() => run('all')} data-testid="select-menu-all">
          {t('all')}
        </Menu.Item>
        <Menu.Item onClick={() => run('forward')} data-testid="select-menu-forward">
          {t('forward')}
        </Menu.Item>
        <Menu.Item onClick={() => run('backward')} data-testid="select-menu-backward">
          {t('backward')}
        </Menu.Item>
        <Menu.Item onClick={() => run('track')} data-testid="select-menu-track">
          {t('track')}
        </Menu.Item>
        <Menu.Divider />
        <Menu.Item onClick={() => run('none')} data-testid="select-menu-none">
          {t('none')}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

export interface AddAtPlayheadMenuProps {
  store: EditorStore;
  disabled?: boolean;
  /** Applies a program as one undo step (refusals are reported by the caller). */
  onApply: (ops: readonly EditOp[], label: string) => void;
  height?: number;
}

/** W0.6 (P5-GEN-03): "Add colour card" and "Add title" at the playhead; the new item is selected. */
export function AddAtPlayheadMenu({ store, disabled = false, onApply, height }: AddAtPlayheadMenuProps) {
  const t = useT('editor-timeline.addMenu');
  const showLandedToast = useLandedToast();
  const [dialog, setDialog] = useState<'title' | 'card' | null>(null);
  const finish = (itemId: string, isTitle: boolean, landed: { trackName: string; frame: number; tc: string }) => {
    const placed = store.getState().doc.tracks.some((t) => t.items.some((i) => i.id === itemId));
    if (!placed) return;
    store.setUi({ selection: [itemId] });
    showLandedToast(store, landed, isTitle ? t('titleAdded') : t('cardAdded'));
  };
  const add = (kind: PlayheadAddKind) => {
    const s = store.getState();
    const { ops, itemId } = addAtPlayhead(s.doc, kind, s.ui.playheadFrame, newMarkerId);
    onApply(ops, kind === 'title' ? t('addTitle') : t('addCard'));
    const placed = store.getState().doc.tracks.find((t) => t.items.some((i) => i.id === itemId));
    if (placed) finish(itemId, kind === 'title', { trackName: placed.name, frame: s.ui.playheadFrame, tc: framesToTimecode(s.ui.playheadFrame, s.doc.settings.rate) });
  };
  const addWith = (spec: GraphicSpec, trackId: string | null) => {
    const s = store.getState();
    const built = buildGraphicOps(s.doc, { spec, at: s.ui.playheadFrame, trackId, newId: newMarkerId, isLocked: (t) => store.effectiveTrackFlags(t).locked });
    setDialog(null);
    onApply(built.ops, built.label);
    finish(built.itemId, built.label === 'Add title', built.landedAt);
  };
  return (
    <>
      <Menu position="bottom-start" withinPortal disabled={disabled}>
        <Menu.Target>
          <Button size="compact-sm" fz="xs" h={height} px={height ? 6 : undefined} variant="subtle" color="gray" leftSection={<IconPlus size={14} />} disabled={disabled} data-testid="add-menu" fw={500}>
            {t('button')}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item onClick={() => add('title')} data-testid="add-title">
            {t('titleHere')}
          </Menu.Item>
          <Menu.Item onClick={() => setDialog('title')} data-testid="add-title-options">
            {t('titleOptions')}
          </Menu.Item>
          <Menu.Item onClick={() => add('colorCard')} data-testid="add-colour-card">
            {t('cardHere')}
          </Menu.Item>
          <Menu.Item onClick={() => setDialog('card')} data-testid="add-colour-card-options">
            {t('cardOptions')}
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
      {dialog === 'title' && <AddTitleDialog opened doc={store.getState().doc} onClose={() => setDialog(null)} onSubmit={addWith} />}
      {dialog === 'card' && <AddColorCardDialog opened doc={store.getState().doc} onClose={() => setDialog(null)} onSubmit={addWith} />}
    </>
  );
}

const TRIM_MODES: readonly TrimMode[] = ['ripple', 'roll', 'slip', 'slide'];

/**
 * Trim mode: what dragging a clip's trim handle on the timeline does. Ripple is the default; roll,
 * slip and slide are the other three modes the edit engine has always carried. View state only.
 */
export function TrimModeMenu({ value, onChange, height }: { value: TrimMode; onChange: (mode: TrimMode) => void; height?: number }) {
  const t = useT('editor-timeline.trim');
  const current = TRIM_MODES.find((m) => m === value) ?? TRIM_MODES[0];
  const currentLabel = t(`modes.${current}.label`);
  return (
    <>
    <VisuallyHidden role="status" data-testid="trim-mode-status">
      {t('button', { mode: currentLabel })}
    </VisuallyHidden>
    <Menu position="bottom-start" withinPortal>
      <Menu.Target>
        <Button size="compact-sm" fz="xs" h={height} px={height ? 6 : undefined} className={classes.trimMode} variant="subtle" color="gray" rightSection={<IconChevronDown size={13} />} data-testid="trim-mode" data-mode={value} aria-label={t('ariaLabel', { mode: currentLabel })} fw={500}>
          {t('button', { mode: currentLabel })}
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>{t('menuLabel')}</Menu.Label>
        {TRIM_MODES.map((mode) => (
          <Menu.Item key={mode} onClick={() => onChange(mode)} data-testid={`trim-mode-${mode}`} aria-checked={value === mode} rightSection={value === mode ? <IconCheck size={14} /> : null}>
            <div>{t(`modes.${mode}.label`)}</div>
            <div style={{ fontSize: 12, opacity: 0.7, maxWidth: 220 }}>{t(`modes.${mode}.hint`)}</div>
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
    </>
  );
}

const TRIM_MODE_ACTION: Record<TrimMode, KeyAction> = { ripple: 'trimRipple', roll: 'trimRoll', slip: 'trimSlip', slide: 'trimSlide' };

/**
 * T-019: the four trim modes as one always-visible, labelled control (Cut and Edit), each with its live chord.
 * The mode on is announced as a status line for assistive tech. `folded` (a narrow timeline) gives the
 * same choice as the compact menu instead, so the state is never lost.
 */
export function TrimModeControl({ value, onChange, height, folded = false }: { value: TrimMode; onChange: (mode: TrimMode) => void; height?: number; folded?: boolean }) {
  const t = useT('editor-timeline.trim');
  const chordFor = useLiveChordLookup();
  const idBase = useId();
  const groupRef = useRef<HTMLDivElement>(null);
  if (folded) return <TrimModeMenu value={value} onChange={onChange} height={height} />;
  /** Radio pattern: one Tab stop (the mode on), the arrow keys move and select. Only while the focus came from the keyboard, so a click leaves the arrows to the editor. */
  const onGroupKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
    const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (step === 0) return;
    try {
      if (!(event.target as HTMLElement).matches(':focus-visible')) return;
    } catch {
      /* no :focus-visible support: treat as keyboard */
    }
    event.preventDefault();
    const next = TRIM_MODES[(TRIM_MODES.indexOf(value) + step + TRIM_MODES.length) % TRIM_MODES.length];
    onChange(next);
    groupRef.current?.querySelector<HTMLElement>(`[data-testid="trim-mode-${next}"]`)?.focus();
  };
  const currentLabel = t(`modes.${value}.label`);
  return (
    <div ref={groupRef} role="radiogroup" aria-label={t('groupLabel')} data-testid="trim-mode" data-mode={value} data-editor-shortcuts="through" data-editor-own-keys="radio" onKeyDown={onGroupKeyDown} style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
      {TRIM_MODES.map((mode) => {
        const chord = chordFor(TRIM_MODE_ACTION[mode]);
        const label = t(`modes.${mode}.label`);
        const on = value === mode;
        return (
          <Button
            key={mode}
            size="compact-sm"
            fz="xs"
            h={height}
            px={6}
            fw={500}
            className={on ? `${classes.trimMode} ${classes.stateOn}` : classes.trimMode}
            variant={on ? 'light' : 'subtle'}
            color={on ? 'yellow' : 'gray'}
            role="radio"
            aria-checked={on}
            tabIndex={on ? 0 : -1}
            aria-label={label}
            aria-describedby={`${idBase}-${mode}`}
            title={chord ? t('chordHint', { label: t(`modes.${mode}.hint`), chord }) : t(`modes.${mode}.hint`)}
            onClick={() => onChange(mode)}
            data-testid={`trim-mode-${mode}`}
          >
            {label}
            <VisuallyHidden id={`${idBase}-${mode}`}>{t(`modes.${mode}.hint`)}</VisuallyHidden>
          </Button>
        );
      })}
      <VisuallyHidden role="status" data-testid="trim-mode-status">
        {t('button', { mode: currentLabel })}
      </VisuallyHidden>
    </div>
  );
}

/**
 * R08.1: "Edit sound separately". While on, selecting a clip selects only that clip and trims make
 * J/L cuts (the picture and its sound stay linked but move on their own). Holding Alt/Option while
 * dragging does the same for one drag. View state only.
 */
export function EditSeparatelyToggle({ value, onChange, height }: { value: boolean; onChange: (value: boolean) => void; height?: number }) {
  const t = useT('editor-timeline.editSeparately');
  return (
    <Button
      size="compact-sm"
      fz="xs"
      h={height}
      px={height ? 6 : undefined}
      className={value ? classes.stateOn : undefined}
      variant={value ? 'light' : 'subtle'}
      color={value ? 'yellow' : 'gray'}
      fw={500}
      onClick={() => onChange(!value)}
      aria-pressed={value}
      title={t('title')}
      data-testid="edit-separately"
    >
      {t('label')}
    </Button>
  );
}
