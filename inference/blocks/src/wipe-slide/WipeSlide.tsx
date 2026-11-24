// ReelBolt block "wipe-slide" (MIT). A directional wipe between two scenes.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type WipeDirection = 'left' | 'right' | 'up' | 'down';

export type WipeSlideProps = {
  /** The scene that is leaving. */
  from: React.ReactNode;
  /** The scene that is arriving. */
  to: React.ReactNode;
  /** Frame (inside the surrounding Sequence) where the wipe starts. Default 0. */
  startFrame?: number;
  /** Wipe length in frames. Default 20. */
  durationInFrames?: number;
  /** Direction the revealing edge travels. Default 'left' (edge moves right to left). */
  direction?: WipeDirection;
};

// The arriving scene is revealed with a clip-path inset that shrinks to zero.
const insetFor = (direction: WipeDirection, remaining: number): string => {
  const pct = `${remaining * 100}%`;
  switch (direction) {
    case 'left':
      return `inset(0 0 0 ${pct})`;
    case 'right':
      return `inset(0 ${pct} 0 0)`;
    case 'up':
      return `inset(${pct} 0 0 0)`;
    case 'down':
      return `inset(0 0 ${pct} 0)`;
  }
};

export const WipeSlide: React.FC<WipeSlideProps> = ({
  from,
  to,
  startFrame = 0,
  durationInFrames = 20,
  direction = 'left',
}) => {
  const frame = useCurrentFrame();
  const progress = easeInOutCubic(
    interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {
      extrapolateLeft: 'clamp',
      extrapolateRight: 'clamp',
    }),
  );
  return (
    <AbsoluteFill>
      <AbsoluteFill>{from}</AbsoluteFill>
      <AbsoluteFill style={{clipPath: insetFor(direction, 1 - progress)}}>{to}</AbsoluteFill>
    </AbsoluteFill>
  );
};
