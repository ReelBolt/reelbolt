// ReelBolt block "wipe-edges" (MIT). A wipe whose moving edge carries a coloured bar.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type WipeEdgesProps = {
  from: React.ReactNode;
  to: React.ReactNode;
  /** Frame where the wipe starts. Default 0. */
  startFrame?: number;
  /** Wipe length in frames. Default 20. */
  durationInFrames?: number;
  /** 'left' moves the edge right to left, 'right' left to right. Default 'left'. */
  direction?: 'left' | 'right';
  /** Colour of the edge bars. Default '#facc15'. */
  edgeColor?: string;
  /** Width of the main edge bar in px. Default 24. */
  edgeWidth?: number;
};

export const WipeEdges: React.FC<WipeEdgesProps> = ({
  from,
  to,
  startFrame = 0,
  durationInFrames = 20,
  direction = 'left',
  edgeColor = '#facc15',
  edgeWidth = 24,
}) => {
  const frame = useCurrentFrame();
  const raw = interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
  });
  const p = easeInOutCubic(raw);
  const active = raw > 0 && raw < 1;
  // Position of the edge as a percentage from the left of the frame.
  const edge = direction === 'left' ? (1 - p) * 100 : p * 100;
  const clip = direction === 'left' ? `inset(0 0 0 ${edge}%)` : `inset(0 ${100 - edge}% 0 0)`;
  const sign = direction === 'left' ? 1 : -1;
  return (
    <AbsoluteFill>
      <AbsoluteFill>{from}</AbsoluteFill>
      <AbsoluteFill style={{clipPath: clip}}>{to}</AbsoluteFill>
      {active
        ? [
            {w: edgeWidth, lag: 0, o: 1},
            {w: edgeWidth / 3, lag: 3, o: 0.7},
            {w: edgeWidth / 6, lag: 6, o: 0.4},
          ].map((bar, i) => (
            <div
              key={i}
              style={{
                position: 'absolute',
                top: 0,
                bottom: 0,
                width: bar.w,
                left: `calc(${edge}% + ${sign * bar.lag * 4}px - ${bar.w / 2}px)`,
                background: edgeColor,
                opacity: bar.o,
              }}
            />
          ))
        : null}
    </AbsoluteFill>
  );
};
