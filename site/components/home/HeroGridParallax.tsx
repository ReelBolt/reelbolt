'use client';
import type { ReactNode } from 'react';
import { useScrollParallax } from '@/lib/use-scroll-parallax';

// Client leaf that gives the hero's grid its scroll parallax. Hero itself stays a server component;
// only this wrapper needs state/effects. The wrapping div (rather than forwardRef on GridOverlay)
// keeps the shared primitive untouched for its other call sites.
export function HeroGridParallax({ children }: { children: ReactNode }) {
  const gridParallaxRef = useScrollParallax<HTMLDivElement>(10);

  return (
    <div ref={gridParallaxRef} className="absolute inset-0">
      {children}
    </div>
  );
}
