'use client';

import { useEffect, useState } from 'react';

/**
 * How many CSS px of the layout viewport the soft keyboard covers (VF8), from `visualViewport`. A bottom sheet
 * lifts by this much so its focused field stays visible. 0 where there is no `visualViewport` or no keyboard.
 */
export function useKeyboardInset(active: boolean): number {
  const [inset, setInset] = useState(0);
  useEffect(() => {
    if (!active) return;
    const vv = typeof window !== 'undefined' ? window.visualViewport : null;
    if (!vv) return;
    const refit = () => setInset(Math.max(0, Math.round(window.innerHeight - vv.height - vv.offsetTop)));
    refit();
    vv.addEventListener('resize', refit);
    vv.addEventListener('scroll', refit);
    return () => {
      vv.removeEventListener('resize', refit);
      vv.removeEventListener('scroll', refit);
    };
  }, [active]);
  return active ? inset : 0;
}
