// ReelBolt block "text-mask-reveal" (MIT). Big letters filled with a sliding gradient, wiped in from the left.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type TextMaskRevealProps = {
  text: string;
  /** Gradient colours that fill the letters. Default a sunset set. */
  colors?: string[];
  fontSize?: number;
  /** CSS font-family; must be an installed family. Default 'Anton, sans-serif'. */
  fontFamily?: string;
  startFrame?: number;
  /** Reveal length in frames. Default 24. */
  durationInFrames?: number;
};

export const TextMaskReveal: React.FC<TextMaskRevealProps> = ({
  text,
  colors = ['#f97316', '#ec4899', '#8b5cf6', '#f97316'],
  fontSize = 140,
  fontFamily = 'Anton, sans-serif',
  startFrame = 0,
  durationInFrames = 24,
}) => {
  const frame = useCurrentFrame();
  const p = easeInOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  // The gradient keeps drifting after the reveal so the fill feels alive.
  const drift = (frame - startFrame) * 1.2;
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', padding: '0 8%', textAlign: 'center'}}>
      <div
        style={{
          fontFamily,
          fontSize,
          lineHeight: 1.5,
          padding: '0 0.1em',
          textTransform: 'uppercase',
          letterSpacing: 4,
          clipPath: `inset(-10% ${(1 - p) * 100}% -10% 0)`,
          backgroundImage: `linear-gradient(90deg, ${colors.join(', ')})`,
          backgroundSize: '300% 100%',
          backgroundPosition: `${drift}% 0`,
          WebkitBackgroundClip: 'text',
          backgroundClip: 'text',
          WebkitTextFillColor: 'transparent',
          color: 'transparent',
        }}
      >
        {text}
      </div>
    </AbsoluteFill>
  );
};
