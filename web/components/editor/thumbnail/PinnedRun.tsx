'use client';

import React, { createContext, useContext } from 'react';
import { createPortal } from 'react-dom';

/** The element a phone sheet reserves under its scrolling body for the Run button (R14.2), or null. */
export const RunFooterContext = createContext<HTMLElement | null>(null);

export function useRunFooter(): HTMLElement | null {
  return useContext(RunFooterContext);
}

/**
 * Puts a tool's Run button (and the sentences about it) in the sheet's pinned footer when there is one, so
 * it is always on screen next to the brush, the marks and the prompt. Without a footer (desktop) it renders in place.
 */
export function PinnedRun({ children }: { children: React.ReactNode }) {
  const footer = useRunFooter();
  return footer ? createPortal(children, footer) : <>{children}</>;
}
