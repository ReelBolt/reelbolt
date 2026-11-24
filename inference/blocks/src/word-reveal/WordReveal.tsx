// ReelBolt block "word-reveal" (MIT). Words rise out of a mask line by line of text, one after another.
import React from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig, spring} from 'remotion';

export type WordRevealProps = {
  text: string;
  color?: string;
  fontSize?: number;
  /** CSS font-family; must be an installed family. Default 'Montserrat, sans-serif'. */
  fontFamily?: string;
  fontWeight?: number;
  /** Frame where the first word starts. Default 0. */
  startFrame?: number;
  /** Frames between words. Default 4. */
  staggerFrames?: number;
};

export const WordReveal: React.FC<WordRevealProps> = ({
  text,
  color = '#ffffff',
  fontSize = 80,
  fontFamily = 'Montserrat, sans-serif',
  fontWeight = 800,
  startFrame = 0,
  staggerFrames = 4,
}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const words = text.split(' ').filter((w) => w.length > 0);
  return (
    <AbsoluteFill
      style={{
        justifyContent: 'center',
        alignItems: 'center',
        flexDirection: 'row',
        flexWrap: 'wrap',
        columnGap: fontSize * 0.28,
        padding: '0 10%',
        fontFamily,
        fontSize,
        fontWeight,
        color,
        lineHeight: 1.2,
        textAlign: 'center',
      }}
    >
      {words.map((word, i) => {
        const s = spring({frame: frame - startFrame - i * staggerFrames, fps, config: {damping: 200}, durationInFrames: 22});
        return (
          <span key={i} style={{display: 'inline-block', clipPath: 'inset(-20% -10% 0 -10%)', padding: '0.08em 0'}}>
            <span style={{display: 'inline-block', transform: `translateY(${(1 - s) * 70}%)`}}>{word}</span>
          </span>
        );
      })}
    </AbsoluteFill>
  );
};
