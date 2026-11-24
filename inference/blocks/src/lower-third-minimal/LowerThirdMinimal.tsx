// ReelBolt block "lower-third-minimal" (MIT). Just text over a thin line that draws in; no box.
import React from 'react';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {easeOutCubic} from '../_shared/motion.ts';

export type LowerThirdMinimalProps = {
  name: string;
  subtitle?: string;
  /** Line colour. Default '#ffffff'. */
  lineColor?: string;
  textColor?: string;
  /** CSS font-family; must be an installed family. Default 'Inter, sans-serif'. */
  fontFamily?: string;
  startFrame?: number;
  /** Frame where it leaves (fades over 10 frames); omit to stay. */
  endFrame?: number;
};

export const LowerThirdMinimal: React.FC<LowerThirdMinimalProps> = ({
  name,
  subtitle = '',
  lineColor = '#ffffff',
  textColor = '#ffffff',
  fontFamily = 'Inter, sans-serif',
  startFrame = 0,
  endFrame,
}) => {
  const frame = useCurrentFrame();
  const {height} = useVideoConfig();
  const t = (from: number, len: number) =>
    easeOutCubic(interpolate(frame, [startFrame + from, startFrame + from + len], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const line = t(0, 14);
  const text = t(6, 16);
  const out = endFrame === undefined ? 1 : interpolate(frame, [endFrame, endFrame + 10], [1, 0], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'});
  return (
    <AbsoluteFill style={{justifyContent: 'flex-end', alignItems: 'flex-start', padding: `0 5% ${height * 0.07}px 5%`, fontFamily, opacity: out}}>
      <div style={{width: 360, maxWidth: '60%'}}>
        <div style={{height: 3, background: lineColor, transformOrigin: 'left', transform: `scaleX(${line})`, marginBottom: 16}} />
        <div style={{color: textColor, opacity: text, transform: `translateY(${(1 - text) * 14}px)`, whiteSpace: 'nowrap'}}>
          <div style={{fontSize: 40, fontWeight: 600, letterSpacing: 0.5}}>{name}</div>
          {subtitle ? <div style={{fontSize: 24, fontWeight: 400, opacity: 0.8, marginTop: 4}}>{subtitle}</div> : null}
        </div>
      </div>
    </AbsoluteFill>
  );
};
