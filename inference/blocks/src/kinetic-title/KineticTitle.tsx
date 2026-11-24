// ReelBolt block "kinetic-title" (MIT). A title that lands word by word on springs.
import React from 'react';
import {AbsoluteFill, interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {clamp01} from '../_shared/motion.ts';

export type KineticTitleProps = {
  /** The title text. Words are split on spaces. */
  text: string;
  /** Text colour. Default '#ffffff'. */
  color?: string;
  /** Font size in px. Default 120. */
  fontSize?: number;
  /** CSS font-family. Default 'Inter, sans-serif' (a bundled font). */
  fontFamily?: string;
  /** Frames between consecutive words. Default 5. */
  staggerFrames?: number;
};

export const KineticTitle: React.FC<KineticTitleProps> = ({
  text,
  color = '#ffffff',
  fontSize = 120,
  fontFamily = 'Inter, sans-serif',
  staggerFrames = 5,
}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const words = text.split(' ').filter((word) => word.length > 0);
  return (
    <AbsoluteFill
      style={{
        justifyContent: 'center',
        alignItems: 'center',
        flexDirection: 'row',
        flexWrap: 'wrap',
        gap: fontSize * 0.25,
        padding: '0 10%',
        fontFamily,
        fontSize,
        fontWeight: 800,
        lineHeight: 1.1,
        color,
        textAlign: 'center',
      }}
    >
      {words.map((word, index) => {
        const progress = spring({
          frame: frame - index * staggerFrames,
          fps,
          config: {damping: 16, stiffness: 140},
        });
        return (
          <span
            key={`${index}-${word}`}
            style={{
              display: 'inline-block',
              opacity: clamp01(progress),
              transform: `translateY(${interpolate(progress, [0, 1], [fontSize * 0.5, 0])}px)`,
            }}
          >
            {word}
          </span>
        );
      })}
    </AbsoluteFill>
  );
};
