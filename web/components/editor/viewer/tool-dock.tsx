'use client';

import React, { createContext, useContext } from 'react';
import { createPortal } from 'react-dom';

/**
 * The viewer's tool dock: a row between the viewer's header and the picture frame, so a tool's controls never
 * cover the picture (handles and draw starts stay reachable across all of it). `ProgramViewer` provides the
 * element; a tool renders its bar through {@link ToolDock}. With no dock (a tool mounted on its own) the bar
 * renders in place.
 */
export const ToolDockContext = createContext<HTMLElement | null>(null);

export function ToolDock({ children }: { children: React.ReactNode }) {
  const dock = useContext(ToolDockContext);
  return dock ? createPortal(children, dock) : <>{children}</>;
}

/** The one compact row every tool bar uses. */
export const TOOL_ROW_STYLE: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  gap: 6,
  padding: '4px 12px',
  background: 'var(--rb-ed-header-bg)',
  borderBlock: '1px solid var(--rb-ed-line)',
  touchAction: 'manipulation',
};
