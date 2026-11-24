// ReelBolt block "progress-ring" (MIT). A circular progress ring that fills to a percentage while the number counts up.
import React from 'react';
import {arc} from 'd3';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {easeOutCubic} from '../_shared/motion.ts';

export type ProgressRingProps = {
  /** Percentage to fill, 0 to 100. */
  percent: number;
  /** Label under the number. Default none. */
  label?: string;
  /** Ring colour. Default '#22d3ee'. */
  color?: string;
  /** Track colour. Default 'rgba(255,255,255,0.14)'. */
  trackColor?: string;
  /** Text colour. Default '#ffffff'. */
  textColor?: string;
  /** Frame the fill starts. Default 5. */
  startFrame?: number;
  /** Fill length in frames. Default 45. */
  durationInFrames?: number;
  /** CSS font-family (installed font). Default 'Space Grotesk, sans-serif'. */
  fontFamily?: string;
};

export const ProgressRing: React.FC<ProgressRingProps> = ({
  percent,
  label,
  color = '#22d3ee',
  trackColor = 'rgba(255,255,255,0.14)',
  textColor = '#ffffff',
  startFrame = 5,
  durationInFrames = 45,
  fontFamily = 'Space Grotesk, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const p = easeOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const value = Math.max(0, Math.min(100, percent)) * p;
  const outer = height * 0.36;
  const base = arc().innerRadius(outer * 0.82).outerRadius(outer).cornerRadius(outer * 0.09);
  const track = base({startAngle: 0, endAngle: Math.PI * 2} as never) ?? '';
  const fill = value > 0.3 ? base({startAngle: 0, endAngle: (value / 100) * Math.PI * 2} as never) ?? '' : '';
  const fs = outer * 0.5;
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', fontFamily, color: textColor}}>
      <svg width={width} height={height} style={{position: 'absolute', inset: 0}}>
        <g transform={`translate(${width / 2},${height / 2})`}>
          <path d={track} fill={trackColor} />
          <path d={fill} fill={color} />
        </g>
      </svg>
      <div style={{textAlign: 'center'}}>
        <div style={{fontSize: fs, fontWeight: 700, lineHeight: 1, fontVariantNumeric: 'tabular-nums'}}>{Math.round(value)}%</div>
        {label ? <div style={{fontSize: fs * 0.3, marginTop: fs * 0.1, opacity: 0.75}}>{label}</div> : null}
      </div>
    </AbsoluteFill>
  );
};
