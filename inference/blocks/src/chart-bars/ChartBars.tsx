// ReelBolt block "chart-bars" (MIT). Horizontal bar chart: bars grow in one after another (d3 scales for geometry).
import React from 'react';
import {scaleBand, scaleLinear, format} from 'd3';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {easeOutCubic} from '../_shared/motion.ts';

export type BarDatum = {label: string; value: number};

export type ChartBarsProps = {
  /** Bars, top to bottom. */
  data: BarDatum[];
  /** Heading above the chart. Default none. */
  title?: string;
  /** Bar colour. Default '#38bdf8'. */
  color?: string;
  /** Text colour. Default: '#e2e8f0' (theme dark) or '#0f172a' (theme light). */
  textColor?: string;
  /** 'dark' (default): light text for dark backdrops. 'light': dark text for light backdrops. Ignored for text if textColor is set. */
  theme?: 'dark' | 'light';
  /** d3-format string for the value labels. Default ',.0f'. */
  valueFormat?: string;
  /** Frame the first bar starts growing. Default 5. */
  startFrame?: number;
  /** Frames between bars. Default 6. */
  staggerFrames?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

export const ChartBars: React.FC<ChartBarsProps> = ({
  data,
  title,
  color = '#38bdf8',
  textColor,
  theme = 'dark',
  valueFormat = ',.0f',
  startFrame = 5,
  staggerFrames = 6,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const fs = Math.round(Math.min(height, width) * 0.04);
  const padX = width * 0.07;
  const top = height * 0.07 + (title ? fs * 2.2 : 0);
  const labelW = width * 0.18;
  const valueW = width * 0.09;
  const x = scaleLinear().domain([0, Math.max(...data.map((d) => d.value), 1)]).range([0, width - padX * 2 - labelW - valueW]);
  const y = scaleBand<string>().domain(data.map((d) => d.label)).range([top, height * 0.93]).padding(0.3);
  const fmt = format(valueFormat);
  return (
    <AbsoluteFill style={{fontFamily, color: textColor ?? (theme === 'light' ? '#0f172a' : '#e2e8f0')}}>
      {title ? <div style={{position: 'absolute', left: padX, top: height * 0.07, fontSize: fs * 1.4, fontWeight: 800}}>{title}</div> : null}
      {data.map((d, i) => {
        const p = easeOutCubic(interpolate(frame, [startFrame + i * staggerFrames, startFrame + i * staggerFrames + 24], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
        const bh = y.bandwidth();
        const top0 = y(d.label) ?? 0;
        return (
          <React.Fragment key={d.label}>
            <div style={{position: 'absolute', left: padX, width: labelW - 16, top: top0, height: bh, display: 'flex', alignItems: 'center', fontSize: fs, fontWeight: 600, opacity: p}}>
              {d.label}
            </div>
            <div style={{position: 'absolute', left: padX + labelW, top: top0, height: bh, width: x(d.value) * p, background: color, borderRadius: bh * 0.2}} />
            <div style={{position: 'absolute', left: padX + labelW + x(d.value) * p + 14, top: top0, height: bh, display: 'flex', alignItems: 'center', fontSize: fs, fontWeight: 800, opacity: p}}>
              {fmt(d.value * p)}
            </div>
          </React.Fragment>
        );
      })}
    </AbsoluteFill>
  );
};
