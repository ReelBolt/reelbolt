// ReelBolt block "whip-blur" (MIT). A fast whip pan: both scenes slide sideways under heavy blur.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type WhipBlurProps = {
  from: React.ReactNode;
  to: React.ReactNode;
  /** Frame where the whip starts. Default 0. */
  startFrame?: number;
  /** Whip length in frames. Default 14. */
  durationInFrames?: number;
  /** 'left' pans the camera to the right (scenes move left). Default 'left'. */
  direction?: 'left' | 'right';
  /** Peak blur in px. Default 28. */
  maxBlur?: number;
};

export const WhipBlur: React.FC<WhipBlurProps> = ({
  from,
  to,
  startFrame = 0,
  durationInFrames = 14,
  direction = 'left',
  maxBlur = 28,
}) => {
  const frame = useCurrentFrame();
  const raw = interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  if (raw <= 0) return <AbsoluteFill>{from}</AbsoluteFill>;
  if (raw >= 1) return <AbsoluteFill>{to}</AbsoluteFill>;
  const p = easeInOutCubic(raw);
  const s = direction === 'left' ? -1 : 1;
  const filter = `blur(${Math.sin(raw * Math.PI) * maxBlur}px)`;
  return (
    <AbsoluteFill style={{overflow: 'hidden'}}>
      <AbsoluteFill style={{transform: `translateX(${s * p * 100}%)`, filter}}>{from}</AbsoluteFill>
      <AbsoluteFill style={{transform: `translateX(${-s * (1 - p) * 100}%)`, filter}}>{to}</AbsoluteFill>
    </AbsoluteFill>
  );
};
