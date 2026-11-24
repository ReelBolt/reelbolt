'use client';

import { useEffect, useState } from 'react';

/** True once `active` has stayed true for `afterMs`; false again as soon as `active` flips off. */
export function useSlow(active: boolean, afterMs = 10000): boolean {
  const [slow, setSlow] = useState(false);
  useEffect(() => {
    if (!active) return;
    const id = setTimeout(() => setSlow(true), afterMs);
    return () => {
      clearTimeout(id);
      setSlow(false);
    };
  }, [active, afterMs]);
  return active && slow;
}
