// ReelBolt block "halftone" (MIT). Halftone dot pattern overlay that fades toward the centre; dots slowly drift.
// Render cost: the masked, rotated full-frame layer adds roughly 0.3 s/frame at 1080p; use it on short beats.
import React from 'react';
import {AbsoluteFill, useCurrentFrame} from 'remotion';

export type HalftoneProps = {
  children?: React.ReactNode;
  /** Dot grid pitch in px. Default 18. */
  dotSize?: number;
  /** Dot colour. Default '#000'. */
  color?: string;
  /** Overlay strength 0..1. Default 0.5. */
  intensity?: number;
  /** Pattern rotation in degrees. Default 45. */
  angle?: number;
  /** Drift in px per second (0 = still). Default 12. */
  drift?: number;
  /** How far the clear centre extends, 0..100 (%). Default 25. */
  clearCenter?: number;
};

export const Halftone: React.FC<HalftoneProps> = ({children, dotSize = 18, color = '#000', intensity = 0.5, angle = 45, drift = 12, clearCenter = 25}) => {
  const frame = useCurrentFrame();
  const off = (frame / 30) * drift;
  const mask = `radial-gradient(ellipse at center, transparent ${clearCenter}%, #000 100%)`;
  return (
    <AbsoluteFill>
      {children}
      <AbsoluteFill style={{pointerEvents: 'none', opacity: Math.min(1, Math.max(0, intensity)), WebkitMaskImage: mask, maskImage: mask, overflow: 'hidden'}}>
        <AbsoluteFill
          style={{
            left: '-25%',
            top: '-25%',
            width: '150%',
            height: '150%',
            transform: `rotate(${angle}deg)`,
            backgroundImage: `radial-gradient(circle at center, ${color} 0 ${dotSize * 0.28}px, transparent ${dotSize * 0.3}px)`,
            backgroundSize: `${dotSize}px ${dotSize}px`,
            backgroundPosition: `${off}px ${off}px`,
          }}
        />
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
