'use client';

import React, { useCallback, useEffect, useId, useRef, useState } from 'react';
import { ActionIcon, FocusTrap } from '@mantine/core';
import { IconChevronDown, IconX } from '@tabler/icons-react';
import { registerSheet } from './sheet-history';
import { useKeyboardInset } from './use-keyboard-inset';
import classes from './phone.module.css';
import { useT } from '@/i18n';

export type SheetDetent = 'peek' | 'half' | 'full';

/** The three resting heights (VF8): a glance, working room with the timeline still visible, and nearly everything. */
export const SHEET_HEIGHTS: Record<SheetDetent, string> = { peek: '120px', half: '45dvh', full: '90dvh' };

const ORDER: SheetDetent[] = ['peek', 'half', 'full'];
const SWIPE_CLOSE_PX = 60;

export function lowerDetent(detent: SheetDetent): SheetDetent | null {
  const i = ORDER.indexOf(detent);
  return i <= 0 ? null : ORDER[i - 1];
}

export function raiseDetent(detent: SheetDetent): SheetDetent {
  return ORDER[Math.min(ORDER.length - 1, ORDER.indexOf(detent) + 1)];
}

export interface BottomSheetProps {
  opened: boolean;
  onClose: () => void;
  /** The accessible name and the visible title. */
  title: string;
  detent: SheetDetent;
  onDetentChange: (detent: SheetDetent) => void;
  /** A dimmed backdrop that closes the sheet when tapped. Off by default so the strip stays usable. */
  backdrop?: boolean;
  /** `right`: a drawer on the right edge (a landscape phone). */
  side?: 'bottom' | 'right';
  /** Stable id for tests and the history entry. */
  testId?: string;
  /** A taller half detent for a sheet that needs the room (the timeline Edit/Inspector sheet). Other sheets keep the default. */
  halfHeight?: string;
  /**
   * Keep the content mounted (hidden) while closed, so what was typed into it survives closing and re-opening.
   * The closed sheet is display:none, not in the accessibility tree, and has no test id.
   */
  keepMounted?: boolean;
  /** Receives the pinned footer element (under the scrolling body); the footer shows only when something fills it. */
  footerRef?: (el: HTMLDivElement | null) => void;
  children: React.ReactNode;
}

/**
 * The one phone sheet. Peek / half / full detents, a grabber (swipe down to lower, tap to raise), Escape,
 * Android back and the iOS swipe-back close the top sheet, the soft keyboard refits it, safe-area insets keep
 * it clear of the home indicator, and focus stays inside while it is open. Controls are 44 px.
 */
export function BottomSheet({ opened, onClose, title, detent, onDetentChange, backdrop = false, side = 'bottom', testId = 'bottom-sheet', halfHeight, keepMounted = false, footerRef, children }: BottomSheetProps) {
  const t = useT('editor.phone.bottomSheet');
  const id = useId();
  const closeRef = useRef(onClose);
  useEffect(() => {
    closeRef.current = onClose;
  }, [onClose]);
  const returnFocus = useRef<HTMLElement | null>(null);
  const [drag, setDrag] = useState(0);
  const startY = useRef<number | null>(null);
  const keyboardInset = useKeyboardInset(opened);

  useEffect(() => {
    if (!opened) return;
    returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const unregister = registerSheet(id, () => closeRef.current());
    return () => {
      unregister();
      const target = returnFocus.current;
      returnFocus.current = null;
      if (target && document.contains(target)) target.focus({ preventScroll: true });
    };
  }, [opened, id]);

  const onGrabDown = useCallback((event: React.PointerEvent<HTMLButtonElement>) => {
    startY.current = event.clientY;
    event.currentTarget.setPointerCapture?.(event.pointerId);
  }, []);
  const onGrabMove = useCallback((event: React.PointerEvent<HTMLButtonElement>) => {
    if (startY.current === null) return;
    setDrag(Math.max(0, event.clientY - startY.current));
  }, []);
  const onGrabUp = useCallback(
    (event: React.PointerEvent<HTMLButtonElement>) => {
      if (startY.current === null) return;
      const moved = event.clientY - startY.current;
      startY.current = null;
      setDrag(0);
      if (moved >= SWIPE_CLOSE_PX) {
        const lower = lowerDetent(detent);
        if (lower) onDetentChange(lower);
        else onClose();
      } else if (Math.abs(moved) < 6) {
        // A tap is the button path for the gesture: raise, and from the top, lower again.
        onDetentChange(detent === 'full' ? 'half' : raiseDetent(detent));
      } else if (moved <= -SWIPE_CLOSE_PX) {
        onDetentChange(raiseDetent(detent));
      }
    },
    [detent, onClose, onDetentChange],
  );

  if (!opened && !keepMounted) return null;

  const style = {
    '--rb-sheet-h': detent === 'half' && halfHeight ? halfHeight : SHEET_HEIGHTS[detent],
    transform: drag > 0 ? `translateY(${drag}px)` : undefined,
    bottom: side === 'bottom' && keyboardInset > 0 ? keyboardInset : undefined,
    display: opened ? undefined : 'none',
  } as React.CSSProperties;

  return (
    <>
      {backdrop && opened && <div className={classes.sheetBackdrop} onClick={onClose} data-testid={`${testId}-backdrop`} aria-hidden />}
      <FocusTrap active={opened}>
        <section
          className={classes.sheet}
          data-side={side}
          data-detent={detent}
          data-keyboard={keyboardInset > 0 ? 'open' : undefined}
          role="dialog"
          aria-modal={backdrop ? true : undefined}
          aria-label={title}
          tabIndex={-1}
          style={style}
          data-testid={opened ? testId : undefined}
          aria-hidden={opened ? undefined : true}
        >
          <div className={classes.sheetBar}>
            {side === 'bottom' ? (
              <button
                type="button"
                className={classes.grabber}
                aria-label={detent === 'full' ? t('makeThePanelSmaller') : t('makeThePanelTaller')}
                onPointerDown={onGrabDown}
                onPointerMove={onGrabMove}
                onPointerUp={onGrabUp}
                onPointerCancel={() => {
                  startY.current = null;
                  setDrag(0);
                }}
                data-testid={`${testId}-grabber`}
              >
                <span className={classes.grabberPill} aria-hidden />
                <span className={classes.sheetTitle}>{title}</span>
              </button>
            ) : (
              <span className={classes.sheetTitle} style={{ flex: 1 }}>
                {title}
              </span>
            )}
            {side === 'bottom' && detent !== 'peek' ? (
              <ActionIcon variant="subtle" color="gray" w={44} h={44} aria-label={t('shrinkThePanel')} onClick={() => onDetentChange(lowerDetent(detent) ?? 'peek')} data-testid={`${testId}-lower`}>
                <IconChevronDown size={20} stroke={1.7} />
              </ActionIcon>
            ) : null}
            <ActionIcon variant="subtle" color="gray" w={44} h={44} aria-label={t('close', { title })} onClick={onClose} data-testid={`${testId}-close`}>
              <IconX size={20} stroke={1.7} />
            </ActionIcon>
          </div>
          <div className={classes.sheetBody}>{children}</div>
          {footerRef ? <div ref={footerRef} className={classes.sheetFooter} data-testid={`${testId}-footer`} /> : null}
        </section>
      </FocusTrap>
    </>
  );
}
