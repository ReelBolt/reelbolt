'use client';

import { useEffect, type RefObject } from 'react';
import { PHONE_STAGE_MIN_HEIGHT, spaceBelow, stageHeight } from '@/lib/editor/stage-fit';

/**
 * Keeps the editor's stage (and the panel beside it) as tall as the window allows without the page
 * scrolling (audit d6 N1): measures where the layout starts and what pads it below, and writes the
 * result to `--stage-height` on the layout. Measured again on a window resize and whenever the
 * editor's own column changes height (a notice appearing above the layout pushes it down).
 */
export function useStageFit(ref: RefObject<HTMLElement | null>, ready: boolean): void {
  useEffect(() => {
    const el = ref.current;
    if (!ready || !el) return;
    const measure = () => {
      const top = el.getBoundingClientRect().top + window.scrollY;
      const height = stageHeight(window.innerHeight, top, spaceBelow(el));
      const value = `${height}px`;
      if (el.style.getPropertyValue('--stage-height') !== value) el.style.setProperty('--stage-height', value);
      // On a phone the tools row sits above the stage inside the layout, so the stage starts lower than the layout does:
      // measure from the stage itself, so it and its zoom bar end inside the window (the CSS caps it at 56dvh).
      const stage = el.querySelector<HTMLElement>(':scope > [data-stage]');
      if (stage) {
        const stageTop = stage.getBoundingClientRect().top + window.scrollY;
        const phone = `${Math.max(PHONE_STAGE_MIN_HEIGHT, Math.floor(window.innerHeight - stageTop - spaceBelow(el)))}px`;
        if (el.style.getPropertyValue('--stage-height-phone') !== phone) el.style.setProperty('--stage-height-phone', phone);
      }
    };
    measure();
    window.addEventListener('resize', measure);
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    if (observer && el.parentElement) observer.observe(el.parentElement);
    return () => {
      window.removeEventListener('resize', measure);
      observer?.disconnect();
    };
  }, [ready, ref]);
}
