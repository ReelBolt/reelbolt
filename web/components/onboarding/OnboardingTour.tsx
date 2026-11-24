'use client';

import { useIsPhoneClass } from '@/lib/hooks/use-device-class';
import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Button, VisuallyHidden } from '@mantine/core';
import { useFocusTrap, useMergedRef, useReducedMotion } from '@mantine/hooks';
import { useLocale, useT } from '@/i18n';
import { guideHrefFor } from '@/lib/onboarding/guide-links';
import { useOnboardingContext } from './onboarding-context';
import { buildTourSteps } from './tour-steps';
import classes from './onboarding.module.css';

interface Box {
  top: number;
  left: number;
  width: number;
  height: number;
}

const GAP = 12;
const PAD = 6;
const MARGIN = 16;

/**
 * The element a step points at, but only when it can honestly be pointed at: present, laid out and
 * fully inside the window (after one attempt to scroll it into view). Otherwise `null`, and the card
 * is centred - the tour never spotlights nothing.
 */
export function measureAnchor(anchor: string | undefined): Box | null {
  if (!anchor || typeof document === 'undefined') return null;
  const el = document.querySelector<HTMLElement>(`[data-tour="${anchor}"]`);
  if (!el || el.getClientRects().length === 0) return null;
  let r = el.getBoundingClientRect();
  const inside = (b: DOMRect) =>
    b.width > 0 && b.height > 0 && b.top >= 0 && b.left >= 0 && b.bottom <= window.innerHeight && b.right <= window.innerWidth;
  if (!inside(r)) {
    el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'auto' });
    r = el.getBoundingClientRect();
    if (!inside(r)) return null;
  }
  return { top: r.top - PAD, left: r.left - PAD, width: r.width + PAD * 2, height: r.height + PAD * 2 };
}

/** Where the card goes next to a spotlight: below, above, right, left - else centred (`null`). */
export function placeCard(target: Box, card: { width: number; height: number }, win: { width: number; height: number }) {
  const clampX = (x: number) => Math.max(MARGIN, Math.min(x, win.width - card.width - MARGIN));
  const clampY = (y: number) => Math.max(MARGIN, Math.min(y, win.height - card.height - MARGIN));
  const below = target.top + target.height + GAP;
  if (below + card.height <= win.height - MARGIN) return { top: below, left: clampX(target.left) };
  const above = target.top - GAP - card.height;
  if (above >= MARGIN) return { top: above, left: clampX(target.left) };
  const right = target.left + target.width + GAP;
  if (right + card.width <= win.width - MARGIN) return { top: clampY(target.top), left: right };
  const left = target.left - GAP - card.width;
  if (left >= MARGIN) return { top: clampY(target.top), left };
  return null;
}

export default function OnboardingTour({ onClose }: { onClose: (reason: 'completed' | 'skipped') => void }) {
  const { editorEnabled, appsEnabled } = useOnboardingContext();
  const t = useT('onboarding.tour');
  const locale = useLocale();
  const steps = useMemo(() => buildTourSteps({ editorEnabled, appsEnabled }), [editorEnabled, appsEnabled]);
  const [index, setIndex] = useState(0);
  const step = steps[index]!;
  const last = index === steps.length - 1;
  const sheet = useIsPhoneClass();
  const reduceMotion = useReducedMotion();
  const titleId = useId();
  const bodyId = useId();
  const cardRef = useRef<HTMLDivElement | null>(null);
  const trapRef = useFocusTrap(true);
  const mergedRef = useMergedRef(cardRef, trapRef);
  const [target, setTarget] = useState<Box | null>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);

  // Remember where focus was, and give it back when the tour ends (however it ends).
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    return () => {
      if (previous && previous.isConnected && previous !== document.body) previous.focus();
      else document.getElementById('main-content')?.focus();
    };
  }, []);

  const finish = useCallback((reason: 'completed' | 'skipped') => onClose(reason), [onClose]);

  // Esc closes the tour, which counts as skipping it.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.preventDefault();
      e.stopPropagation();
      finish('skipped');
    };
    document.addEventListener('keydown', onKey, true);
    return () => document.removeEventListener('keydown', onKey, true);
  }, [finish]);

  // Keep the spotlight on its anchor through resizes and scrolling.
  useLayoutEffect(() => {
    if (sheet || !step.anchor) return;
    let frame = 0;
    const update = () => {
      frame = 0;
      const next = measureAnchor(step.anchor);
      setTarget((prev) =>
        prev && next && prev.top === next.top && prev.left === next.left && prev.width === next.width && prev.height === next.height
          ? prev
          : next,
      );
    };
    const schedule = () => {
      if (!frame) frame = window.requestAnimationFrame(update);
    };
    update();
    window.addEventListener('resize', schedule);
    window.addEventListener('scroll', schedule, true);
    return () => {
      if (frame) window.cancelAnimationFrame(frame);
      window.removeEventListener('resize', schedule);
      window.removeEventListener('scroll', schedule, true);
    };
  }, [sheet, step.anchor, index]);

  useLayoutEffect(() => {
    const card = cardRef.current;
    if (!target || sheet || !card) return;
    const placed = placeCard(target, { width: card.offsetWidth, height: card.offsetHeight }, { width: window.innerWidth, height: window.innerHeight });
    setPos(placed);
  }, [target, sheet, index]);

  const anchored = !sheet && Boolean(step.anchor);
  const spot = anchored && target && pos ? target : null;
  const mode = sheet ? classes.sheet : spot ? '' : classes.centered;
  const style = spot && pos ? { top: pos.top, left: pos.left } : undefined;

  return createPortal(
    <div className={classes.root} data-testid="onboarding-tour" data-reduced-motion={reduceMotion ? 'true' : 'false'}>
      <div className={classes.blocker} />
      {spot ? (
        <div className={classes.spot} style={spot} data-testid="onboarding-spot" aria-hidden="true" />
      ) : (
        <div className={classes.dim} aria-hidden="true" />
      )}
      <div
        ref={mergedRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={bodyId}
        tabIndex={-1}
        className={`${classes.card} ${mode}`}
        style={style}
        data-onboarding-tour="card"
        data-placement={sheet ? 'sheet' : spot ? 'anchored' : 'centered'}
        data-step={step.id}
      >
        <VisuallyHidden aria-live="polite" role="status">
          {t('announce', { n: index + 1, total: steps.length, title: t.msg(step.text.title) })}
        </VisuallyHidden>
        <div className={classes.progress}>
          <ol className={classes.dots} aria-hidden="true">
            {steps.map((s, i) => (
              <li key={s.id} className={`${classes.dot} ${i === index ? classes.dotActive : ''}`} />
            ))}
          </ol>
          <span>{t('progress', { n: index + 1, total: steps.length })}</span>
        </div>
        <h2 id={titleId} className={classes.title}>
          {t.msg(step.text.title)}
        </h2>
        <p id={bodyId} className={classes.body}>
          {t.msg(step.text.body)}
        </p>
        <a className={classes.learn} href={guideHrefFor(step.guide, locale)} target="_blank" rel="noopener noreferrer">
          {t('learnMore')}
        </a>
        <div className={classes.actions}>
          <Button variant="subtle" color="gray" style={{ minHeight: 44 }} onClick={() => finish('skipped')}>
            {t('skip')}
          </Button>
          <div className={classes.actionsMain}>
            <Button variant="default" style={{ minHeight: 44 }} disabled={index === 0} onClick={() => setIndex((i) => Math.max(0, i - 1))}>
              {t('back')}
            </Button>
            <Button
              data-autofocus
              style={{ minHeight: 44 }}
              onClick={() => (last ? finish('completed') : setIndex((i) => i + 1))}
            >
              {last ? t('finish') : t('next')}
            </Button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
