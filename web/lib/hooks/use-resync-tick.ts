'use client';

import { useEffect, useRef, useState } from 'react';
import type { ExecutionStreamConnectionState } from './use-execution-stream';

/**
 * A counter that goes up when the run page may have missed its last update: the tab became visible
 * again, or the live stream reconnected. Nothing happens once the run is finished. The page feeds the
 * counter into its coalesced resync, so bursts of triggers become one read.
 */
export function useResyncTick(connectionState: ExecutionStreamConnectionState, terminal: boolean): number {
  const [tick, setTick] = useState(0);
  const terminalRef = useRef(terminal);
  useEffect(() => {
    terminalRef.current = terminal;
  }, [terminal]);

  useEffect(() => {
    const onVisible = () => {
      if (!document.hidden && !terminalRef.current) setTick((t) => t + 1);
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  }, []);

  const previous = useRef(connectionState);
  useEffect(() => {
    if (previous.current === 'reconnecting' && connectionState === 'connected' && !terminalRef.current) {
      setTick((t) => t + 1);
    }
    previous.current = connectionState;
  }, [connectionState]);

  return tick;
}
