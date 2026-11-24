// ReelBolt block "chart-line-draw" (MIT). A line chart that draws itself left to right, with dots and an area fill.
import React from 'react';
import {area, curveMonotoneX, line, scaleLinear} from 'd3';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type ChartLineDrawProps = {
  /** Y values, evenly spaced along x. */
  values: number[];
  /** X labels, same length as values (only first, middle and last are shown). Default none. */
  labels?: string[];
  /** Heading above the chart. Default none. */
  title?: string;
  /** Line colour. Default '#34d399'. */
  color?: string;
  /** Text colour. Default: '#e2e8f0' (theme dark) or '#0f172a' (theme light). */
  textColor?: string;
  /** 'dark' (default): light text for dark backdrops. 'light': dark text for light backdrops. Ignored for text if textColor is set. */
  theme?: 'dark' | 'light';
  /** Frame the drawing starts. Default 5. */
  startFrame?: number;
  /** Drawing length in frames. Default 50. */
  durationInFrames?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

export const ChartLineDraw: React.FC<ChartLineDrawProps> = ({
  values,
  labels = [],
  title,
  color = '#34d399',
  textColor,
  theme = 'dark',
  startFrame = 5,
  durationInFrames = 50,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const fs = Math.round(Math.min(height, width) * 0.035);
  const left = width * 0.09;
  const right = width * 0.89;
  const top = height * 0.07 + (title ? fs * 3 : 0);
  const bottom = height * 0.85;
  const lo = Math.min(...values);
  const hi = Math.max(...values);
  const x = scaleLinear().domain([0, values.length - 1]).range([left, right]);
  const y = scaleLinear().domain([Math.min(0, lo), hi]).nice().range([bottom, top]);
  const d = line<number>().x((_, i) => x(i)).y((v) => y(v)).curve(curveMonotoneX)(values) ?? '';
  const fill = area<number>().x((_, i) => x(i)).y0(bottom).y1((v) => y(v)).curve(curveMonotoneX)(values) ?? '';
  const p = easeInOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const reveal = left + (right - left) * p;
  const shown = [0, Math.floor((values.length - 1) / 2), values.length - 1];
  return (
    <AbsoluteFill style={{fontFamily, color: textColor ?? (theme === 'light' ? '#0f172a' : '#e2e8f0')}}>
      {title ? <div style={{position: 'absolute', left, top: height * 0.07, fontSize: fs * 1.5, fontWeight: 800}}>{title}</div> : null}
      <svg width={width} height={height} style={{position: 'absolute', inset: 0}}>
        <defs>
          <clipPath id="line-reveal"><rect x={0} y={0} width={reveal} height={height} /></clipPath>
          <linearGradient id="line-area" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={color} stopOpacity={0.35} />
            <stop offset="100%" stopColor={color} stopOpacity={0} />
          </linearGradient>
        </defs>
        {y.ticks(4).map((t) => (
          <line key={t} x1={left} x2={right} y1={y(t)} y2={y(t)} stroke={theme === 'light' ? 'rgba(15,23,42,0.15)' : 'rgba(255,255,255,0.12)'} strokeWidth={1} />
        ))}
        <g clipPath="url(#line-reveal)">
          <path d={fill} fill="url(#line-area)" />
          <path d={d} fill="none" stroke={color} strokeWidth={Math.max(4, height * 0.006)} strokeLinecap="round" strokeLinejoin="round" />
        </g>
        {values.map((v, i) => (x(i) <= reveal ? <circle key={i} cx={x(i)} cy={y(v)} r={height * 0.009} fill={color} /> : null))}
      </svg>
      {shown.map((i) =>
        labels[i] ? (
          <div key={i} style={{position: 'absolute', top: bottom + fs * 0.6, left: x(i) - 100, width: 200, textAlign: 'center', fontSize: fs, opacity: 0.8}}>
            {labels[i]}
          </div>
        ) : null,
      )}
    </AbsoluteFill>
  );
};
