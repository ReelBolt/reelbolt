// ReelBolt block "ink-reveal" (MIT). The new scene spreads from a point like ink, through a wobbling clip-path.
import React from 'react';
import {AbsoluteFill, interpolate, random, useCurrentFrame} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type InkRevealProps = {
  from: React.ReactNode;
  to: React.ReactNode;
  /** Frame where the reveal starts. Default 0. */
  startFrame?: number;
  /** Reveal length in frames. Default 28. */
  durationInFrames?: number;
  /** Where the ink starts, in percent of the frame. Default 50 / 50. */
  originX?: number;
  originY?: number;
  /** Edge roughness, 0 (circle) to 1 (very ragged). Default 0.5. */
  roughness?: number;
  /** Seed for the blob shape. Default 'ink'. */
  seed?: string;
};

const POINTS = 56;

export const InkReveal: React.FC<InkRevealProps> = ({
  from,
  to,
  startFrame = 0,
  durationInFrames = 28,
  originX = 50,
  originY = 50,
  roughness = 0.5,
  seed = 'ink',
}) => {
  const frame = useCurrentFrame();
  const raw = interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  if (raw <= 0) return <AbsoluteFill>{from}</AbsoluteFill>;
  if (raw >= 1) return <AbsoluteFill>{to}</AbsoluteFill>;
  // Radius in percent of the frame width; 150 covers every corner even for off-centre origins.
  const radius = easeInOutCubic(raw) * 150;
  const phase = random(`${seed}-phase`) * Math.PI * 2;
  const pts = Array.from({length: POINTS}, (_, i) => {
    const a = (i / POINTS) * Math.PI * 2;
    const lobes = Math.sin(a * 3 + phase) * 0.12 + Math.sin(a * 7 + phase * 2) * 0.06;
    const grit = (random(`${seed}-${i}`) - 0.5) * 0.12;
    const r = radius * (1 + (lobes + grit) * roughness * 2);
    return `${originX + Math.cos(a) * r * 0.5625}% ${originY + Math.sin(a) * r}%`;
  });
  return (
    <AbsoluteFill>
      <AbsoluteFill>{from}</AbsoluteFill>
      <AbsoluteFill style={{clipPath: `polygon(${pts.join(',')})`}}>{to}</AbsoluteFill>
    </AbsoluteFill>
  );
};
