'use client';
import { useEffect, useRef, useState } from 'react';
import dynamic from 'next/dynamic';
import { useReducedMotion } from '@/lib/use-reduced-motion';
import { readSceneEnv, shouldMountScene, whenFirstInteraction } from '@/lib/scene-gate';
import { SceneBoundary } from './SceneBoundary';

// Static line-drawing stand-in for the reduced-motion path and the WebGL-failure fallback — keeps
// the "viewfinder" panel reading as a technical readout with zero motion and zero WebGL.
function ViewfinderStatic() {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 100 100"
      className="h-full w-full p-6 text-accent"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.2"
    >
      <path d="M50 8 L88 50 L50 92 L12 50 Z" />
      <path d="M50 32 L69 50 L50 68 L31 50 Z" />
    </svg>
  );
}

// Declared at module scope (not inside the component) so its identity is stable across renders, and
// `next/dynamic` with `{ ssr: false }` is only legal inside a Client Component in the App Router —
// which is why this file is 'use client'.
const LazyViewfinderScene = dynamic(() => import('./ViewfinderScene'), {
  ssr: false,
  loading: () => <ViewfinderStatic />,
});

// The only component other files should import to get the viewfinder 3D
// scene: it isolates every WebGL concern (SSR exclusion, lazy loading,
// reduced-motion opt-out, visibility gating) behind a server-renderable
// surface. There is no scroll-progress ref — this scene only ever spins in
// place.
export function ViewfinderMount() {
  const reduced = useReducedMotion();
  const wrapRef = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const [eligible, setEligible] = useState(false);

  // The shared scene gate: wide desktop, >=4 cores, no Save-Data, mounted after first interaction.
  useEffect(() => {
    if (reduced || !shouldMountScene(readSceneEnv())) return;
    return whenFirstInteraction(() => setEligible(true));
  }, [reduced]);

  const mountScene = !reduced && eligible;

  useEffect(() => {
    const wrap = wrapRef.current;
    if (!mountScene || !wrap) return;

    const observer = new IntersectionObserver(
      ([entry]) => setVisible(entry.isIntersecting),
      { threshold: 0 },
    );
    observer.observe(wrap);

    const onVisibilityChange = () => {
      if (document.hidden) setVisible(false);
    };
    document.addEventListener('visibilitychange', onVisibilityChange);

    return () => {
      observer.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [mountScene]);

  if (!mountScene) {
    return <ViewfinderStatic />;
  }

  return (
    <div ref={wrapRef} aria-hidden="true" className="h-full w-full">
      <SceneBoundary fallback={<ViewfinderStatic />}>
        <LazyViewfinderScene active={visible} />
      </SceneBoundary>
    </div>
  );
}
