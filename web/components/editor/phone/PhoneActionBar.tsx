'use client';

import React, { useCallback, useEffect, useState } from 'react';
import {
  IconArrowsLeftRight,
  IconCheck,
  IconChecks,
  IconCopy,
  IconDots,
  IconMapPinPlus,
  IconPhotoPlus,
  IconScissors,
  IconTextPlus,
  IconTrash,
  IconX,
} from '@tabler/icons-react';
import { BottomSheet, type SheetDetent } from './BottomSheet';
import { MoreSheetBody, NudgeSheetBody, TrimSheetBody } from './ActionSheets';
import { ClipSheetBody, type ClipSheetSection } from './ClipSheet';
import { onClipSheetRequest } from './clip-sheet-bus';
import type { PhoneActions } from './phone-actions';
import classes from './phone.module.css';
import { useT } from '@/i18n';

type BarSheet = 'trim' | 'nudge' | 'clip' | 'more';


const DETENT: Record<BarSheet, SheetDetent> = { trim: 'half', nudge: 'half', clip: 'half', more: 'half' };

type IconType = React.ComponentType<{ size?: number | string; 'aria-hidden'?: boolean }>;

interface BarButtonProps {
  id: string;
  label: string;
  icon: IconType;
  onClick: () => void;
  disabled?: boolean;
  danger?: boolean;
  pressed?: boolean;
  badge?: number;
}

function BarButton({ id, label, icon: Icon, onClick, disabled, danger, pressed, badge }: BarButtonProps) {
  return (
    <button
      type="button"
      className={`${classes.actionBtn} ${danger ? classes.actionBtnDanger : ''}`}
      disabled={disabled}
      style={{ height: 44, minWidth: 44 }}
      aria-pressed={pressed}
      onClick={onClick}
      data-testid={`phone-action-${id}`}
    >
      <Icon size={20} aria-hidden />
      <span>{label}</span>
      {badge !== undefined && badge > 0 && (
        <span className={classes.actionBadge} aria-hidden>
          {badge}
        </span>
      )}
    </button>
  );
}

export interface PhoneActionBarProps {
  actions: PhoneActions;
  /** Opens the Media sheet (T-005 refines what it shows). */
  onAddMedia: () => void;
}

/**
 * RM.5: the 52 px bar above the dock. Nothing selected: Add media, Add text, Marker, Select. A clip selected:
 * Split, Duplicate, Trim, Nudge, Delete (8 px clear of the rest), More. Select mode: tap clips to build a
 * group, then Duplicate, Nudge, Lift or Delete them together. Delete and Lift show the 6 second Undo.
 */
export function PhoneActionBar({ actions, onAddMedia }: PhoneActionBarProps) {
  const t = useT('editor.phone.phoneActionBar');
  const a = actions;
  const [sheet, setSheet] = useState<BarSheet | null>(null);
  const [detent, setDetent] = useState<SheetDetent>('half');
  const count = a.selection.length;

  const [focus, setFocus] = useState<ClipSheetSection | undefined>(undefined);
  const open = useCallback((next: BarSheet) => {
    setDetent(DETENT[next]);
    setSheet(next);
  }, []);
  const openClip = useCallback((section: ClipSheetSection) => {
    setFocus(section);
    setDetent(section === 'picture' ? 'half' : 'full');
    setSheet('clip');
  }, []);
  // The strip's Clip chip asks for the same sheet.
  useEffect(() => onClipSheetRequest(openClip), [openClip]);
  const close = useCallback(() => setSheet(null), []);

  // A sheet about a clip closes itself when the selection it was about goes away.
  const needsClip = sheet === 'trim' || sheet === 'clip';
  const active: BarSheet | null = sheet === null ? null : count === 0 ? null : needsClip && a.clip === null ? null : sheet;

  let buttons: React.ReactNode;
  if (a.selectMode) {
    buttons = (
      <>
        <BarButton id="select-done" label={t('done')} icon={IconCheck} onClick={() => a.setSelectMode(false)} badge={count} pressed />
        <BarButton id="select-clear" label={t('clear')} icon={IconX} onClick={a.clearSelection} disabled={count === 0} />
        <BarButton id="duplicate" label={t('duplicate')} icon={IconCopy} onClick={a.duplicate} disabled={a.readOnly || count === 0} />
        <BarButton id="nudge" label={t('nudge')} icon={IconArrowsLeftRight} onClick={() => open('nudge')} disabled={a.readOnly || count === 0} />
        <BarButton id="lift" label={t('lift')} icon={IconScissors} onClick={a.lift} disabled={a.readOnly || count === 0} />
        <span className={classes.actionGap} aria-hidden />
        <BarButton id="delete" label={t('delete')} icon={IconTrash} onClick={a.remove} disabled={a.readOnly || count === 0} danger />
      </>
    );
  } else if (count === 0) {
    buttons = (
      <>
        <BarButton id="add-media" label={t('addMedia')} icon={IconPhotoPlus} onClick={onAddMedia} disabled={a.readOnly} />
        <BarButton id="add-text" label={t('addText')} icon={IconTextPlus} onClick={a.addText} disabled={a.readOnly} />
        <BarButton id="marker" label={t('marker')} icon={IconMapPinPlus} onClick={a.addMarker} disabled={a.readOnly} />
        <BarButton id="select" label={t('select')} icon={IconChecks} onClick={() => a.setSelectMode(true)} />
      </>
    );
  } else {
    buttons = (
      <>
        <BarButton id="split" label={t('split')} icon={IconScissors} onClick={a.split} disabled={a.readOnly} />
        <BarButton id="duplicate" label={t('duplicate2')} icon={IconCopy} onClick={a.duplicate} disabled={a.readOnly} />
        <BarButton id="trim" label={t('trim')} icon={IconArrowsLeftRight} onClick={() => open('trim')} disabled={a.readOnly || a.clip === null} />
        <BarButton id="nudge" label={t('nudge2')} icon={IconArrowsLeftRight} onClick={() => open('nudge')} disabled={a.readOnly} />
        <BarButton id="more" label={t('more')} icon={IconDots} onClick={() => open('more')} />
        <span className={classes.actionGap} aria-hidden />
        <BarButton id="delete" label={t('delete2')} icon={IconTrash} onClick={a.remove} disabled={a.readOnly} danger />
      </>
    );
  }

  return (
    <>
      <div className={classes.actionBar} style={{ height: 52 }} role="toolbar" aria-label={t('clipActions')} data-testid="phone-action-bar" data-mode={a.selectMode ? 'select' : count === 0 ? 'none' : 'clip'}>
        {buttons}
      </div>
      {active && (
        <BottomSheet key={active} opened onClose={close} title={t(`sheetTitles.${active}`)} detent={detent} onDetentChange={setDetent} testId={`phone-action-sheet-${active}`}>
          {active === 'trim' && <TrimSheetBody actions={a} onDone={close} />}
          {active === 'nudge' && <NudgeSheetBody actions={a} />}
          {active === 'clip' && <ClipSheetBody actions={a} focus={focus} />}
          {active === 'more' && <MoreSheetBody actions={a} onClose={close} onOpen={openClip} onEnterSelect={() => a.setSelectMode(true)} />}
        </BottomSheet>
      )}
    </>
  );
}
