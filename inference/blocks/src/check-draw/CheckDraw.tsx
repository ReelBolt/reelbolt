// ReelBolt block "check-draw" (MIT). Circle that draws itself, then a checkmark strokes in.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';
import {easeOutCubic} from '../_shared/motion.ts';

export type CheckDrawProps = {
  children?: React.ReactNode;
  /** Diameter in px. Default 360. */
  size?: number;
  /** Frame the animation starts. Default 5. */
  startFrame?: number;
  /** Frames for circle plus check. Default 36. */
  durationInFrames?: number;
  color?: string;
  strokeWidth?: number;
  /** Fill the circle with a tint once drawn. Default true. */
  fill?: boolean;
};

export const CheckDraw: React.FC<CheckDrawProps> = ({children, size = 360, startFrame = 5, durationInFrames = 36, color = '#22c55e', strokeWidth = 18, fill = true}) => {
  const frame = useCurrentFrame();
  const clamp = {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;
  const circle = easeOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames * 0.6], [0, 1], clamp));
  const check = easeOutCubic(interpolate(frame, [startFrame + durationInFrames * 0.5, startFrame + durationInFrames], [0, 1], clamp));
  const pop = 1 + 0.06 * Math.sin(Math.PI * interpolate(frame, [startFrame + durationInFrames, startFrame + durationInFrames + 10], [0, 1], clamp));
  const r = 50 - strokeWidth / (size / 100) / 2;
  return (
    <AbsoluteFill>
      {children}
      <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
        <svg width={size} height={size} viewBox="0 0 100 100" style={{transform: `scale(${pop})`}}>
          <circle cx={50} cy={50} r={r} fill={fill ? color : 'none'} fillOpacity={0.15 * circle} />
          <circle cx={50} cy={50} r={r} fill="none" stroke={color} strokeWidth={strokeWidth / (size / 100)} strokeLinecap="round" pathLength={1} strokeDasharray="1 1" strokeDashoffset={1 - circle} transform="rotate(-90 50 50)" />
          <path d="M 30 52 L 44 66 L 71 36" fill="none" stroke={color} strokeWidth={strokeWidth / (size / 100)} strokeLinecap="round" strokeLinejoin="round" pathLength={1} strokeDasharray="1 1" strokeDashoffset={1 - check} />
        </svg>
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
