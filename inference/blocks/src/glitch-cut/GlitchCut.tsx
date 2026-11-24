// ReelBolt block "glitch-cut" (MIT). A scene change through horizontal slices that jitter sideways.
import React from 'react';
import {AbsoluteFill, interpolate, random, useCurrentFrame} from 'remotion';

export type GlitchCutProps = {
  from: React.ReactNode;
  to: React.ReactNode;
  /** Frame where the glitch starts. Default 0. */
  startFrame?: number;
  /** Glitch length in frames. Default 14. */
  durationInFrames?: number;
  /** Number of horizontal slices. Default 8. */
  slices?: number;
  /** Maximum sideways jitter in px. Default 90. */
  intensity?: number;
  /** Seed for the deterministic randomness. Default 'glitch'. */
  seed?: string;
};

export const GlitchCut: React.FC<GlitchCutProps> = ({
  from,
  to,
  startFrame = 0,
  durationInFrames = 14,
  slices = 8,
  intensity = 90,
  seed = 'glitch',
}) => {
  const frame = useCurrentFrame();
  const p = interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  if (p <= 0) return <AbsoluteFill>{from}</AbsoluteFill>;
  if (p >= 1) return <AbsoluteFill>{to}</AbsoluteFill>;
  const n = Math.min(24, Math.max(2, Math.round(slices)));
  const strength = Math.sin(p * Math.PI);
  return (
    <AbsoluteFill>
      {Array.from({length: n}, (_, i) => {
        // Each slice switches to the new scene at its own moment.
        const switchAt = 0.2 + 0.6 * random(`${seed}-switch-${i}`);
        const jitter = (random(`${seed}-${i}-${frame}`) - 0.5) * 2 * intensity * strength;
        return (
          <AbsoluteFill
            key={i}
            style={{
              clipPath: `inset(${(i / n) * 100}% 0 ${(1 - (i + 1) / n) * 100}% 0)`,
              transform: `translateX(${jitter}px) scale(1.15)`, // overscan hides the gaps the jitter would open
            }}
          >
            {p > switchAt ? to : from}
          </AbsoluteFill>
        );
      })}
    </AbsoluteFill>
  );
};
