// ReelBolt block "film-grain" (MIT). Animated film grain overlay (SVG feTurbulence, re-seeded every few frames).
import React from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig} from 'remotion';

export type FilmGrainProps = {
  /** Content the grain sits on top of. */
  children?: React.ReactNode;
  /** Grain strength 0..1. Default 0.35. */
  intensity?: number;
  /** Grain size: higher is finer. Default 0.8. */
  frequency?: number;
  /** Frames each grain pattern is held (12 fps look at 30 fps = 2). Default 2. */
  holdFrames?: number;
};

export const FilmGrain: React.FC<FilmGrainProps> = ({children, intensity = 0.35, frequency = 0.8, holdFrames = 2}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const seed = Math.floor(frame / Math.max(1, holdFrames)) % 997;
  return (
    <AbsoluteFill>
      {children}
      <AbsoluteFill style={{mixBlendMode: 'overlay', opacity: Math.min(1, Math.max(0, intensity)), pointerEvents: 'none'}}>
        <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
          <filter id="reelbolt-grain" x="0" y="0" width="100%" height="100%">
            <feTurbulence type="fractalNoise" baseFrequency={frequency} numOctaves={2} seed={seed} stitchTiles="stitch" />
            <feColorMatrix type="saturate" values="0" />
          </filter>
          <rect width={width} height={height} filter="url(#reelbolt-grain)" />
        </svg>
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
