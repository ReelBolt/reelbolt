// ReelBolt block "sparkline" (MIT). A compact trend card: label, latest value and a self-drawing line with an end dot.
import React from 'react';
import {curveMonotoneX, format, line, scaleLinear} from 'd3';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type SparklineProps = {
  /** Y values, evenly spaced. */
  values: number[];
  /** Metric name. Default none. */
  label?: string;
  /** d3-format string for the latest value. Default ',.0f'. */
  valueFormat?: string;
  /** Line colour. Default '#f472b6'. */
  color?: string;
  /** Text colour. Default '#ffffff'. */
  textColor?: string;
  /** Card background. Default 'rgba(15,23,42,0.9)'. */
  cardColor?: string;
  /** Frame the drawing starts. Default 5. */
  startFrame?: number;
  /** Drawing length in frames. Default 40. */
  durationInFrames?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

export const Sparkline: React.FC<SparklineProps> = ({
  values,
  label,
  valueFormat = ',.0f',
  color = '#f472b6',
  textColor = '#ffffff',
  cardColor = 'rgba(15,23,42,0.9)',
  startFrame = 5,
  durationInFrames = 40,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const cw = width * 0.5;
  const ch = height * 0.4;
  const pad = ch * 0.1;
  const gx = cw - pad * 2;
  const gh = ch * 0.45;
  const x = scaleLinear().domain([0, values.length - 1]).range([0, gx]);
  const y = scaleLinear().domain([Math.min(...values), Math.max(...values)]).range([gh, 0]);
  const d = line<number>().x((_, i) => x(i)).y((v) => y(v)).curve(curveMonotoneX)(values) ?? '';
  const p = easeInOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const idx = p * (values.length - 1);
  const lo = Math.floor(idx);
  const current = values[lo] + ((values[Math.min(lo + 1, values.length - 1)] - values[lo]) * (idx - lo));
  const last = values.length - 1;
  const fs = ch * 0.12;
  return (
    <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', fontFamily}}>
      <div style={{width: cw, height: ch, borderRadius: ch * 0.08, background: cardColor, color: textColor, padding: pad, boxSizing: 'border-box', display: 'flex', flexDirection: 'column', justifyContent: 'space-between'}}>
        <div style={{display: 'flex', justifyContent: 'space-between', alignItems: 'baseline'}}>
          <span style={{fontSize: fs, opacity: 0.7}}>{label ?? ''}</span>
          <span style={{fontSize: fs * 1.7, fontWeight: 800, fontVariantNumeric: 'tabular-nums'}}>{format(valueFormat)(current)}</span>
        </div>
        <svg width={gx} height={gh} style={{overflow: 'visible'}}>
          <path d={d} fill="none" stroke={color} strokeWidth={ch * 0.02} strokeLinecap="round" strokeLinejoin="round" pathLength={1} strokeDasharray={1} strokeDashoffset={1 - p} />
          {p > 0.98 ? <circle cx={x(last)} cy={y(values[last])} r={ch * 0.03} fill={color} /> : null}
        </svg>
      </div>
    </AbsoluteFill>
  );
};
