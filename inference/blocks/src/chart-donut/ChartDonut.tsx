// ReelBolt block "chart-donut" (MIT). Donut chart that sweeps open clockwise, with a total in the middle and a legend.
import React from 'react';
import {arc, format, pie} from 'd3';
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';
import {easeInOutCubic} from '../_shared/motion.ts';

export type DonutSlice = {label: string; value: number; color?: string};

export type ChartDonutProps = {
  /** Slices, clockwise from the top. */
  data: DonutSlice[];
  /** Small caption under the total. Default 'Total'. */
  centerLabel?: string;
  /** Text colour. Default: '#e2e8f0' (theme dark) or '#0f172a' (theme light). */
  textColor?: string;
  /** 'dark' (default): light text for dark backdrops. 'light': dark text for light backdrops. Ignored for text if textColor is set. */
  theme?: 'dark' | 'light';
  /** Colours used for slices without their own. */
  palette?: string[];
  /** Frame the sweep starts. Default 5. */
  startFrame?: number;
  /** Sweep length in frames. Default 45. */
  durationInFrames?: number;
  /** CSS font-family (installed font). Default 'Inter, sans-serif'. */
  fontFamily?: string;
};

const PALETTE = ['#38bdf8', '#a78bfa', '#34d399', '#fbbf24', '#fb7185'];

export const ChartDonut: React.FC<ChartDonutProps> = ({
  data,
  centerLabel = 'Total',
  textColor,
  theme = 'dark',
  palette = PALETTE,
  startFrame = 5,
  durationInFrames = 45,
  fontFamily = 'Inter, sans-serif',
}) => {
  const frame = useCurrentFrame();
  const {width, height} = useVideoConfig();
  const p = easeInOutCubic(interpolate(frame, [startFrame, startFrame + durationInFrames], [0, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}));
  const total = data.reduce((s, d) => s + d.value, 0);
  const portrait = height > width;
  const outer = portrait ? width * 0.3 : Math.min(height * 0.34, width * 0.2);
  const cx = portrait ? width / 2 : width * 0.33;
  const cy = portrait ? height * 0.34 : height / 2;
  const slices = pie<DonutSlice>().value((d) => d.value).sort(null).startAngle(0).endAngle(Math.PI * 2 * p)(data);
  const path = arc<(typeof slices)[number]>().innerRadius(outer * 0.62).outerRadius(outer).cornerRadius(6).padAngle(0.012);
  const fs = Math.round(Math.min(height, width) * 0.04);
  const pct = format('.0%');
  return (
    <AbsoluteFill style={{fontFamily, color: textColor ?? (theme === 'light' ? '#0f172a' : '#e2e8f0')}}>
      <svg width={width} height={height} style={{position: 'absolute', inset: 0}}>
        <g transform={`translate(${cx},${cy})`}>
          {slices.map((s, i) => (
            <path key={s.data.label} d={path(s) ?? ''} fill={s.data.color ?? palette[i % palette.length]} />
          ))}
        </g>
      </svg>
      <div style={{position: 'absolute', left: cx - outer * 0.55, width: outer * 1.1, top: cy - fs * 1.6, textAlign: 'center'}}>
        <div style={{fontSize: fs * 2, fontWeight: 800, lineHeight: 1.1}}>{format(',.0f')(total * p)}</div>
        <div style={{fontSize: fs * 0.8, opacity: 0.7}}>{centerLabel}</div>
      </div>
      <div style={{position: 'absolute', left: portrait ? width * 0.1 : width * 0.6, right: portrait ? width * 0.1 : width * 0.07, top: portrait ? cy + outer + fs * 2 : cy - data.length * fs * 1.1, display: 'flex', flexDirection: 'column', gap: fs * 0.7}}>
        {data.map((d, i) => (
          <div key={d.label} style={{display: 'flex', alignItems: 'center', gap: fs * 0.6, fontSize: fs, opacity: interpolate(p, [i / data.length, (i + 1) / data.length], [0.25, 1], {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'})}}>
            <span style={{width: fs * 0.8, height: fs * 0.8, borderRadius: 4, background: d.color ?? palette[i % palette.length]}} />
            <span style={{flex: 1, fontWeight: 600}}>{d.label}</span>
            <span style={{fontWeight: 800}}>{pct(d.value / total)}</span>
          </div>
        ))}
      </div>
    </AbsoluteFill>
  );
};
