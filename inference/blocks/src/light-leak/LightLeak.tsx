// ReelBolt block "light-leak" (MIT). Warm film light leaks: drifting gradient blobs blended with screen.
import React from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig} from 'remotion';

export type LightLeakProps = {
  children?: React.ReactNode;
  /** Strength 0..1. Default 0.7. */
  intensity?: number;
  /** Blob colours (CSS). Default warm orange, red, amber. */
  colors?: string[];
  /** Drift speed multiplier. Default 1. */
  speed?: number;
};

const ANCHORS = [
  {x: 0.05, y: 0.2, r: 0.55, phase: 0},
  {x: 0.95, y: 0.8, r: 0.5, phase: 2},
  {x: 0.5, y: 0.0, r: 0.4, phase: 4},
];

export const LightLeak: React.FC<LightLeakProps> = ({children, intensity = 0.7, colors = ['#ff8a3d', '#ff3d5a', '#ffd166'], speed = 1}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const t = (frame / 30) * speed;
  return (
    <AbsoluteFill>
      {children}
      <AbsoluteFill style={{mixBlendMode: 'screen', opacity: Math.min(1, Math.max(0, intensity)), pointerEvents: 'none'}}>
        {ANCHORS.map((a, i) => {
          const size = a.r * Math.max(width, height) * 2;
          const cx = (a.x + Math.sin(t * 0.7 + a.phase) * 0.12) * width;
          const cy = (a.y + Math.cos(t * 0.5 + a.phase) * 0.15) * height;
          const pulse = 0.75 + 0.25 * Math.sin(t * 1.3 + a.phase * 2);
          const color = colors[i % colors.length];
          return (
            <div
              key={i}
              style={{
                position: 'absolute',
                left: cx - size / 2,
                top: cy - size / 2,
                width: size,
                height: size,
                borderRadius: '50%',
                opacity: pulse,
                background: `radial-gradient(circle, ${color} 0%, transparent 65%)`,
              }}
            />
          );
        })}
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
