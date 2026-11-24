// ReelBolt block "flash-cut" (MIT). A hard cut hidden behind a quick colour flash.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';

export type FlashCutProps = {
  from: React.ReactNode;
  to: React.ReactNode;
  /** Frame where the flash starts. Default 0. */
  startFrame?: number;
  /** Flash length in frames; the cut happens in the middle. Default 10. */
  durationInFrames?: number;
  /** Flash colour. Default '#ffffff'. */
  color?: string;
};

export const FlashCut: React.FC<FlashCutProps> = ({
  from,
  to,
  startFrame = 0,
  durationInFrames = 10,
  color = '#ffffff',
}) => {
  const frame = useCurrentFrame();
  const mid = startFrame + durationInFrames / 2;
  const opacity = interpolate(frame, [startFrame, mid, startFrame + durationInFrames], [0, 1, 0], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  return (
    <AbsoluteFill>
      <AbsoluteFill>{frame < mid ? from : to}</AbsoluteFill>
      <AbsoluteFill style={{background: color, opacity}} />
    </AbsoluteFill>
  );
};
