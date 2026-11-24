// Pure-CSS scroll reveal. The entrance animation lives in app/globals.css (`.reveal`) and is driven
// by `animation-timeline: view()`, so it needs no client JS and no IntersectionObserver. The class
// hides nothing by default: without animation-timeline support, or with prefers-reduced-motion set,
// the element is simply visible. `delay` is accepted and ignored so existing call sites compile.
import type { ComponentType, ElementType, ReactNode } from 'react';

export function Reveal({
  children,
  as: Tag = 'div',
  className = '',
}: {
  children: ReactNode;
  /** Ignored. The CSS reveal has no per-element delay; kept so call sites compile unchanged. */
  delay?: number;
  as?: ElementType;
  className?: string;
}) {
  // `as` accepts any element type at the call site, so cast once to the shape this component passes.
  const Component = Tag as ComponentType<{ className?: string; children?: ReactNode }>;
  return <Component className={`reveal ${className}`.trim()}>{children}</Component>;
}
