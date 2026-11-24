// ReelBolt block "char-stagger" (MIT). Letters pop in one by one with a springy overshoot.
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';

export type CharStaggerProps = {
  text: string;
  color?: string;
  fontSize?: number;
  /** CSS font-family; must be an installed family. Default 'Space Grotesk, sans-serif'. */
  fontFamily?: string;
  fontWeight?: number;
  /** Frame where the first letter starts. Default 0. */
  startFrame?: number;
  /** Frames between letters. Default 2. */
  staggerFrames?: number;
};

export const CharStagger: React.FC<CharStaggerProps> = ({
  text,
  color = '#ffffff',
  fontSize = 110,
  fontFamily = 'Space Grotesk, sans-serif',
  fontWeight = 700,
  startFrame = 0,
  staggerFrames = 2,
}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const words = text.split(' ').filter((w) => w.length > 0);
  let index = 0;
  return (
    <AbsoluteFill
      style={{
        justifyContent: 'center',
        alignItems: 'center',
        flexDirection: 'row',
        flexWrap: 'wrap',
        columnGap: fontSize * 0.3,
        padding: '0 8%',
        fontFamily,
        fontSize,
        fontWeight,
        color,
        lineHeight: 1.2,
        textAlign: 'center',
      }}
    >
      {words.map((word, w) => (
        <span key={w} style={{display: 'inline-block', whiteSpace: 'nowrap'}}>
          {Array.from(word).map((ch, c) => {
            const s = spring({frame: frame - startFrame - index++ * staggerFrames, fps, config: {damping: 9, stiffness: 160}});
            return (
              <span
                key={c}
                style={{
                  display: 'inline-block',
                  opacity: interpolate(s, [0, 0.4], [0, 1], {extrapolateRight: 'clamp'}),
                  transform: `translateY(${(1 - s) * 0.6 * fontSize}px) scale(${0.6 + 0.4 * s})`,
                }}
              >
                {ch}
              </span>
            );
          })}
        </span>
      ))}
    </AbsoluteFill>
  );
};
