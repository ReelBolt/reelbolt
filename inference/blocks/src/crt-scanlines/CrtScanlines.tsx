// ReelBolt block "crt-scanlines" (MIT). Old-monitor look: scanlines, rounded screen, vignette and flicker.
import React from 'react';
import {AbsoluteFill, random, useCurrentFrame} from 'remotion';

export type CrtScanlinesProps = {
  children?: React.ReactNode;
  /** Overall strength 0..1. Default 0.5. */
  intensity?: number;
  /** Scanline pitch in px. Default 4. */
  lineHeight?: number;
  /** Screen corner radius in px (curvature feel). Default 40. */
  curvature?: number;
  /** Brightness flicker 0..1. Default 0.4. */
  flicker?: number;
  /** Seed for the flicker. Default 'crt'. */
  seed?: string;
};

export const CrtScanlines: React.FC<CrtScanlinesProps> = ({children, intensity = 0.5, lineHeight = 4, curvature = 40, flicker = 0.4, seed = 'crt'}) => {
  const frame = useCurrentFrame();
  const k = Math.min(1, Math.max(0, intensity));
  const flick = 1 - random(`${seed}-${frame}`) * 0.12 * flicker;
  return (
    <AbsoluteFill style={{background: '#000'}}>
      <AbsoluteFill style={{borderRadius: curvature, overflow: 'hidden', opacity: flick, filter: `contrast(${1 + 0.15 * k}) saturate(${1 + 0.2 * k})`}}>
        {children}
        <AbsoluteFill
          style={{
            backgroundImage: `repeating-linear-gradient(0deg, rgba(0,0,0,${0.45 * k}) 0px, rgba(0,0,0,${0.45 * k}) ${lineHeight / 2}px, transparent ${lineHeight / 2}px, transparent ${lineHeight}px)`,
          }}
        />
        <AbsoluteFill style={{background: `radial-gradient(ellipse at center, transparent 55%, rgba(0,0,0,${0.75 * k}) 100%)`}} />
        <AbsoluteFill style={{boxShadow: `inset 0 0 ${curvature * 2}px rgba(0,0,0,${0.6 * k})`, borderRadius: curvature}} />
      </AbsoluteFill>
    </AbsoluteFill>
  );
};
