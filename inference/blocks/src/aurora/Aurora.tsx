// ReelBolt block "aurora" (MIT). Soft, slowly waving aurora bands over a night-sky gradient.
import React from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig} from 'remotion';

export type AuroraProps = {
  children?: React.ReactNode;
  /** Band colours. Default green, teal, violet. */
  colors?: string[];
  /** Sky gradient [top, bottom]. */
  sky?: [string, string];
  /** Wave speed multiplier. Default 1. */
  speed?: number;
  /** Band opacity 0..1. Default 0.7. */
  intensity?: number;
};

export const Aurora: React.FC<AuroraProps> = ({children, colors = ['#34d399', '#22d3ee', '#a78bfa'], sky = ['#030712', '#0f1e3d'], speed = 1, intensity = 0.7}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const t = (frame / 30) * speed;
  return (
    <AbsoluteFill style={{background: `linear-gradient(180deg, ${sky[0]}, ${sky[1]})`, overflow: 'hidden'}}>
      {colors.map((c, i) => {
        const w = width * 1.4;
        const bandH = height * 0.35;
        const x = (width - w) / 2 + Math.sin(t * 0.6 + i * 2) * width * 0.08;
        const y = height * (0.12 + i * 0.14) + Math.sin(t * 0.8 + i * 1.3) * height * 0.04;
        const skew = Math.sin(t * 0.5 + i) * 8 - 6;
        return (
          <div
            key={i}
            style={{
              position: 'absolute',
              left: x,
              top: y,
              width: w,
              height: bandH,
              opacity: intensity * (0.7 + 0.3 * Math.sin(t + i * 1.9)),
              transform: `skewY(${skew}deg)`,
              background: `radial-gradient(ellipse at center, ${c} 0%, transparent 68%)`,
              filter: 'blur(14px)',
            }}
          />
        );
      })}
      {children}
    </AbsoluteFill>
  );
};
