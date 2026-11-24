// ReelBolt block "zoom-through" (MIT). The old scene rushes toward the camera while the new one settles in.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type ZoomThroughProps = {
  from: React.ReactNode;
  to: React.ReactNode;
  /** Frame where the zoom starts. Default 0. */
  startFrame?: number;
  /** Zoom length in frames. Default 20. */
  durationInFrames?: number;
  /** Scale the outgoing scene reaches. Default 3. */
  zoom?: number;
  /** Peak blur in px at the crossover. Default 12. */
  maxBlur?: number;
};

export const ZoomThrough: React.FC<ZoomThroughProps> = ({
  from,
  to,
  startFrame = 0,
  durationInFrames = 20,
  zoom = 3,
  maxBlur = 12,
}) => {
  const frame = useCurrentFrame();
  const raw = interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  if (raw <= 0) return <AbsoluteFill>{from}</AbsoluteFill>;
  if (raw >= 1) return <AbsoluteFill>{to}</AbsoluteFill>;
  const p = easeInOutCubic(raw);
  const blur = Math.sin(raw * Math.PI) * maxBlur;
  return (
    <AbsoluteFill style={{overflow: 'hidden'}}>
      <AbsoluteFill style={{transform: `scale(${1 + (zoom - 1) * p})`, opacity: 1 - p, filter: `blur(${blur}px)`}}>{from}</AbsoluteFill>
      <AbsoluteFill style={{transform: `scale(${0.6 + 0.4 * p})`, opacity: p, filter: `blur(${blur}px)`}}>{to}</AbsoluteFill>
    </AbsoluteFill>
  );
};
