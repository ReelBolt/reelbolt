// ReelBolt block "bokeh" (MIT). Floating soft light circles, seeded so every render is identical.
import React from 'react';
import {AbsoluteFill, random, useCurrentFrame, useVideoConfig} from 'remotion';

export type BokehProps = {
  children?: React.ReactNode;
  /** Number of circles (keep under 40). Default 18. */
  count?: number;
  /** Overall opacity 0..1. Default 0.6. */
  intensity?: number;
  /** Circle colours. Default warm whites and amber. */
  colors?: string[];
  /** Largest circle diameter in px. Default 220. */
  maxSize?: number;
  /** Rise speed in px per second. Default 30. */
  speed?: number;
  /** Seed. Default 'bokeh'. */
  seed?: string;
};

export const Bokeh: React.FC<BokehProps> = ({children, count = 18, intensity = 0.6, colors = ['#fff3d6', '#ffd28a', '#ffffff', '#ffb36b'], maxSize = 220, speed = 30, seed = 'bokeh'}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const n = Math.min(40, Math.max(0, count));
  return (
    <AbsoluteFill>
      {children}
      <AbsoluteFill style={{mixBlendMode: 'screen', opacity: Math.min(1, Math.max(0, intensity)), pointerEvents: 'none', overflow: 'hidden'}}>
        {Array.from({length: n}, (_, i) => {
          const size = maxSize * (0.3 + random(`${seed}-s-${i}`) * 0.7);
          const span = height + size * 2;
          const rise = (random(`${seed}-y-${i}`) * span + (frame / 30) * speed * (0.5 + random(`${seed}-v-${i}`))) % span;
          const y = height + size - rise;
          const x = random(`${seed}-x-${i}`) * width + Math.sin(frame / 40 + i) * 24;
          const a = 0.25 + random(`${seed}-a-${i}`) * 0.5;
          const color = colors[i % colors.length];
          return (
            <div
              key={i}
              style={{
                position: 'absolute',
                left: x - size / 2,
                top: y - size / 2,
                width: size,
                height: size,
                borderRadius: '50%',
                opacity: a,
                background: `radial-gradient(circle, ${color} 0%, ${color} 35%, transparent 70%)`,
                boxShadow: `inset 0 0 0 2px rgba(255,255,255,0.12)`,
              }}
            />
          );
        })}
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
