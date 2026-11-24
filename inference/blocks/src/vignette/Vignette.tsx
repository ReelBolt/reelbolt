// ReelBolt block "vignette" (MIT). Darkened (or tinted) edges that focus the eye on the centre.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';

export type VignetteProps = {
  children?: React.ReactNode;
  /** Edge darkness 0..1. Default 0.6. */
  intensity?: number;
  /** Where the clear centre ends, 0..100 (% of the radius). Default 45. */
  innerStop?: number;
  /** Edge colour. Default '#000'. */
  color?: string;
  /** Slow breathing amount 0..1 (0 = static). Default 0. */
  pulse?: number;
  /** Frames per breath. Default 90. */
  pulseFrames?: number;
};

export const Vignette: React.FC<VignetteProps> = ({children, intensity = 0.6, innerStop = 45, color = '#000', pulse = 0, pulseFrames = 90}) => {
  const frame = useCurrentFrame();
  const breath = Math.sin((frame / Math.max(1, pulseFrames)) * Math.PI * 2) * 0.5 * pulse;
  const alpha = interpolate(intensity + breath * 0.3, [0, 1], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  return (
    <AbsoluteFill>
      {children}
      <AbsoluteFill
        style={{
          pointerEvents: 'none',
          background: `radial-gradient(ellipse at center, transparent ${innerStop}%, ${color} 130%)`,
          opacity: alpha,
        }}
      />
    </AbsoluteFill>
  );
};
